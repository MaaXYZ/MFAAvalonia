using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using MaaFramework.Binding.Interop.Native;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.UserControls;
using MFAAvalonia.Views.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using SharpCompress.Archives;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.Enums;
using SukiUI.Toasts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;
#pragma warning  disable CS1998 // 此异步方法缺少 "await" 运算符，将以同步方式运行。
#pragma warning  disable CS4014 // 由于此调用不会等待，因此在此调用完成之前将会继续执行当前方法。
public static class VersionChecker
{
    private static bool shouldShowToast = false;
    private const string ResourceUpdateDebugEnv = "MFA_DEBUG_UPDATE_RESOURCE";
    private const string ResourceUpdateDryRunEnv = "MFA_DEBUG_UPDATE_RESOURCE_DRY_RUN";
    private const string ResourceUpdateKeepArtifactsEnv = "MFA_DEBUG_UPDATE_RESOURCE_KEEP";

    public enum VersionType
    {
        Alpha = 0,
        Beta = 1,
        Stable = 2
    }

    private static readonly ConcurrentQueue<ValueType.MFATask> Queue = new();
    private static readonly SemaphoreSlim CheckExecutionLock = new(1, 1);
    private static readonly object StartupCheckLock = new();
    private static Task? _startupCheckTask;
    private static int _restartPending;

    public static bool IsRestartPending => Volatile.Read(ref _restartPending) != 0;

    public sealed record LocalResourcePackageInspection(
        bool HasInterface,
        bool IsValid,
        string ResourceName,
        string CurrentVersion,
        string PackageVersion,
        string ErrorMessage);

    public static bool IsSupportedLocalResourcePackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return false;

        var fileName = Path.GetFileName(packagePath);
        return fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<LocalResourcePackageInspection> InspectLocalResourcePackageAsync(string packagePath) =>
        Task.Run(() => InspectLocalResourcePackage(packagePath));

    private static LocalResourcePackageInspection InspectLocalResourcePackage(string packagePath)
    {
        var currentName = MaaProcessor.Interface?.Name?.Trim() ?? string.Empty;
        var currentRid = MaaProcessor.Interface?.RID?.Trim() ?? string.Empty;
        var currentVersion = MaaProcessor.Interface?.Version?.Trim() ?? string.Empty;

        try
        {
            if (!File.Exists(packagePath) || !IsSupportedLocalResourcePackage(packagePath))
                return new(false, false, string.Empty, currentVersion, string.Empty, string.Empty);

            using var archive = ArchiveFactory.Open(packagePath);
            var entries = archive.Entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => new
                {
                    Entry = entry,
                    Path = NormalizeArchiveEntryPath(entry.Key)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                .ToList();

            var interfaceEntry = entries
                .Where(item => IsSupportedPackageInterfacePath(item.Path))
                .OrderBy(item => item.Path.Count(character => character == '/'))
                .FirstOrDefault();
            if (interfaceEntry == null)
                return new(false, false, string.Empty, currentVersion, string.Empty, string.Empty);

            var packageRoot = interfaceEntry.Path[..^"interface.json".Length];
            var hasResourceContent = entries.Any(item =>
                item.Path.StartsWith($"{packageRoot}resource/", StringComparison.OrdinalIgnoreCase));
            var hasIncrementalManifest = entries.Any(item =>
                item.Path.Equals($"{packageRoot}changes.json", StringComparison.OrdinalIgnoreCase));
            if (!hasResourceContent && !hasIncrementalManifest)
            {
                return new(true, false, string.Empty, currentVersion, string.Empty,
                    LangKeys.DroppedResourcePackageInvalid.ToLocalization());
            }

            JObject packageInterface;
            try
            {
                using var entryStream = interfaceEntry.Entry.OpenEntryStream();
                using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
                packageInterface = JObject.Parse(reader.ReadToEnd(), new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Load
                });
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"拖拽资源包 interface.json 解析失败：文件={packagePath}，原因={ex.Message}");
                return new(true, false, string.Empty, currentVersion, string.Empty,
                    LangKeys.DroppedResourcePackageInvalid.ToLocalization());
            }

            var packageName = packageInterface["name"]?.ToString().Trim() ?? string.Empty;
            var packageRid = packageInterface["mirrorchyan_rid"]?.ToString().Trim() ?? string.Empty;
            var packageVersion = packageInterface["version"]?.ToString().Trim() ?? string.Empty;
            var displayName = !string.IsNullOrWhiteSpace(packageName)
                ? packageName
                : !string.IsNullOrWhiteSpace(currentName)
                    ? currentName
                    : packageRid;

            if (!string.IsNullOrWhiteSpace(currentRid) && !string.IsNullOrWhiteSpace(packageRid))
            {
                if (!packageRid.Equals(currentRid, StringComparison.OrdinalIgnoreCase))
                {
                    return new(true, false, displayName, currentVersion, packageVersion,
                        LangKeys.DroppedResourceRidMismatch.ToLocalizationFormatted(false, packageRid, currentRid));
                }
            }
            else if (string.IsNullOrWhiteSpace(currentName)
                     || string.IsNullOrWhiteSpace(packageName)
                     || !packageName.Equals(currentName, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, false, displayName, currentVersion, packageVersion,
                    LangKeys.DroppedResourceNameMismatch.ToLocalizationFormatted(false, packageName, currentName));
            }

            if (!TryCompareVersions(packageVersion, currentVersion, out var versionComparison))
            {
                return new(true, false, displayName, currentVersion, packageVersion,
                    LangKeys.DroppedResourcePackageInvalid.ToLocalization());
            }

            if (versionComparison < 0)
            {
                return new(true, false, displayName, currentVersion, packageVersion,
                    LangKeys.DroppedResourceVersionTooLow.ToLocalizationFormatted(false, packageVersion, currentVersion));
            }

            // TODO: Use the currently loaded MaaFramework to preflight package resources and agents.
            return new(true, true, displayName, currentVersion, packageVersion, string.Empty);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"检查拖拽资源包失败：文件={packagePath}，原因={ex.Message}", ex);
            return new(false, false, string.Empty, currentVersion, string.Empty, string.Empty);
        }
    }

    private static string NormalizeArchiveEntryPath(string entryPath) =>
        (entryPath ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static bool IsSupportedPackageInterfacePath(string entryPath)
    {
        var segments = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
            return segments[0].Equals("interface.json", StringComparison.OrdinalIgnoreCase);
        if (segments.Length == 2)
            return segments[1].Equals("interface.json", StringComparison.OrdinalIgnoreCase);
        return segments.Length == 3
               && segments[1].Equals("assets", StringComparison.OrdinalIgnoreCase)
               && segments[2].Equals("interface.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnvEnabled(string envName)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static void LogResourceUpdateDebugSummary(
        string stage,
        string latestVersion,
        string localVersion,
        string tempZipFilePath,
        string tempExtractDir,
        string originPath,
        string targetRoot,
        bool isGithub,
        bool isFull,
        bool isIncrementalPackage,
        bool debugEnabled,
        bool dryRun)
    {
        if (!debugEnabled)
            return;

        LoggerHelper.Info(
            $"[UpdateResourceDebug] stage={stage}, localVersion={localVersion}, latestVersion={latestVersion}, " +
            $"source={(isGithub ? "GitHub" : "Mirror")}, mode={(isFull ? "Full" : "Incremental")}, " +
            $"incrementalPackage={isIncrementalPackage}, dryRun={dryRun}, tempZip={tempZipFilePath}, " +
            $"tempExtractDir={tempExtractDir}, originPath={originPath}, targetRoot={targetRoot}");
    }

    private static void WriteResourceUpdateDebugManifest(
        string tempPath,
        string latestVersion,
        string localVersion,
        string downloadUrl,
        string tempZipFilePath,
        string tempExtractDir,
        string originPath,
        string interfacePath,
        bool isGithub,
        bool isFull,
        bool isIncrementalPackage,
        bool debugEnabled,
        bool dryRun)
    {
        if (!debugEnabled)
            return;

        try
        {
            var manifestPath = Path.Combine(tempPath, "update-resource-debug.json");
            var manifest = new
            {
                createdAt = DateTime.Now,
                localVersion,
                latestVersion,
                source = isGithub ? "GitHub" : "Mirror",
                mode = isFull ? "Full" : "Incremental",
                isIncrementalPackage,
                dryRun,
                dataRoot = AppPaths.DataRoot,
                resourceRoot = AppPaths.ResourceDirectory,
                agentRoot = AppPaths.AgentDirectory,
                downloadUrl,
                tempZipFilePath,
                tempExtractDir,
                originPath,
                interfacePath
            };

            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            LoggerHelper.Info($"[UpdateResourceDebug] 已写入调试清单：{manifestPath}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"[UpdateResourceDebug] 写入调试清单失败：{ex.Message}");
        }
    }

    private static string GetLocalPackageExtractDirectory(string localPackagePath)
    {
        var normalizedPackagePath = Path.GetFullPath(localPackagePath);
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPackagePath)))[..12]
            .ToLowerInvariant();
        return Path.Combine(AppPaths.TempResourceDirectory, $"local_package_{pathHash}_extracted");
    }

    private static async Task UpdateResourceFromLocalPackageCoreAsync(string packagePath, string currentVersion)
    {
        var extractDirectory = GetLocalPackageExtractDirectory(packagePath);
        try
        {
            await UpdateResource(
                    false,
                    currentVersion: currentVersion,
                    localPackagePath: packagePath)
                .ConfigureAwait(false);
        }
        finally
        {
            CleanupLocalPackageExtractDirectory(extractDirectory);
        }
    }

    private static void CleanupLocalPackageExtractDirectory(string extractDirectory)
    {
        try
        {
            if (!Directory.Exists(extractDirectory))
                return;

            Directory.Delete(extractDirectory, true);
            LoggerHelper.Info($"已清理本地资源包解压目录：目录={extractDirectory}");
        }
        catch (Exception ex)
        {
            // App startup also clears temp_res, so a locked directory is cleaned on the next launch.
            LoggerHelper.Warning($"清理本地资源包解压目录失败，将在下次启动时重试：目录={extractDirectory}，原因={ex.Message}");
        }
    }

    public static void Check()
    {
        _ = CheckAsync();
    }

    /// <summary>
    /// 主窗口首次就绪后执行一次启动更新检查。后续调用复用同一个任务，
    /// 避免窗口重复 Loaded 或多个实例初始化导致重复请求与重复更新。
    /// </summary>
    public static Task CheckOnStartupAsync()
    {
        lock (StartupCheckLock)
        {
            return _startupCheckTask ??= CheckAsync(isStartup: true);
        }
    }

    public static async Task CheckAsync(bool isStartup = false)
    {
        await CheckExecutionLock.WaitAsync();
        try
        {
            var config = new
            {
                AutoUpdateResource = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableAutoUpdateResource, false),
                CheckVersion = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableCheckVersion, true),
            };
            var resourceVersion = GetResourceVersion();

            if ((!config.AutoUpdateResource && !config.CheckVersion)
                || resourceVersion.Contains("debug", StringComparison.OrdinalIgnoreCase))
            {
                LoggerHelper.Info("已按更新设置或调试资源版本跳过启动更新检测。");
                return;
            }

            AddCDKCheckTask();

            if (config.AutoUpdateResource)
            {
                AddResourceUpdateTask();
            }
            else if (config.CheckVersion)
            {
                AddResourceCheckTask(notifyWhenUpToDate: !isStartup, notifyOnError: !isStartup);
            }

        // if (config.AutoUpdateMFA)
        // {
        //     AddMFAUpdateTask();
        // }
        // else if (config.CheckVersion)
        // {
        //     AddMFACheckTask();
        // }

            await TaskManager.RunTaskAsync(async () => await ExecuteTasksAsync(),
                () => ToastNotification.Show("自动更新时发生错误！"), isStartup ? "启动更新检测" : "更新检测");
        }
        finally
        {
            CheckExecutionLock.Release();
        }
    }

    public static void CheckCDKAsync() => TaskManager.RunTaskAsync(() => CheckForCDK(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0), name: "查询CDK剩余时间");
    public static void CheckMFAVersionAsync() => TaskManager.RunTaskAsync(async () => await CheckForMFAUpdatesAsync(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0), name: "检测MFA版本");
    public static void CheckResourceVersionAsync() => TaskManager.RunTaskAsync(async () => await CheckForResourceUpdatesAsync(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0), name: "检测资源版本");
    public static void UpdateResourceAsync(string
        currentVersion = "") => TaskManager.RunTaskAsync(() => UpdateResource(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0, currentVersion: currentVersion), name: "更新资源");
    public static void UpdateResourceFromLocalPackageAsync(string packagePath, string currentVersion = "") =>
        TaskManager.RunTaskAsync(
            () => Task.Run(() => UpdateResourceFromLocalPackageCoreAsync(packagePath, currentVersion)),
            name: "本地更新包更新");
    public static void UpdateMFAAsync() => TaskManager.RunTaskAsync(() => UpdateMFA(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0), name: "更新软件");

    public static void UpdateMaaFwAsync() => TaskManager.RunTaskAsync(() => UpdateMaaFw(), name: "更新MaaFw");

    private static void AddResourceCheckTask(bool notifyWhenUpToDate = true, bool notifyOnError = true)
    {
        Queue.Enqueue(new ValueType.MFATask
        {
            Action = async () => await CheckForResourceUpdatesAsync(
                Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0,
                notifyWhenUpToDate,
                notifyOnError),
            Name = "更新资源"
        });
    }

    private static void AddMFACheckTask()
    {
        Queue.Enqueue(new ValueType.MFATask
        {
            Action = async () => await CheckForMFAUpdatesAsync(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0),
            Name = "更新软件"
        });
    }
    private static void AddCDKCheckTask()
    {
        Queue.Enqueue(new ValueType.MFATask
        {
            Action = async () => CheckForCDK(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0),
            Name = "查询CDK"
        });
    }
    private static void AddResourceUpdateTask()
    {
        Queue.Enqueue(new ValueType.MFATask
        {
            Action = async () => await UpdateResource(
                Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0,
                closeDialog: true,
                noDialog: true),
            Name = "更新资源"
        });
    }

    private static SemaphoreSlim _queueLock = new(1, 1);

    private static void AddMFAUpdateTask()
    {
        Queue.Enqueue(new ValueType.MFATask
        {

            Action = async () => UpdateMFA(Instances.VersionUpdateSettingsUserControlModel.DownloadSourceIndex == 0),
            Name = "更新软件"
        });
    }

    public static void CheckMinVersion()
    {
        if (IsNewVersionAvailable(GetMinVersion(), GetLocalVersion()))
        {
            Instances.DialogManager.CreateDialog().OfType(NotificationType.Warning).WithContent(LangKeys.UiVersionBelowResourceRequirement.ToLocalizationFormatted(false, GetLocalVersion(), GetMinVersion()))
                .WithActionButton(LangKeys.Ok.ToLocalization(), dialog => { }, true).TryShow();
        }
    }
    public static void CheckForCDK(bool isGithub = true)
    {
        if (isGithub) return;
        try
        {
            GetDownloadUrlFromMirror("v0.0.0", "MFAAvalonia", CDK(), out _, out _, out _, out _, onlyCheck: false, saveAnnouncement: false);
        }
        catch (Exception)
        {
        }
    }

    private static object CreateUpdateToastContent(string subject, string latestVersion)
    {
        var summary = subject + LangKeys.NewVersionAvailableLatestVersion.ToLocalization() + latestVersion;
        var releasePath = Path.Combine(AppPaths.ResourceDirectory, ChangelogViewModel.ReleaseFileName);

        try
        {
            if (File.Exists(releasePath))
            {
                var release = File.ReadAllText(releasePath);
                if (!string.IsNullOrWhiteSpace(release) && !release.Trim().Equals("placeholder", StringComparison.OrdinalIgnoreCase))
                    return new UpdateToastContentView($"{summary}\n\n{release}");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"读取更新说明失败：{ex.Message}");
        }

        return summary;
    }

    public static async Task CheckForResourceUpdatesAsync(
        bool isGithub = true,
        bool notifyWhenUpToDate = true,
        bool notifyOnError = true)
    {
        Instances.RootViewModel.SetUpdating(true);
        var url = MaaProcessor.Interface?.Github ?? MaaProcessor.Interface?.Url ?? string.Empty;

        string[] strings = [];
        try
        {
            if (isGithub)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    ToastHelper.Info(LangKeys.CurrentResourcesNotSupportGitHub.ToLocalization());
                    Instances.RootViewModel.SetUpdating(false);
                    return;
                }
                strings = GetRepoFromUrl(url);
            }
            var resourceVersion = GetResourceVersion();
            if (string.IsNullOrWhiteSpace(resourceVersion))
            {
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            string latestVersion = string.Empty;
            string sha256 = string.Empty;
            if (isGithub)
            {
                var result = await GetLatestVersionAndDownloadUrlFromGithubAsync(strings[0], strings[1], true, currentVersion: resourceVersion).ConfigureAwait(false);
                latestVersion = result.latestVersion;
                sha256 = result.sha256;
            }
            else
                GetDownloadUrlFromMirror(resourceVersion, GetResourceID(), CDK(), out _, out latestVersion, out sha256, out _, onlyCheck: true, currentVersion: resourceVersion);

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            if (IsNewVersionAvailable(latestVersion, resourceVersion))
            {
                DispatcherHelper.PostOnMainThread(() =>
                {
                    Instances.RootViewModel.WindowUpdateInfo = LangKeys.MirrorChyanResourceUpdateShortTip.ToLocalizationFormatted(false, latestVersion);

                    Instances.ToastManager.CreateToast().WithTitle(LangKeys.UpdateResource.ToLocalization())
                        .WithContent(CreateUpdateToastContent(
                            LangKeys.ResourceOption.ToLocalization(), latestVersion)).Dismiss().After(TimeSpan.FromSeconds(6))
                        .WithActionButton(LangKeys.Later.ToLocalization(), _ => { }, true, SukiButtonStyles.Basic)
                        .WithActionButton(LangKeys.Update.ToLocalization(), _ =>
                        {
                            if (!Instances.RootViewModel.IsUpdating)
                                UpdateResourceAsync();
                            else
                                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.CurrentOtherUpdatingTask.ToLocalization());
                        }, true).Queue();
                });
                Instances.RootViewModel.TempResourceUpdateAction = () =>
                    DispatcherHelper.PostOnMainThread(() =>
                    {
                        Instances.ToastManager.CreateToast().WithTitle(LangKeys.UpdateResource.ToLocalization())
                            .WithContent(CreateUpdateToastContent(
                                LangKeys.ResourceOption.ToLocalization(), latestVersion)).Dismiss().After(TimeSpan.FromSeconds(6))
                            .WithActionButton(LangKeys.Later.ToLocalization(), _ => { }, true, SukiButtonStyles.Basic)
                            .WithActionButton(LangKeys.Update.ToLocalization(), _ =>
                            {
                                if (!Instances.RootViewModel.IsUpdating)
                                    UpdateResourceAsync();
                                else
                                    ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.CurrentOtherUpdatingTask.ToLocalization());
                            }, true).Queue();
                    });
            }
            else
            {
                if (notifyWhenUpToDate)
                    ToastHelper.Info(LangKeys.ResourcesAreLatestVersion.ToLocalization());
            }
            Instances.RootViewModel.SetUpdating(false);
        }
        catch (Exception ex)
        {
            Instances.RootViewModel.SetUpdating(false);
            if (notifyOnError)
            {
                if (ex.Message.Contains("resource not found"))
                    ToastHelper.Error(LangKeys.CurrentResourcesNotSupportMirror.ToLocalization());
                else
                    ToastHelper.Error(LangKeys.ErrorWhenCheck.ToLocalizationFormatted(true, "Resource"), ex.Message, -1);
            }
            LoggerHelper.Error($"检查资源更新失败：来源={(isGithub ? "GitHub" : "Mirror")}，原因={ex.Message}", ex);
        }
    }

    public static async Task CheckForMFAUpdatesAsync(bool isGithub = true)
    {
        try
        {
            Instances.RootViewModel.SetUpdating(true);
            var localVersion = GetLocalVersion();
            string latestVersion = string.Empty;
            string sha256 = string.Empty;
            if (isGithub)
            {
                var result = await GetLatestVersionAndDownloadUrlFromGithubAsync().ConfigureAwait(false);
                latestVersion = result.latestVersion;
                sha256 = result.sha256;
            }
            else
                GetDownloadUrlFromMirror(localVersion, "MFAAvalonia", CDK(), out _, out latestVersion, out sha256, out _, isUI: true, onlyCheck: true);
            var mirrocS = false;
            if (IsNewVersionAvailable(latestVersion, GetMaxVersion()))
            {
                latestVersion = GetMaxVersion();
                if (!isGithub) mirrocS = true;
            }
            if (mirrocS)
            {
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.SwitchUiUpdateSourceToGithub.ToLocalization());
            }
            else if (IsNewVersionAvailable(latestVersion, localVersion))
            {
                DispatcherHelper.PostOnMainThread(() =>
                {
                    Instances.ToastManager.CreateToast().WithTitle(LangKeys.SoftwareUpdate.ToLocalization())
                        .WithContent(CreateUpdateToastContent("MFA", latestVersion)).Dismiss().After(TimeSpan.FromSeconds(6))
                        .WithActionButton(LangKeys.Later.ToLocalization(), _ => { }, true, SukiButtonStyles.Basic)
                        .WithActionButton(LangKeys.Update.ToLocalization(), _ =>
                        {
                            if (!Instances.RootViewModel.IsUpdating)
                                UpdateMFAAsync();
                            else
                                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.CurrentOtherUpdatingTask.ToLocalization());
                        }, true).Queue();
                });
            }
            else
            {
                ToastHelper.Info(LangKeys.MFAIsLatestVersion.ToLocalization());
            }

            Instances.RootViewModel.SetUpdating(false);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("resource not found"))
                ToastHelper.Error(LangKeys.CurrentResourcesNotSupportMirror.ToLocalization());
            else
                ToastHelper.Error(LangKeys.ErrorWhenCheck.ToLocalizationFormatted(false, "MFA"), ex.Message);
            Instances.RootViewModel.SetUpdating(false);
            LoggerHelper.Error($"检查 MFA 更新失败：来源={(isGithub ? "GitHub" : "Mirror")}，原因={ex.Message}", ex);
        }
    }


    public async static Task UpdateResource(bool isGithub = true, bool closeDialog = false, bool noDialog = false, Action action = null, string currentVersion = "", string? localPackagePath = null)
    {
        shouldShowToast = false;
        Instances.RootViewModel.SetUpdating(true);
        var debugEnabled = IsEnvEnabled(ResourceUpdateDebugEnv);
        var dryRun = debugEnabled && IsEnvEnabled(ResourceUpdateDryRunEnv);
        var keepArtifacts = debugEnabled || IsEnvEnabled(ResourceUpdateKeepArtifactsEnv);
        var isLocalPackage = !string.IsNullOrWhiteSpace(localPackagePath);
        ProgressBar? progress = null;
        TextBlock? textBlock = null;
        TextBlock? downloadSpeedTextBlock = null;
        ISukiToast? sukiToast = null;
        StackPanel stackPanel = await DispatcherHelper.RunOnMainThreadAsync(() =>
        {
            progress = new ProgressBar
            {
                Height = 10,
                Value = 0,
                Margin = new Thickness(0, 5),
                ShowProgressText = true
            };
            StackPanel stackPanel = new();
            textBlock = new TextBlock
            {
                Text = LangKeys.GettingLatestResources.ToLocalization(),
            };
            downloadSpeedTextBlock = new TextBlock
            {
                Text = string.Empty,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var infoGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };
            Grid.SetColumn(textBlock, 0);
            Grid.SetColumn(downloadSpeedTextBlock, 1);
            infoGrid.Children.Add(textBlock);
            infoGrid.Children.Add(downloadSpeedTextBlock);
            stackPanel.Children.Add(progress);
            stackPanel.Children.Add(infoGrid);
            return stackPanel;
        });

        sukiToast = await DispatcherHelper.RunOnMainThreadAsync(() =>
            Instances.ToastManager.CreateToast()
                .WithTitle(LangKeys.UpdateResource.ToLocalization())
                .WithContent(stackPanel).Queue()
        );
        var localVersion = string.IsNullOrWhiteSpace(currentVersion) ? MaaProcessor.Interface?.Version ?? string.Empty : currentVersion;

        if (string.IsNullOrWhiteSpace(localVersion))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.FailToGetCurrentVersionInfo.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            return;
        }

        SetProgress(progress, 10);
        string[] strings = [];
        if (isGithub)
        {
            var url = MaaProcessor.Interface?.Github ?? MaaProcessor.Interface?.Url ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.CurrentResourcesNotSupportGitHub.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            strings = GetRepoFromUrl(url);
        }
        string latestVersion = string.Empty;
        string downloadUrl = string.Empty;
        string sha256 = string.Empty;
        var isFull = true;
        if (isLocalPackage)
        {
            if (!File.Exists(localPackagePath))
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), $"本地资源包不存在：{localPackagePath}");
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            latestVersion = Path.GetFileNameWithoutExtension(localPackagePath);
            downloadUrl = localPackagePath;
            LoggerHelper.Info($"已选择本地更新包：文件={localPackagePath}");
        }
        else
        {
            try
            {
                if (isGithub)
                {
                    var result = await GetLatestVersionAndDownloadUrlFromGithubAsync(strings[0], strings[1], false, "", localVersion).ConfigureAwait(false);
                    downloadUrl = result.url;
                    latestVersion = result.latestVersion;
                    sha256 = result.sha256;
                }
                else
                    GetDownloadUrlFromMirror(localVersion, GetResourceID(), CDK(), out downloadUrl, out latestVersion, out sha256, out isFull, currentVersion: localVersion);
            }
            catch (Exception ex)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn($"{LangKeys.FailToGetLatestVersionInfo.ToLocalization()}", ex.Message, -1);
                Instances.RootViewModel.SetUpdating(false);
                LoggerHelper.Error($"获取资源包下载信息失败：来源={(isGithub ? "GitHub" : "Mirror")}，本地版本={localVersion}，原因={ex.Message}", ex);
                return;
            }
        }

        SetProgress(progress, 50);

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.FailToGetLatestVersionInfo.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.ClearDownloadProgress();
            return;
        }

        if (!isLocalPackage && !IsNewVersionAvailable(latestVersion, localVersion))
        {
            Dismiss(sukiToast);
            ToastHelper.Info(LangKeys.ResourcesAreLatestVersion.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.ClearDownloadProgress();
            action?.Invoke();
            return;
        }

        SetProgress(progress, 100);

        if (!isLocalPackage && string.IsNullOrWhiteSpace(downloadUrl))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.FailToGetDownloadUrl.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.ClearDownloadProgress();
            return;
        }
        if (OperatingSystem.IsAndroid() && !isLocalPackage && string.IsNullOrWhiteSpace(sha256))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), "Android APK Release 未提供 SHA256 digest，已拒绝下载。", -1);
            Instances.RootViewModel.SetUpdating(false);
            return;
        }
        MaaProcessorManager.Instance.Current.SetTasker();
        // 仅停止正在运行的任务，不 Dispose 处理器（避免界面变白/配置列表清空）
        // BeforeClosed 将在重启前调用
        DispatcherHelper.PostOnMainThread(() =>
        {
            if (Instances.RootViewModel.IsRunning)
            {
                foreach (var processor in MaaProcessor.Processors)
                {
                    processor.Stop(MFATask.MFATaskStatus.STOPPED);
                }
            }
        });
        var tempPath = AppPaths.TempResourceDirectory;
        Directory.CreateDirectory(tempPath);
        var debugSessionId = DateTime.Now.ToString("yyyyMMddHHmmss");
        string fileExtension = OperatingSystem.IsAndroid() ? ".apk" : GetFileExtensionFromUrl(downloadUrl);
        if (string.IsNullOrEmpty(fileExtension))
        {
            fileExtension = ".zip";
        }
        var tempZipFilePath = isLocalPackage
            ? localPackagePath!
            : Path.Combine(
                tempPath,
                keepArtifacts ? $"resource_{latestVersion}_{debugSessionId}{fileExtension}" : $"resource_{latestVersion}{fileExtension}");

        if (!isLocalPackage)
        {
            SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.Downloading.ToLocalization());
            SetProgress(progress, 0);
            (var downloadStatus, tempZipFilePath) = await DownloadWithRetry(downloadUrl, tempZipFilePath, progress, 3, textBlock, downloadSpeedTextBlock);
            LoggerHelper.Info($"已下载更新包到临时文件：文件={tempZipFilePath}");
            if (!downloadStatus)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.DownloadFailed.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
        }
        else
        {
            SetStatusText(textBlock, downloadSpeedTextBlock, $"使用本地更新包：{Path.GetFileName(localPackagePath)}");
            LoggerHelper.Info($"使用本地更新包执行更新：文件={tempZipFilePath}");
        }

        SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.Extracting.ToLocalization());
        SetProgress(progress, 0);

        var tempExtractDir = isLocalPackage
            ? GetLocalPackageExtractDirectory(localPackagePath!)
            : Path.Combine(
                tempPath,
                keepArtifacts ? $"resource_{latestVersion}_{debugSessionId}_extracted" : $"resource_{latestVersion}_extracted");
        if (Directory.Exists(tempExtractDir))
            Directory.Delete(tempExtractDir, true);
        if (!File.Exists(tempZipFilePath))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.DownloadFailed.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            return;
        }
        SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.Verifying.ToLocalization());
        var sha256Verified = true;
        if (isLocalPackage)
        {
            LoggerHelper.Info($"本地更新包已跳过远程 SHA256 校验：文件={tempZipFilePath}");
        }
        else if (string.IsNullOrWhiteSpace(sha256))
        {
            LoggerHelper.Warning($"已跳过 SHA256 校验：文件={tempZipFilePath}，原因=未提供校验值");
        }
        else
        {
            sha256Verified = await VerifyFileSha256Async(tempZipFilePath, sha256);
            LoggerHelper.Info($"SHA256 校验结果：文件={tempZipFilePath}，期望值={sha256}，通过={sha256Verified}");
        }
        if (!string.IsNullOrWhiteSpace(sha256) && !sha256Verified)
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.HashVerificationFailed.ToLocalization());
            Instances.RootViewModel.SetUpdating(false);
            return;
        }
        if (OperatingSystem.IsAndroid())
        {
            if (!tempZipFilePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), "Android 更新资产不是 APK，已拒绝安装。", -1);
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(sha256) || !sha256Verified)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), "Android APK 缺少有效的 SHA256 校验值，已拒绝安装。", -1);
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            if (PlatformApplicationRestart.InstallApkAsync == null)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.ApplyingUpdate.ToLocalization());
            SetProgress(progress, 100);
            Instances.RootViewModel.SetUpdating(false);
            Dismiss(sukiToast);
            await PlatformApplicationRestart.InstallApkAsync(tempZipFilePath);
            return;
        }
        SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.Extracting.ToLocalization());
        LoggerHelper.Info($"准备解压资源包：源文件={tempZipFilePath}，目标目录={tempExtractDir}");
        UniversalExtractor.Extract(tempZipFilePath, tempExtractDir);
        SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.ApplyingUpdate.ToLocalization());
        var changesPath = Path.Combine(tempExtractDir, "changes.json");
        var isIncrementalPackage = File.Exists(changesPath)
                                   && (isLocalPackage || !isGithub)
                                   && !currentVersion.Equals("v0.0.0", StringComparison.OrdinalIgnoreCase);
        if (!ValidateExtractedResourcePackage(tempExtractDir, isIncrementalPackage, out var originPath, out var interfacePath, out var resourceDirPath, out var validationMessage))
        {
            Dismiss(sukiToast);
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), validationMessage, -1);
            Instances.RootViewModel.SetUpdating(false);
            return;
        }

        var containsCoreApplicationFiles = ContainsCoreApplicationFiles(originPath);
        if (containsCoreApplicationFiles)
        {
            LoggerHelper.Warning($"更新包包含核心程序文件，将按二合一路径处理安装目录与数据目录：目录={originPath}");
        }

        if (File.Exists(interfacePath))
        {
            try
            {
                var interfaceJson = JObject.Parse(await File.ReadAllTextAsync(interfacePath));
                latestVersion = interfaceJson["version"]?.ToString() ?? latestVersion;
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"读取本地资源包版本信息失败：文件={interfacePath}，原因={ex.Message}");
            }
        }

        var wpfDir = AppPaths.DataRoot;
        var resourcePath = AppPaths.ResourceDirectory;
        var agentPath = AppPaths.AgentDirectory;
        // 获取当前运行的可执行文件路径（最可靠的方式，即使用户重命名了文件也能正确获取）
        var exeName = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        LoggerHelper.Info($"当前进程可执行文件：{exeName}");
        // 如果路径为空或文件不存在，尝试其他方式
        if (string.IsNullOrEmpty(exeName) || !File.Exists(exeName))
        {
            // 尝试使用 Environment.ProcessPath (.NET 6+)
            exeName = Environment.ProcessPath ?? string.Empty;
            LoggerHelper.Info($"已改用 Environment.ProcessPath 获取进程路径：{exeName}");
        }

        // 如果仍然为空或不存在，使用 AppContext.BaseDirectory + 当前进程名
        if (string.IsNullOrEmpty(exeName) || !File.Exists(exeName))
        {
            var processName = Process.GetCurrentProcess().ProcessName;
            var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            exeName = Path.Combine(AppContext.BaseDirectory, processName + extension);
            LoggerHelper.Info($"已回退为进程名推断可执行文件路径：{exeName}");
        }
        if (File.Exists(changesPath))
            isFull = false;
        else
            LoggerHelper.Error($"未找到 changes.json，无法执行增量更新：文件={changesPath}");
        LogResourceUpdateDebugSummary(
            "validated",
            latestVersion,
            localVersion,
            tempZipFilePath,
            tempExtractDir,
            originPath,
            wpfDir,
            isGithub,
            isFull,
            isIncrementalPackage,
            debugEnabled,
            dryRun);
        WriteResourceUpdateDebugManifest(
            tempPath,
            latestVersion,
            localVersion,
            downloadUrl,
            tempZipFilePath,
            tempExtractDir,
            originPath,
            interfacePath,
            isGithub,
            isFull,
            isIncrementalPackage,
            debugEnabled,
            dryRun);

        if (dryRun)
        {
            SetProgress(progress, 100);
            SetStatusText(textBlock, downloadSpeedTextBlock, "UpdateResource DryRun completed");
            LoggerHelper.Warning($"[UpdateResourceDebug] 已启用 DryRun，跳过实际覆盖与重启。tempExtractDir={tempExtractDir}");
            ToastHelper.Info("UpdateResource DryRun 已完成", tempExtractDir, 5000);
            Instances.RootViewModel.SetUpdating(false);
            if (closeDialog)
                Dismiss(sukiToast);
            action?.Invoke();
            return;
        }

        await StopTaskersAndAgentsForUpdateAsync();
        using var updateTransaction = new UpdateFileTransaction(tempPath);
        LoggerHelper.Info((isGithub || isFull || currentVersion.Equals("v0.0.0", StringComparison.OrdinalIgnoreCase)) ? "全量更新" : "增量更新");
        if (isGithub || isFull || currentVersion.Equals("v0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(resourcePath))
            {
                foreach (var rfile in Directory.EnumerateFiles(resourcePath, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(rfile);
                    if (fileName.Equals(ChangelogViewModel.ChangelogFileName, StringComparison.OrdinalIgnoreCase) || fileName.Contains("interface.json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        File.SetAttributes(rfile, FileAttributes.Normal);
                        LoggerHelper.Info($"准备删除旧文件：文件={rfile}");
                        updateTransaction.DeleteFile(rfile);
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"删除旧文件失败：文件={rfile}，原因={ex.Message}", ex);
                        throw;
                    }
                }
            }
            if (Directory.Exists(agentPath))
            {
                foreach (var rfile in Directory.EnumerateFiles(agentPath, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(rfile);
                    if (fileName.Equals(ChangelogViewModel.ChangelogFileName, StringComparison.OrdinalIgnoreCase) || fileName.Contains("interface.json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        File.SetAttributes(rfile, FileAttributes.Normal);
                        LoggerHelper.Info($"准备删除旧文件：文件={rfile}");
                        updateTransaction.DeleteFile(rfile);
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"删除旧文件失败：文件={rfile}，原因={ex.Message}", ex);
                        throw;
                    }
                }
            }
        }
        else
        {
            if (File.Exists(changesPath))
            {
                var changes = await File.ReadAllTextAsync(changesPath);
                if (string.IsNullOrWhiteSpace(changes))
                {
                    LoggerHelper.Warning($"changes.json 为空，无法执行增量更新：文件={changesPath}");
                }
                else
                {
                    var stringBuilder = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd"));
                    stringBuilder.AppendLine(changes);
                    await File.WriteAllTextAsync(AppPaths.ChangesPath, stringBuilder.ToString());
                }
                try
                {
                    var changesJson = JsonConvert.DeserializeObject<MirrorChangesJson>(changes);
                    ApplyIncrementalDeletions(changesJson, updateTransaction);
                }
                catch (Exception e)
                {
                    LoggerHelper.Error($"处理增量更新文件列表失败：changes.json={changesPath}，原因={e.Message}", e);
                    throw;
                }
            }
        }


        SetProgress(progress, 1);

        var di = new DirectoryInfo(originPath);
        if (di.Exists)
        {
            await CopyUpdatePayload(originPath, progress, true, default, copyDataFiles: true, copyInstallFiles: !containsCoreApplicationFiles, updateTransaction: updateTransaction);
        }

        var restartExecutablePath = exeName;
        if (containsCoreApplicationFiles)
        {
            var packageExecutablePath = FindMFAExecutableInDirectory(originPath);
            if (!string.IsNullOrWhiteSpace(packageExecutablePath))
            {
                var packageExecutableRelativePath = Path.GetRelativePath(originPath, packageExecutablePath);
                restartExecutablePath = Path.Combine(AppPaths.InstallRoot, packageExecutableRelativePath);
                LoggerHelper.Info($"检测到更新包中的主程序入口：源文件={packageExecutablePath}，重启目标={restartExecutablePath}");

                if (HasExecutableFileNameChanged(exeName, restartExecutablePath))
                {
                    LoggerHelper.Info($"检测到主程序文件名变更，将在更新完成后尝试把旧主程序改名为 backupMFA：旧文件={exeName}，新文件={restartExecutablePath}");
                }
            }

            LoggerHelper.Info($"更新包包含程序文件，直接写入安装目录：目标目录={AppPaths.InstallRoot}");
            await CopyUpdatePayload(
                originPath,
                progress,
                false,
                default,
                copyDataFiles: false,
                copyInstallFiles: true,
                continueOnCopyFailure: false,
                skipRunningExecutable: true,
                updateTransaction: updateTransaction);

        }

        // 所有数据和安装文件成功写入后再提交版本元数据。
        var newInterfacePath = Path.Combine(wpfDir, "interface.json");
        if (File.Exists(interfacePath))
        {
            var jsonContent = await File.ReadAllTextAsync(interfacePath);
            var @interface = JObject.Parse(jsonContent);
            if (@interface != null)
            {
                @interface["github"] = MaaProcessor.Interface?.Github ?? MaaProcessor.Interface?.Url;
                @interface["version"] = latestVersion;
            }
            updateTransaction.WriteAllText(newInterfacePath, @interface.ToString(Formatting.Indented));
        }

        updateTransaction.Commit();

        if (containsCoreApplicationFiles && HasExecutableFileNameChanged(exeName, restartExecutablePath))
            TryRenameExecutableToBackup(exeName, allowCurrentProcessExecutable: true);

        SetProgress(progress, 100);
        SetStatusText(textBlock, downloadSpeedTextBlock, LangKeys.UpdateCompleted.ToLocalization());
        Instances.RootViewModel.SetUpdating(false);

        if (closeDialog)
            Dismiss(sukiToast);
        shouldShowToast = true;
        action?.Invoke();

        if (isLocalPackage)
            CleanupLocalPackageExtractDirectory(tempExtractDir);

        // 重启前执行清理（保存配置、释放资源等）
        Interlocked.Exchange(ref _restartPending, 1);
        DispatcherHelper.PostOnMainThread(() => Instances.TryBeforeClosed(true, true));
        await RestartApplicationAsync(restartExecutablePath);
    }

    private static async Task StopTaskersAndAgentsForUpdateAsync()
    {
        LoggerHelper.Info("程序更新前开始停止所有 MaaTasker 和 Agent 进程。");
        await Task.Run(() =>
        {
            foreach (var processor in MaaProcessor.Processors.ToList())
            {
                try
                {
                    processor.SetTasker(requireAllStopped: true);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"程序更新前清理实例失败：实例ID={processor.InstanceId}，原因={ex.Message}", ex);
                    throw;
                }
            }
        });
        LoggerHelper.Info("程序更新前所有 MaaTasker 和 Agent 进程已停止。");
    }

    private static void ApplyIncrementalDeletions(MirrorChangesJson? changes, UpdateFileTransaction updateTransaction)
    {
        DeleteIncrementalFiles(changes?.Deleted, "Deleted", updateTransaction);
        DeleteIncrementalFiles(changes?.Modified, "Modified", updateTransaction);

        var directories = (changes?.DeletedDirectories ?? [])
            .Select(relativePath =>
            {
                if (!TryGetSafeUpdateTargetPath(relativePath, out var fullPath))
                    throw new InvalidDataException($"增量更新包含越界删除目录：{relativePath}");
                return (relativePath, fullPath);
            })
            .Where(item => Directory.Exists(item.fullPath))
            .OrderByDescending(item => item.fullPath.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar));

        foreach (var (relativePath, fullPath) in directories)
        {
            LoggerHelper.Info($"准备删除增量更新中标记为 deleted_dir 的目录：相对路径={relativePath}，目录={fullPath}");
            DeleteDirectoryForUpdate(relativePath, fullPath);
        }

        foreach (var relativePath in changes?.AddedDirectories ?? [])
        {
            if (!TryGetSafeUpdateTargetPath(relativePath, out var fullPath))
                throw new InvalidDataException($"增量更新包含越界新增目录：{relativePath}");

            LoggerHelper.Info($"准备创建增量更新中标记为 added_dir 的目录：相对路径={relativePath}，目录={fullPath}");
            Directory.CreateDirectory(fullPath);
        }
    }

    private static void DeleteIncrementalFiles(IEnumerable<string>? relativePaths, string changeType, UpdateFileTransaction updateTransaction)
    {
        foreach (var relativePath in relativePaths ?? [])
        {
            if (!TryGetSafeUpdateTargetPath(relativePath, out var fullPath))
                throw new InvalidDataException($"增量更新包含越界{changeType}路径：{relativePath}");
            if (!File.Exists(fullPath))
                continue;

            LoggerHelper.Info($"准备删除增量更新中标记为 {changeType} 的文件：文件={fullPath}");
            updateTransaction.DeleteFile(fullPath);
            if (File.Exists(fullPath))
                throw new IOException($"无法删除或备份增量更新文件：{fullPath}");
        }
    }

    private static void DeleteDirectoryForUpdate(string relativePath, string directoryPath)
    {
        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // deleted_dir entries are processed deepest-first after their files are removed.
                // Refuse to recursively delete unexpected contents so an incomplete manifest
                // cannot remove user files or files written later in the same update.
                Directory.Delete(directoryPath, recursive: false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxRetries)
                    break;
                LoggerHelper.Warning($"删除增量更新目录失败，准备重试：第 {attempt}/{maxRetries} 次，目录={directoryPath}，原因={ex.Message}");
                Thread.Sleep(500 * attempt);
            }
        }

        PendingUpdateDeletionHelper.EnqueueDirectory(relativePath);
        LoggerHelper.Warning($"删除增量更新目录失败，已加入下次启动待删除清单：相对路径={relativePath}，目录={directoryPath}");
    }

    /// <summary>
    /// 跨平台重启应用（仅 macOS 处理权限和启动逻辑，Windows/Linux 保留原有逻辑）
    /// </summary>
    /// <param name="exeName">应用可执行文件路径</param>
    public async static Task RestartApplicationAsync(string exeName)
    {
        LoggerHelper.Info($"准备重新启动应用：可执行文件={exeName}");
        LoggerHelper.Info("当前 MFA 进程即将退出。");
        LoggerHelper.DisposeLogger();
        if (OperatingSystem.IsAndroid())
        {
            if (PlatformApplicationRestart.RestartAsync != null)
            {
                await PlatformApplicationRestart.RestartAsync();
            }
            else
            {
                Instances.ShutdownApplication(forceStop: true);
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            // ==== 仅 macOS 执行专属逻辑 ====
            try
            {
                // 1. 自动赋予执行权限（解决 Permission denied）
                await GrantMacOSExecutePermissionAsync(exeName);

                // 2. macOS 专属启动方式
                StartMacOSApplication(exeName);
                // 3. 短暂延迟确保新进程启动，再关闭当前应用
                await Task.Delay(1000);
                Instances.ShutdownApplication();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"macOS 启动应用失败：{ex.Message}");
                throw;
            }
        }
        else
        {
            // ==== Windows/Linux 完全保留原有逻辑 ====
            Process.Start(exeName);
            Instances.ShutdownApplication();
        }
    }

    /// <summary>
    /// macOS 专属：给文件赋予执行权限（chmod +x）
    /// </summary>
    async private static Task GrantMacOSExecutePermissionAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            // 处理 .app 应用包（macOS 标准格式，无需赋予权限，直接用 open 启动）
            if (Directory.Exists(filePath) && Path.GetExtension(filePath).Equals(".app", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new FileNotFoundException("macOS 目标可执行文件不存在", filePath);
        }

        // 执行 chmod +x 命令（转义路径中的单引号，避免空格/特殊字符报错）
        string escapedPath = filePath.Replace("'", "\\'");
        ProcessStartInfo chmodInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"chmod +x '{escapedPath}'\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        using Process chmodProcess = Process.Start(chmodInfo)!;
        string error = await chmodProcess.StandardError.ReadToEndAsync();
        await chmodProcess.WaitForExitAsync();

        if (chmodProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"赋予 macOS 执行权限失败：{error}");
        }
    }

    /// <summary>
    /// macOS 专属：启动应用（兼容二进制文件和 .app 包）
    /// </summary>
    private static void StartMacOSApplication(string exePath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo();

        if (Directory.Exists(exePath) && Path.GetExtension(exePath).Equals(".app", StringComparison.OrdinalIgnoreCase))
        {
            // 启动 .app 应用包（使用系统 open 命令）
            startInfo.FileName = "/usr/bin/open";
            startInfo.Arguments = $"\"{exePath}\"";
        }
        else
        {
            // 启动普通二进制文件（已赋予执行权限，可直接启动）
            startInfo.FileName = exePath;
            startInfo.WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        Process.Start(startInfo);
    }

    /// <summary>
    /// 将源目录（newPath）的所有内容复制到目标目录（oldPath）
    /// 1. 缺失的目标目录自动创建
    /// 2. 目标文件已存在时，先调用 DeleteFileWithBackup 备份删除
    /// 3. 复制后设置目标文件为普通属性（Normal）
    /// 4. 支持进度显示、公告文件特殊处理、取消操作
    /// </summary>
    /// <param name="newPath">源目录路径（要复制的目录）</param>
    /// <param name="oldPath">目标目录路径（复制到的目录）</param>
    /// <param name="progressBar">进度条控件（用于显示复制进度）</param>
    /// <param name="saveAnnouncement">是否启用公告文件特殊处理</param>
    /// <param name="cancellationToken">取消令牌（用于中断复制操作）</param>
    public async static Task CopyAndDelete(
        string newPath,
        string oldPath,
        ProgressBar? progressBar = null,
        bool saveAnnouncement = false,
        CancellationToken cancellationToken = default,
        bool skipCoreApplicationFiles = false)
    {
        // 1. 参数合法性验证（保持原有逻辑，空路径/源目录不存在直接返回）
        if (string.IsNullOrWhiteSpace(newPath) || string.IsNullOrWhiteSpace(oldPath) || !Directory.Exists(newPath))
            return;

        // 2. 统计源目录总文件数（用于进度条初始化）
        int totalFileCount = Directory.EnumerateFiles(newPath, "*", SearchOption.AllDirectories)
            .Count(file => !skipCoreApplicationFiles
                           || !IsCoreApplicationFile(Path.GetRelativePath(newPath, file)));

        // 3. 初始化进度条（UI操作需通过 Invoke 确保线程安全）
        DispatcherHelper.PostOnMainThread(() =>
        {
            progressBar?.Maximum = 100;
            progressBar?.Value = 0;
            progressBar?.IsVisible = true;
        });

        // 4. 进度计数器（递归中共享进度状态，避免线程安全问题）
        var progressCounter = new ProgressCounter()
        {
            Total = totalFileCount
        };

        try
        {
            // 5. 递归复制目录（核心逻辑，异步支持）
            await CopyDirectoryRecursively(
                newPath, oldPath, newPath, progressBar, saveAnnouncement, cancellationToken, progressCounter, skipCoreApplicationFiles);
        }
        catch (OperationCanceledException)
        {
            // 取消操作时可添加日志或后续处理
            DispatcherHelper.PostOnMainThread(() => progressBar?.IsVisible = false);
            throw; // 如需上层处理取消异常，可抛出；否则直接捕获忽略
        }

        // 6. 复制完成隐藏进度条
        DispatcherHelper.PostOnMainThread(() => progressBar?.IsVisible = false);
    }

    private async static Task CopyUpdatePayload(
        string sourceRoot,
        ProgressBar? progressBar = null,
        bool saveAnnouncement = false,
        CancellationToken cancellationToken = default,
        bool copyDataFiles = true,
        bool copyInstallFiles = true,
        string? installTargetRootOverride = null,
        bool continueOnCopyFailure = false,
        bool skipRunningExecutable = false,
        UpdateFileTransaction? updateTransaction = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return;

        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(file => new
            {
                SourceFile = file,
                RelativePath = Path.GetRelativePath(sourceRoot, file)
            })
            .Where(item =>
            {
                var isDataFile = IsDataRootRelativePath(item.RelativePath);
                return (copyDataFiles && isDataFile) || (copyInstallFiles && !isDataFile);
            })
            .ToList();

        DispatcherHelper.PostOnMainThread(() =>
        {
            progressBar?.Maximum = 100;
            progressBar?.Value = 0;
            progressBar?.IsVisible = true;
        });

        var progressCounter = new ProgressCounter
        {
            Total = files.Count
        };

        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isDataFile = IsDataRootRelativePath(item.RelativePath);
            var targetRoot = isDataFile
                ? AppPaths.DataRoot
                : installTargetRootOverride ?? AppPaths.InstallRoot;
            var targetFile = Path.Combine(targetRoot, item.RelativePath);
            var currentProcessPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (skipRunningExecutable
                && !string.IsNullOrWhiteSpace(currentProcessPath)
                && string.Equals(
                    Path.GetFullPath(targetFile),
                    Path.GetFullPath(currentProcessPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                var replacement = await TryReplaceRunningExecutableAsync(item.SourceFile, targetFile, cancellationToken);
                if (!replacement.replaced)
                {
                    if (!continueOnCopyFailure)
                        throw new IOException($"替换当前正在运行的同名主程序失败：{targetFile}");

                    LoggerHelper.Warning($"替换当前正在运行的同名主程序失败，已按继续模式跳过：文件={targetFile}");
                    progressCounter.Current++;
                    DispatcherHelper.PostOnMainThread(() =>
                    {
                        if (progressBar == null || progressCounter.Total <= 0)
                            return;

                        var percentage = Math.Round((progressCounter.Current * 100.0) / progressCounter.Total, 1);
                        progressBar.Value = Math.Min(percentage, 100);
                    });
                    continue;
                }

                if (updateTransaction != null && replacement.backupPath != null)
                    updateTransaction.TrackReplacement(targetFile, replacement.backupPath);

                progressCounter.Current++;
                DispatcherHelper.PostOnMainThread(() =>
                {
                    if (progressBar == null || progressCounter.Total <= 0)
                        return;

                    var percentage = Math.Round((progressCounter.Current * 100.0) / progressCounter.Total, 1);
                    progressBar.Value = Math.Min(percentage, 100);
                });
                continue;
            }

            var targetDir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            if (saveAnnouncement
                && targetFile.Contains(AnnouncementViewModel.AnnouncementFolder, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(item.SourceFile).Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAnnouncementFile(item.SourceFile, targetFile, cancellationToken);
            }

            var maxRetries = 5;
            var copySuccess = false;
            for (var i = 0; i < maxRetries; i++)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (updateTransaction != null)
                        {
                            updateTransaction.ReplaceFile(item.SourceFile, targetFile, cancellationToken);
                        }
                        else
                        {
                            var tempTarget = targetFile + ".pending_update";
                            try
                            {
                                File.Copy(item.SourceFile, tempTarget, overwrite: true);
                            }
                            catch
                            {
                                if (File.Exists(tempTarget)) File.Delete(tempTarget);
                                throw;
                            }
                            if (File.Exists(targetFile))
                                DeleteFileWithBackup(targetFile);
                            File.Move(tempTarget, targetFile, overwrite: true);
                            RestoreUnixExecutablePermission(targetFile);
                        }
                    }, cancellationToken);

                    File.SetAttributes(targetFile, FileAttributes.Normal);
                    copySuccess = true;
                    break;
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LoggerHelper.Warning($"复制文件失败，准备重试：第 {i + 1}/{maxRetries} 次，源文件={item.SourceFile}，目标文件={targetFile}，原因={ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LoggerHelper.Warning($"复制文件被拒绝，准备重试：第 {i + 1}/{maxRetries} 次，源文件={item.SourceFile}，目标文件={targetFile}，原因={ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            if (!copySuccess)
            {
                if (continueOnCopyFailure)
                {
                    LoggerHelper.Warning($"复制文件失败，已按继续模式跳过：源文件={item.SourceFile}，目标文件={targetFile}");
                    progressCounter.Current++;
                    DispatcherHelper.PostOnMainThread(() =>
                    {
                        if (progressBar == null || progressCounter.Total <= 0)
                            return;

                        var percentage = Math.Round((progressCounter.Current * 100.0) / progressCounter.Total, 1);
                        progressBar.Value = Math.Min(percentage, 100);
                    });
                    continue;
                }

                throw new IOException($"Failed to copy file after {maxRetries} attempts: {item.SourceFile} -> {targetFile}");
            }

            progressCounter.Current++;
            DispatcherHelper.PostOnMainThread(() =>
            {
                if (progressBar == null || progressCounter.Total <= 0)
                    return;

                var percentage = Math.Round((progressCounter.Current * 100.0) / progressCounter.Total, 1);
                progressBar.Value = Math.Min(percentage, 100);
            });
        }

        DispatcherHelper.PostOnMainThread(() => progressBar?.IsVisible = false);
    }

    /// <summary>
    /// 递归复制目录内容（核心异步逻辑）
    /// </summary>
    async private static Task CopyDirectoryRecursively(
        string sourceDir,
        string targetDir,
        string sourceRootDir,
        ProgressBar? progressBar,
        bool saveAnnouncement,
        CancellationToken cancellationToken,
        ProgressCounter progressCounter,
        bool skipCoreApplicationFiles)
    {
        // 响应取消请求
        cancellationToken.ThrowIfCancellationRequested();

        // 7. 自动创建目标目录（含所有父目录）
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // 8. 遍历并复制当前目录下的所有文件
        foreach (string sourceFile in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(sourceFile);
            string relativePath = Path.GetRelativePath(sourceRootDir, sourceFile);
            if (skipCoreApplicationFiles && IsCoreApplicationFile(relativePath))
            {
                LoggerHelper.Warning($"已跳过复制核心程序文件到资源目录：相对路径={relativePath}");
                continue;
            }
            string targetFile = Path.Combine(targetDir, fileName);

            // 9. 公告文件特殊处理（仅当 saveAnnouncement 为 true 时执行）
            if (saveAnnouncement
                && targetFile.Contains(AnnouncementViewModel.AnnouncementFolder, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(fileName).Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAnnouncementFile(sourceFile, targetFile, cancellationToken);
            }

            // 10 & 11. 异步复制文件（含重试机制）
            int maxRetries = 5;
            bool copySuccess = false;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tempTarget = targetFile + ".pending_update";
                        try
                        {
                            File.Copy(sourceFile, tempTarget, overwrite: true);
                        }
                        catch
                        {
                            if (File.Exists(tempTarget)) File.Delete(tempTarget);
                            throw;
                        }
                        if (File.Exists(targetFile))
                            DeleteFileWithBackup(targetFile);
                        File.Move(tempTarget, targetFile, overwrite: true);
                        RestoreUnixExecutablePermission(targetFile);
                    }, cancellationToken);

                    // 12. 设置目标文件为普通属性（清除只读/隐藏等限制）
                    File.SetAttributes(targetFile, FileAttributes.Normal);
                    copySuccess = true;
                    break;
                }
                catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // 文件被锁定时，记录警告并重试
                    LoggerHelper.Warning($"复制文件失败，准备重试：第 {i + 1}/{maxRetries} 次，源文件={sourceFile}，目标文件={targetFile}，原因={ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 权限不足时，记录警告并重试
                    LoggerHelper.Warning($"复制文件被拒绝，准备重试：第 {i + 1}/{maxRetries} 次，源文件={sourceFile}，目标文件={targetFile}，原因={ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            if (!copySuccess)
            {
                throw new IOException($"Failed to copy file after {maxRetries} attempts: {sourceFile} -> {targetFile}");
            }

            // 13. 更新进度条（线程安全）
            progressCounter.Current++;
            DispatcherHelper.PostOnMainThread(() =>
            {
                double percentage = Math.Round((progressCounter.Current * 100.0) / progressCounter.Total, 1);
                // 确保百分比不超过100（防止极端情况下的计算误差）
                progressBar.Value = Math.Min(percentage, 100);
            });
        }

        // 14. 递归处理所有子目录
        foreach (string sourceSubDir in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string subDirName = Path.GetFileName(sourceSubDir);
            string targetSubDir = Path.Combine(targetDir, subDirName);

            await CopyDirectoryRecursively(
                sourceSubDir, targetSubDir, sourceRootDir, progressBar, saveAnnouncement, cancellationToken, progressCounter, skipCoreApplicationFiles);
        }
    }

    /// <summary>
    /// 公告文件特殊处理（内容对比 + 配置更新）
    /// </summary>
    async private static Task HandleAnnouncementFile(
        string sourceFile,
        string targetFile,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetFile))
        {
            // 异步读取文件内容（避免IO阻塞）
            string sourceContent = await File.ReadAllTextAsync(sourceFile, cancellationToken);
            string destContent = await File.ReadAllTextAsync(targetFile, cancellationToken);

            // 内容不一致时，重置公告不显示配置
            if (!string.Equals(sourceContent, destContent, StringComparison.Ordinal))
            {
                GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, bool.FalseString);
            }
        }
        else
        {
            // 目标文件不存在，直接重置配置
            GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowAnnouncementAgain, bool.FalseString);
        }
    }

    /// <summary>
    /// 进度计数器（用于递归中共享进度状态）
    /// </summary>
    private class ProgressCounter
    {
        public int Current { get; set; } = 0;
        public int Total { get; set; } = 0;
    }

    private static void RestoreUnixExecutablePermission(string filePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(filePath) || !IsLikelyExecutablePath(filePath))
            return;

        try
        {
            var mode = File.GetUnixFileMode(filePath);
            File.SetUnixFileMode(filePath, mode
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"恢复 Unix 执行权限失败：文件={filePath}，原因={ex.Message}");
        }
    }

    private static bool IsLikelyExecutablePath(string filePath)
    {
        var normalizedPath = filePath.Replace('\\', '/');
        if (normalizedPath.Contains("/python/bin/", StringComparison.OrdinalIgnoreCase)
            || (normalizedPath.Contains("/runtimes/", StringComparison.OrdinalIgnoreCase)
                && normalizedPath.Contains("/native/", StringComparison.OrdinalIgnoreCase))
            || normalizedPath.Contains("/libs/MaaAgentBinary/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(filePath);
        return string.IsNullOrEmpty(extension)
            || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".command", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UpdateFileTransaction : IDisposable
    {
        private sealed record FileChange(string TargetPath, string? BackupPath);

        private readonly string _backupRoot;
        private readonly List<FileChange> _changes = [];
        private int _nextBackupId;
        private bool _committed;

        public UpdateFileTransaction(string tempRoot)
        {
            _backupRoot = Path.Combine(tempRoot, $"update_rollback_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_backupRoot);
        }

        public void DeleteFile(string targetPath)
        {
            if (!File.Exists(targetPath))
                return;

            var backupPath = NextBackupPath();
            File.SetAttributes(targetPath, FileAttributes.Normal);
            File.Move(targetPath, backupPath);
            _changes.Add(new FileChange(targetPath, backupPath));
        }

        public void ReplaceFile(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            var pendingPath = targetPath + ".pending_update";
            try
            {
                File.Copy(sourcePath, pendingPath, overwrite: true);
                cancellationToken.ThrowIfCancellationRequested();

                string? backupPath = null;
                if (File.Exists(targetPath))
                {
                    backupPath = NextBackupPath();
                    File.SetAttributes(targetPath, FileAttributes.Normal);
                    File.Move(targetPath, backupPath);
                }

                // Record the change before publishing the new file so rollback also covers a
                // failure between moving the old target and moving the pending file into place.
                _changes.Add(new FileChange(targetPath, backupPath));
                File.Move(pendingPath, targetPath, overwrite: true);
                RestoreUnixExecutablePermission(targetPath);
            }
            catch
            {
                if (File.Exists(pendingPath))
                    File.Delete(pendingPath);
                throw;
            }
        }

        public void WriteAllText(string targetPath, string content)
        {
            var stagedPath = Path.Combine(_backupRoot, $"staged_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(stagedPath, content);
            ReplaceFile(stagedPath, targetPath, CancellationToken.None);
        }

        public void TrackReplacement(string targetPath, string backupPath)
        {
            _changes.Add(new FileChange(targetPath, backupPath));
        }

        public void Commit()
        {
            _committed = true;
            TryDeleteBackupRoot();
        }

        public void Dispose()
        {
            if (!_committed)
            {
                if (Rollback())
                    TryDeleteBackupRoot();
                return;
            }

            TryDeleteBackupRoot();
        }

        private string NextBackupPath() =>
            Path.Combine(_backupRoot, $"{_nextBackupId++:D8}.bak");

        private bool Rollback()
        {
            var succeeded = true;
            LoggerHelper.Warning($"更新未完成，开始回滚已修改文件：数量={_changes.Count}");
            for (var index = _changes.Count - 1; index >= 0; index--)
            {
                var change = _changes[index];
                try
                {
                    if (File.Exists(change.TargetPath))
                    {
                        File.SetAttributes(change.TargetPath, FileAttributes.Normal);
                        File.Delete(change.TargetPath);
                    }

                    if (change.BackupPath != null && File.Exists(change.BackupPath))
                    {
                        var targetDirectory = Path.GetDirectoryName(change.TargetPath);
                        if (!string.IsNullOrWhiteSpace(targetDirectory))
                            Directory.CreateDirectory(targetDirectory);
                        File.Move(change.BackupPath, change.TargetPath, overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    LoggerHelper.Error($"回滚更新文件失败：目标={change.TargetPath}，备份={change.BackupPath}，原因={ex.Message}", ex);
                }
            }

            if (!succeeded)
                LoggerHelper.Error($"部分更新文件回滚失败，已保留回滚目录供手动恢复：目录={_backupRoot}");
            return succeeded;
        }

        private void TryDeleteBackupRoot()
        {
            try
            {
                if (Directory.Exists(_backupRoot))
                    Directory.Delete(_backupRoot, recursive: true);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"清理更新回滚目录失败：目录={_backupRoot}，原因={ex.Message}");
            }
        }
    }

    static void DeleteFileWithBackup(string filePath)
    {
        if (IsCurrentProcessExecutable(filePath))
        {
            LoggerHelper.Warning($"已阻止将当前正在运行的主程序文件改名为 backupMFA：文件={filePath}");
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"删除文件失败，准备尝试备份：文件={filePath}，原因={e.Message}");
            int index = 0;
            string currentDate = DateTime.Now.ToString("yyyyMMddHHmm");
            string backupFilePath = $"{filePath}.{currentDate}.{index}.backupMFA";

            while (File.Exists(backupFilePath))
            {
                index++;
                backupFilePath = $"{filePath}.{currentDate}.{index}.backupMFA";
            }

            try
            {
                File.Move(filePath, backupFilePath);
                LoggerHelper.Info($"文件备份成功：源文件={filePath}，备份文件={backupFilePath}");
            }
            catch (Exception e1)
            {
                // 文件被锁定时，记录错误但不抛出异常，让更新流程继续
                LoggerHelper.Warning($"移动文件到备份位置失败，已跳过该文件：源文件={filePath}，目标文件={backupFilePath}，原因={e1.Message}");
            }
        }
    }

    public async static Task UpdateMFA(bool isGithub, bool noDialog = false)
    {
        Instances.RootViewModel.SetUpdating(true);
        ProgressBar? progress = null;
        TextBlock? textBlock = null;
        ISukiToast? sukiToast = null;

        // 初始化进度UI
        DispatcherHelper.PostOnMainThread(() =>
        {
            progress = new ProgressBar
            {
                Value = 0,
                Margin = new Thickness(0, 5),
                ShowProgressText = true
            };
            textBlock = new TextBlock
            {
                Text = LangKeys.GettingLatestSoftware.ToLocalization()
            };

            var stackPanel = new StackPanel();
            stackPanel.Children.Add(textBlock);
            stackPanel.Children.Add(progress);

            sukiToast = Instances.ToastManager.CreateToast()
                .WithTitle(LangKeys.SoftwareUpdate.ToLocalization())
                .WithContent(stackPanel)
                .Queue();
        });

        try
        {
            SetProgress(progress, 10);

            // 获取版本信息
            string downloadUrl, latestVersion, sha256;
            try
            {
                if (isGithub)
                {
                    var result = await GetLatestVersionAndDownloadUrlFromGithubAsync().ConfigureAwait(false);
                    downloadUrl = result.url;
                    latestVersion = result.latestVersion;
                    sha256 = result.sha256;
                }
                else
                    GetDownloadUrlFromMirror(GetLocalVersion(), "MFAAvalonia", CDK(), out downloadUrl, out latestVersion, out sha256, out _, isUI: true);
            }
            catch (Exception ex)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn($"{LangKeys.FailToGetLatestVersionInfo.ToLocalization()}", ex.Message);
                LoggerHelper.Error($"获取 MFA 下载信息失败：source={(isGithub ? "GitHub" : "Mirror")}, reason={ex.Message}", ex);
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            // 版本验证
            SetProgress(progress, 50);
            var mirrocS = false;
            if (IsNewVersionAvailable(latestVersion, GetMaxVersion()))
            {
                latestVersion = GetMaxVersion();
                if (isGithub)
                {
                    var result = await GetLatestVersionAndDownloadUrlFromGithubAsync(targetVersion: latestVersion).ConfigureAwait(false);
                    downloadUrl = result.url;
                    sha256 = result.sha256;
                }
                else
                {
                    mirrocS = true;
                }
            }

            if (!IsNewVersionAvailable(latestVersion, GetLocalVersion()))
            {
                Dismiss(sukiToast);
                ToastHelper.Info(LangKeys.MFAIsLatestVersion.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            else if (mirrocS)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.SwitchUiUpdateSourceToGithub.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }


            // 准备临时目录
            var tempPath = AppPaths.TempMfaDirectory;
            Directory.CreateDirectory(tempPath);

            // 下载更新包
            SetText(textBlock, LangKeys.Downloading.ToLocalization());
            SetProgress(progress, 0);
            var tempZip = Path.Combine(tempPath, $"mfa_{latestVersion}{(OperatingSystem.IsAndroid() ? ".apk" : ".zip")}");
            (var downloadStatus, tempZip) = await DownloadWithRetry(downloadUrl, tempZip, progress, 3);
            if (!downloadStatus)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.DownloadFailed.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            // 解压文件
            SetProgress(progress, 20);
            var extractDir = Path.Combine(tempPath, $"mfa_{latestVersion}_extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            SetText(textBlock, LangKeys.Verifying.ToLocalization());
            var sha256Verified = true;
            if (string.IsNullOrWhiteSpace(sha256))
            {
                LoggerHelper.Warning($"已跳过 SHA256 校验：文件={tempZip}，原因=未提供校验值");
            }
            else
            {
                sha256Verified = await VerifyFileSha256Async(tempZip, sha256);
                LoggerHelper.Info($"SHA256 校验结果：文件={tempZip}，通过={sha256Verified}");
            }
            if (!string.IsNullOrWhiteSpace(sha256) && !sha256Verified)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.HashVerificationFailed.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            if (OperatingSystem.IsAndroid())
            {
                if (!downloadUrl.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(sha256) || !sha256Verified ||
                    PlatformApplicationRestart.InstallApkAsync == null)
                {
                    Dismiss(sukiToast);
                    ToastHelper.Warn(LangKeys.Warning.ToLocalization(), "Android Mirror 更新必须提供 APK 和有效 SHA256 校验值。", -1);
                    Instances.RootViewModel.SetUpdating(false);
                    return;
                }
                SetText(textBlock, LangKeys.ApplyingUpdate.ToLocalization());
                SetProgress(progress, 100);
                Instances.RootViewModel.SetUpdating(false);
                Dismiss(sukiToast);
                await PlatformApplicationRestart.InstallApkAsync(tempZip);
                return;
            }
            SetText(textBlock, LangKeys.Extracting.ToLocalization());
            UniversalExtractor.Extract(tempZip, extractDir);

            SetText(textBlock, LangKeys.ApplyingUpdate.ToLocalization());
            // 执行更新
            SetProgress(progress, 40);

            // 准备备份目录
            var backupDir = Path.Combine(AppPaths.BackupDirectory, DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupDir);

            // 执行文件替换
            SetProgress(progress, 60);
            await ReplaceFilesWithRetry(extractDir, backupDir);

            SetProgress(progress, 100);

            // 获取当前可执行文件名以重启
            string exeName = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(exeName) || !File.Exists(exeName))
            {
                // 如果获取失败，尝试构建默认路径
                var processName = Process.GetCurrentProcess().ProcessName;
                var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
                exeName = Path.Combine(AppContext.BaseDirectory, processName + extension);
            }

            // 在重启前给一点时间
            await Task.Delay(500);

            await RestartApplicationAsync(exeName);
        }
        finally
        {
            Instances.RootViewModel.SetUpdating(false);
            Dismiss(sukiToast);
            if (shouldShowToast)
            {
                DispatcherHelper.PostOnMainThread(() =>
                {
                    Instances.DialogManager.CreateDialog().WithContent(LangKeys.GameResourceUpdated.ToLocalization()).WithActionButton(LangKeys.Yes.ToLocalization(), _ =>
                        {
                            Process.Start(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
                            Instances.ShutdownApplication();
                        }, dismissOnClick: true, "Flat", "Accent")
                        .WithActionButton(LangKeys.No.ToLocalization(), _ =>
                        {
                            Dismiss(sukiToast);
                        }, dismissOnClick: true).TryShow();
                });
                shouldShowToast = false;
            }
        }
    }

    [Obsolete("旧的外部更新器探测逻辑，已废弃；不要在新的本地二合一更新链路中使用。")]
    private static string GetVersionFromCommand(string filePath)
    {
        try
        {
            LoggerHelper.Info($"目标更新器路径：文件={filePath}，存在={File.Exists(filePath)}");
            using var process = Process.Start(new ProcessStartInfo
            {
                WorkingDirectory = AppContext.BaseDirectory,
                FileName = filePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                process.WaitForExit();
                var output = process.StandardOutput.ReadToEnd().Trim();
                LoggerHelper.Info("获取到的版本信息: " + output);
                return Version.TryParse(output, out var version) ? version.ToString() : "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }

    #region 增强型更新核心方法

    async private static Task<(bool, string)> DownloadWithRetry(
        string url,
        string savePath,
        ProgressBar? progress,
        int retries,
        TextBlock? downloadSizeTextBlock = null,
        TextBlock? downloadSpeedTextBlock = null)
    {
        for (int i = 0; i < retries; i++)
        {
            try
            {
                return await DownloadFileAsync(url, savePath, progress, downloadSizeTextBlock, downloadSpeedTextBlock);
            }
            catch (WebException ex) when (i < retries - 1)
            {
                LoggerHelper.Warning($"下载失败，准备重试：第 {i + 1}/{retries} 次，状态={ex.Status}");
                await Task.Delay(2000 * (i + 1));
            }
        }
        return (false, savePath);
    }

    private static string BuildArguments(string source, string target, string oldName, string newName)
    {
        var args = new List<string>
        {
            EscapeArgument(source),
            EscapeArgument(target)
        };

        if (!string.IsNullOrWhiteSpace(oldName))
            args.Add(EscapeArgument(oldName));

        if (!string.IsNullOrWhiteSpace(newName))
            args.Add(EscapeArgument(newName));

        return string.Join(" ", args);
    }

    // 处理含空格的参数
    private static string EscapeArgument(string arg) => $"\"{arg.Replace("\"", "\\\"")}\"";

    [Obsolete("旧的外部更新器链路，已废弃；新的本地二合一更新不应再调用此方法。")]
    async private static Task ApplySecureUpdate(string source, string target, string oldName = "", string newName = "")
    {
        source = Path.GetFullPath(source).Replace('\\', Path.DirectorySeparatorChar);
        target = Path.GetFullPath(target).Replace('\\', Path.DirectorySeparatorChar);

        target = target.TrimEnd('\\', '/');
        source = source.TrimEnd('\\', '/');

        string updaterPath = FindUpdaterExecutablePath(AppContext.BaseDirectory);

        if (!File.Exists(updaterPath))
        {
            LoggerHelper.Error($"更新器文件不存在：目录={AppContext.BaseDirectory}");
            throw new FileNotFoundException("更新程序源文件未找到");
        }

        // 仅执行一次chmod（修复重复执行问题）
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var chmodProcess = Process.Start("/bin/chmod", $"+x {updaterPath}");
            // 增强错误处理：检查进程是否启动成功
            if (chmodProcess == null)
            {
                LoggerHelper.Error("无法启动 chmod 进程，可能缺少权限。");
                throw new InvalidOperationException("设置更新器执行权限失败");
            }
            await chmodProcess.WaitForExitAsync();
            if (chmodProcess.ExitCode != 0)
            {
                LoggerHelper.Error($"chmod 执行失败：退出码={chmodProcess.ExitCode}");
                throw new InvalidOperationException("设置更新器执行权限失败");
            }
        }

        // 获取当前进程PID，传递给更新器（关键修改）
        var currentProcessId = Process.GetCurrentProcess().Id;

        // 构建命令参数
        var arguments = $"{BuildArguments(source, target, oldName, newName)} {EscapeArgument(currentProcessId.ToString())}";

        LoggerHelper.Info($"准备启动更新器：文件={updaterPath}，参数={arguments}");

        try
        {
            // 专门针对macOS系统的特殊处理
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: 使用nohup启动完全独立的后台进程
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"cd '{AppContext.BaseDirectory}' && nohup '{updaterPath}' {arguments} > /dev/null 2>&1 &\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                LoggerHelper.Info($"macOS shell 启动命令：/bin/sh \"cd '{AppContext.BaseDirectory}' && nohup '{updaterPath}' {arguments} > /dev/null 2>&1 &\"");

                using var shellProcess = Process.Start(psi);
                if (shellProcess?.HasExited == false)
                {
                    LoggerHelper.Info($"更新器已通过 macOS shell 启动：方式=nohup，进程ID={shellProcess.Id}");
                }
            }
            else
            {
                // 其他系统：保持原有逻辑

                var psi = new ProcessStartInfo
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    FileName = updaterPath, // 使用完整路径
                    Arguments = $"{BuildArguments(source, target, oldName, newName)} {EscapeArgument(currentProcessId.ToString())}",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                LoggerHelper.Info($"更新器启动命令：{updaterPath} {BuildArguments(source, target, oldName, newName)} {EscapeArgument(currentProcessId.ToString())}");

                using var updaterProcess = Process.Start(psi);
                if (updaterProcess?.HasExited == false)
                {
                    LoggerHelper.Info($"更新器已启动：进程ID={updaterProcess.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"启动更新器失败：原因={ex.Message}", ex);
            throw;
        }
        finally
        {
            // 给更新器一些时间启动
            await Task.Delay(1000);
            DispatcherHelper.PostOnMainThread(Instances.RootView.BeforeClosed);
            Instances.ShutdownApplication();
        }
    }


    private static string CreateVersionBackup(string dir)
    {
        var backupPath = Path.Combine(AppContext.BaseDirectory, dir);

        Directory.CreateDirectory(backupPath);
        return backupPath;
    }

    async private static Task ReplaceFilesWithRetry(string sourceDir, string backupDir, int maxRetry = 3)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(AppContext.BaseDirectory, relativePath);
            var backupPath = Path.Combine(backupDir, relativePath);

            for (int i = 0; i < maxRetry; i++)
            {
                try
                {
                    if (File.Exists(targetPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                        File.Move(targetPath, backupPath, overwrite: true);
                    }
                    // 确保目标目录存在
                    var destDir = Path.GetDirectoryName(targetPath);
                    if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

                    File.Move(file, targetPath, overwrite: true);
                    break;
                }
                catch (IOException ex) when (i < maxRetry - 1)
                {
                    await Task.Delay(1000 * (i + 1));
                    LoggerHelper.Warning($"文件替换重试: {ex.Message}");
                }
            }
        }
    }

    #endregion

    public async static Task UpdateMaaFw()
    {
        Instances.RootViewModel.SetUpdating(true);
        ProgressBar? progress = null;
        TextBlock? textBlock = null;
        ISukiToast? sukiToast = null;

        // UI初始化（与原有逻辑保持一致）
        DispatcherHelper.PostOnMainThread(() =>
        {
            progress = new ProgressBar
            {
                Value = 0,
                Margin = new Thickness(0, 5),
                ShowProgressText = true
            };
            textBlock = new TextBlock
            {
                Text = LangKeys.GettingLatestMaaFW.ToLocalization()
            };
            var stackPanel = new StackPanel();
            stackPanel.Children.Add(textBlock);
            stackPanel.Children.Add(progress);
            sukiToast = Instances.ToastManager.CreateToast()
                .WithTitle(LangKeys.UpdateMaaFW.ToLocalization())
                .WithContent(stackPanel).Queue();
        });

        try
        {
            // 版本信息获取（保持原有逻辑）
            SetProgress(progress, 10);
            var resId = "MaaFramework";
            var currentVersion = MaaUtility.MaaVersion();
            string downloadUrl = string.Empty, latestVersion = string.Empty, sha256 = string.Empty;
            try
            {
                GetDownloadUrlFromMirror(currentVersion, resId, CDK(), out downloadUrl, out latestVersion, out sha256, out _, "MFA", true);
            }
            catch (Exception ex)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn($"{LangKeys.FailToGetLatestVersionInfo.ToLocalization()}", ex.Message, -1);
                LoggerHelper.Error($"获取资源更新地址失败：来源=Mirror，资源ID={resId}，当前版本={currentVersion}，原因={ex.Message}", ex);
                Instances.RootViewModel.SetUpdating(false);
                return;
            }
            // 版本校验（保持原有逻辑）
            SetProgress(progress, 50);
            if (!IsNewVersionAvailable(latestVersion, currentVersion))
            {
                Dismiss(sukiToast);
                ToastHelper.Info(LangKeys.MaaFwIsLatestVersion.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            // 下载与解压（优化为使用DownloadWithRetry）
            var tempPath = AppPaths.TempMaaFwDirectory;
            Directory.CreateDirectory(tempPath);
            var tempZip = Path.Combine(tempPath, $"maafw_{latestVersion}.zip");
            SetText(textBlock, LangKeys.Downloading.ToLocalization());
            (var downloadStatus, tempZip) = await DownloadWithRetry(downloadUrl, tempZip, progress, 3);
            if (!downloadStatus)
            {
                Dismiss(sukiToast);
                ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.DownloadFailed.ToLocalization());
                Instances.RootViewModel.SetUpdating(false);
                return;
            }

            SetText(textBlock, LangKeys.ApplyingUpdate.ToLocalization());
            // 文件替换（复用ReplaceFilesWithRetry）
            SetProgress(progress, 0);
            var extractDir = Path.Combine(tempPath, $"maafw_{latestVersion}_extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(tempZip, extractDir);
            SetProgress(progress, 20);

            var utf8Bytes = Encoding.UTF8.GetBytes(AppContext.BaseDirectory);
            var utf8BaseDirectory = Encoding.UTF8.GetString(utf8Bytes);
            var sourceBytes = Encoding.UTF8.GetBytes(Path.Combine(extractDir, "bin"));
            var sourceDirectory = Encoding.UTF8.GetString(sourceBytes);
            SetProgress(progress, 100);

            // 清理与重启（复用ApplySecureUpdate）
            await ApplySecureUpdate(sourceDirectory, utf8BaseDirectory, Process.GetCurrentProcess().MainModule.ModuleName);
        }
        finally
        {
            Instances.RootViewModel.SetUpdating(false);
            Dismiss(sukiToast);
        }
    }

    async private static Task ExecuteTasksAsync()
    {
        try
        {
            while (Queue.TryDequeue(out var task))
            {
                await _queueLock.WaitAsync();
                try
                {
                    LoggerHelper.Info($"开始执行更新任务：名称={task.Name}");
                    await task.Action();
                    LoggerHelper.Info($"更新任务完成：名称={task.Name}");
                }
                finally
                {
                    _queueLock.Release();
                }
            }
        }
        finally
        {
            Instances.RootViewModel.SetUpdating(false);
        }
    }


    public static async Task<(string url, string latestVersion, string sha256)> GetLatestVersionAndDownloadUrlFromGithubAsync(
        string owner = "MaaXYZ",
        string repo = "MFAAvalonia",
        bool onlyCheck = false,
        string targetVersion = "",
        string currentVersion = "v0.0.0")
    {
        var versionType = repo.Equals("MFAAvalonia", StringComparison.OrdinalIgnoreCase)
            ? Instances.VersionUpdateSettingsUserControlModel.UIUpdateChannelIndex.ToVersionType()
            : Instances.VersionUpdateSettingsUserControlModel.ResourceUpdateChannelIndex.ToVersionType();
        string url = string.Empty;
        string latestVersion = string.Empty;
        string sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return (url, latestVersion, sha256);

        var releaseUrl = $"https://api.github.com/repos/{owner}/{repo}/releases";
        int page = 1;
        const int perPage = 30;
        using var httpClient = CreateHttpClientWithProxy();

        if (!string.IsNullOrWhiteSpace(Instances.VersionUpdateSettingsUserControlModel.GitHubToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Instances.VersionUpdateSettingsUserControlModel.GitHubToken);
        }

        httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("request");
        httpClient.DefaultRequestHeaders.Accept.TryParseAdd("application/json");

        // 用于存储找到的最佳版本
        JToken? bestRelease = null;
        string bestVersion = string.Empty;

        while (page < 101)
        {
            var urlWithParams = $"{releaseUrl}?per_page={perPage}&page={page}";
            try
            {
                var response = await httpClient.GetAsync(urlWithParams).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var tags = JArray.Parse(json);
                    if (tags.Count == 0)
                    {
                        break;
                    }
                    foreach (var tag in tags)
                    {
                        // 检查是否为预发布版本
                        if ((bool)tag["prerelease"] && versionType == VersionType.Stable)
                        {
                            continue;
                        }

                        var tagVersion = tag["tag_name"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrEmpty(tagVersion)) continue;
                        // 检查版本类型是否符合更新渠道
                        var isAlpha = tagVersion.Contains("alpha", StringComparison.OrdinalIgnoreCase);
                        var isBeta = tagVersion.Contains("beta", StringComparison.OrdinalIgnoreCase);

                        // Alpha渠道：接受所有版本（alpha、beta、stable）
                        // Beta渠道：接受beta和stable版本，不接受alpha
                        // Stable渠道：只接受stable版本，不接受alpha和beta
                        if (isAlpha && versionType != VersionType.Alpha)
                        {
                            continue;
                        }
                        if (isBeta && versionType == VersionType.Stable)
                        {
                            continue;
                        }

                        // 如果指定了目标版本，直接查找该版本
                        if (!string.IsNullOrEmpty(targetVersion) && tagVersion.Trim().Equals(targetVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            latestVersion = tagVersion;
                            if (IsNewVersionAvailable(latestVersion, currentVersion))
                            {
                                if (onlyCheck && repo != "MFAAvalonia")
                                    SaveRelease(tag, "body");
                                if (!onlyCheck && repo != "MFAAvalonia")
                                    SaveChangelog(tag, "body");
                            }
                            (url, sha256) = await GetDownloadUrlFromGitHubReleaseAsync(latestVersion, owner, repo).ConfigureAwait(false);
                            return (url, latestVersion, sha256);
                        }

                        // 比较版本，找到符合条件的最新版本
                        if (string.IsNullOrEmpty(targetVersion))
                        {
                            if (string.IsNullOrEmpty(bestVersion) || IsNewVersionAvailable(tagVersion, bestVersion))
                            {
                                bestVersion = tagVersion;
                                bestRelease = tag;
                            }
                        }
                    }
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden && response.ReasonPhrase?.Contains("403") == true)
                {
                    LoggerHelper.Error("GitHub API 速率限制已超出，请稍后再试。");
                    throw new Exception("GitHub API速率限制已超出，请稍后再试。");
                }
                else
                {
                    LoggerHelper.Error($"请求 GitHub 失败：状态码={(int)response.StatusCode} {response.StatusCode}，原因={response.ReasonPhrase}");
                    throw new Exception($"请求 GitHub 失败：状态码={(int)response.StatusCode} {response.StatusCode}，原因={response.ReasonPhrase}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LoggerHelper.Error($"处理 GitHub 响应失败：原因={e.Message}", e);
                throw new Exception($"处理 GitHub 响应失败：原因={e.Message}");
            }
            page++;
        }

        // 如果找到了最佳版本，返回它
        if (!string.IsNullOrEmpty(bestVersion) && bestRelease != null)
        {
            latestVersion = bestVersion;
            if (IsNewVersionAvailable(latestVersion, currentVersion))
            {
                if (onlyCheck && repo != "MFAAvalonia")
                    SaveRelease(bestRelease, "body");
                if (!onlyCheck && repo != "MFAAvalonia")
                    SaveChangelog(bestRelease, "body");
            }
            (url, sha256) = await GetDownloadUrlFromGitHubReleaseAsync(latestVersion, owner, repo).ConfigureAwait(false);
        }
        return (url, latestVersion, sha256);
    }

    private static string ExtractSha256FromDigest(string? digest)
    {
        if (string.IsNullOrEmpty(digest))
            return string.Empty;

        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            // 提取冒号后的部分
            return digest.Substring(7);
        }

        return digest;
    }

    // 标准化操作系统标识
    private static (string os, string family) GetNormalizedOSInfo()
    {
        if (OperatingSystem.IsAndroid())
            return ("android", "android");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("win", "windows");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            // macOS/OS X 既属于"osx"具体系统，也属于"unix"家族
            return ("osx", "unix");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            // Linux 属于"linux"具体系统，也属于"unix"家族
            return ("linux", "unix");

        // 其他类Unix系统（如FreeBSD）
        if (IsUnixLike())
            return ("unix", "unix");

        return ("unknown", "unknown");
    }

    // 辅助判断：是否为类Unix系统（非Windows）
    private static bool IsUnixLike()
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "linux"
                    : "unknown";
        return platform != "windows" && platform != "unknown";
    }

    public static string GetNormalizedArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64", // 保持x64，不强制转为x86_64
            Architecture.Arm64 => "arm64", // 保持arm64，不强制转为aarch64
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown"
        };
    }

    private static readonly Dictionary<string, List<string>> ArchitectureAliases = new()
    {
        {
            "x64", new List<string>
            {
                "x64",
                "x86_64"
            }
        }, // x64支持x86_64别名
        {
            "arm64", new List<string>
            {
                "arm64",
                "aarch64"
            }
        }, // arm64支持aarch64别名
        {
            "x86", new List<string>
            {
                "x86"
            }
        }, // x86保持默认
        {
            "arm", new List<string>
            {
                "arm"
            }
        } // arm保持默认
    };

    private static int GetAssetPriority(string fileName, string targetOS, string targetFamily, string targetArch)
    {
        if (string.IsNullOrEmpty(fileName)) return 0;
        fileName = fileName.ToLower();

        // 系统别名映射（保留原有定义）
        var osAliases = new Dictionary<string, List<string>>
        {
            {
                "osx", new List<string>
                {
                    "osx",
                    "macos",
                    "mac"
                }
            },
            {
                "linux", new List<string>
                {
                    "linux",
                    "debian",
                    "ubuntu"
                }
            },
            {
                "unix", new List<string>
                {
                    "unix",
                    "bsd",
                    "freebsd"
                }
            },
            {
                "win", new List<string>
                {
                    "win",
                    "windows"
                } // 补充Windows别名，避免遗漏
            }
        };

        // 处理架构别名：将目标架构转为包含所有别名的正则模式（如x64→x64|x86_64）
        string archWithAliases = ArchitectureAliases.TryGetValue(targetArch, out var archAliases)
            ? string.Join("|", archAliases)
            : targetArch;

        // 优先级规则：全部通过GetPattern生成，确保复用逻辑
        var patterns = new List<(string pattern, int priority)>
        {
            // 1. 具体系统+架构（含别名）完全匹配（如win-x64、win-x86_64）
            (GetPattern(targetOS, archWithAliases, osAliases), 100),
            // 2. 具体系统匹配（任意架构，用.*表示通配符）
            (GetPattern(targetOS, ".*", osAliases), 80),
            // 3. 家族+架构（含别名）匹配（如unix-arm64、unix-aarch64）
            (GetPattern(targetFamily, archWithAliases, osAliases), 60),
            // 4. 家族匹配（任意架构，用.*表示通配符）
            (GetPattern(targetFamily, ".*", osAliases), 40),
            // 5. 仅架构（含别名）匹配（如-x64、-x86_64）
            ($"-(?:{archWithAliases})", 20)
        };

        // 遍历规则计算优先级
        int basePriority = 0;
        foreach (var (pattern, priority) in patterns)
        {
            if (pattern != null && Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase))
            {
                basePriority = priority;
                break;
            }
        }

        // 文件名包含 "MFA" 时，额外加最高档分数（100）
        if (fileName.Contains("MFA"))
        {
            basePriority += 100;
        }

        return basePriority;
    }

    // 辅助方法：生成匹配模式（支持别名）
    private static string GetPattern(string osOrFamily, string arch, Dictionary<string, List<string>> aliases)
    {
        if (aliases.TryGetValue(osOrFamily, out var aliasList))
        {
            var allIdentifiers = new HashSet<string>(aliasList)
            {
                osOrFamily
            };
            var identifiersPattern = string.Join("|", allIdentifiers);
            // 关键：用 \b 或 ^ 限定系统标识在开头或 - 之后，避免跨系统匹配
            return $@"\b(?:{identifiersPattern})-(?:{arch})\b";
        }
        return $@"\b{osOrFamily}-{arch}\b";
    }

    private static async Task<(string downloadUrl, string sha256)> GetDownloadUrlFromGitHubReleaseAsync(string version, string owner, string repo)
    {
        string downloadUrl = string.Empty;
        string sha256 = string.Empty;
        // 获取系统信息（具体系统 + 家族）
        var (osPlatform, osFamily) = GetNormalizedOSInfo();
        var cpuArch = GetNormalizedArchitecture();
        LoggerHelper.Info($"目标系统：平台={osPlatform}，系统家族={osFamily}，架构={cpuArch}");

        var releaseUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{version}";
        using var httpClient = CreateHttpClientWithProxy();
        httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("MFAComponentUpdater/1.0");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        if (!string.IsNullOrWhiteSpace(Instances.VersionUpdateSettingsUserControlModel.GitHubToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Instances.VersionUpdateSettingsUserControlModel.GitHubToken);
        }

        try
        {
            var response = await httpClient.GetAsync(releaseUrl).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var releaseData = JObject.Parse(jsonResponse);

                if (releaseData["assets"] is JArray { Count: > 0 } assets)
                {
                    var orderedAssets = assets
                        .Select(asset => new
                        {
                            // The API asset URL supports authenticated downloads for private repositories.
                            // Keep browser_download_url as a fallback for older/non-standard API responses.
                            Url = asset["url"]?.ToString() ?? asset["browser_download_url"]?.ToString(),
                            Name = asset["name"]?.ToString().ToLower(),
                            Sha256 = ExtractSha256FromDigest(asset["digest"]?.ToString())
                        })
                        // 使用新的优先级计算方法（传入系统家族）
                        .OrderByDescending(a => GetAssetPriority(a.Name, osPlatform, osFamily, cpuArch))
                        .ToList();

                    // 输出调试日志（查看每个资产的优先级）
                    foreach (var asset in orderedAssets)
                    {
                        int priority = GetAssetPriority(asset.Name, osPlatform, osFamily, cpuArch);
                        LoggerHelper.Info($"候选资产优先级：名称={asset.Name}，优先级={priority}");
                    }

                    var bestAsset = OperatingSystem.IsAndroid()
                        ? orderedAssets.FirstOrDefault(a =>
                            a.Url != null
                            && a.Name?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true
                            && GetAssetPriority(a.Name, osPlatform, osFamily, cpuArch) > 0)
                        : orderedAssets.FirstOrDefault(a => a.Url != null);
                    downloadUrl = bestAsset?.Url ?? string.Empty;
                    sha256 = bestAsset?.Sha256 ?? string.Empty;
                }
            }
            else
            {
                LoggerHelper.Error($"请求 GitHub 失败：状态码={(int)response.StatusCode} {response.StatusCode}，原因={response.ReasonPhrase}");
                throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoggerHelper.Error($"处理 GitHub 响应失败：原因={e.Message}", e);
            throw;
        }
        return (downloadUrl, sha256);
    }


    private static void GetDownloadUrlFromMirror(string version,
        string resId,
        string cdk,
        out string url,
        out string latestVersion,
        out string sha256,
        out bool isFull,
        string userAgent = "MFA",
        bool isUI = false,
        bool onlyCheck = false,
        string currentVersion = "v0.0.0",
        bool showResponse = false,
        bool saveAnnouncement = true
    )
    {
        var versionType = isUI ? Instances.VersionUpdateSettingsUserControlModel.UIUpdateChannelIndex.ToVersionType() : Instances.VersionUpdateSettingsUserControlModel.ResourceUpdateChannelIndex.ToVersionType();
        if (string.IsNullOrWhiteSpace(resId))
        {
            throw new Exception(LangKeys.CurrentResourcesNotSupportMirror.ToLocalization());
        }
        if (string.IsNullOrWhiteSpace(cdk) && !onlyCheck)
        {
            throw new Exception(LangKeys.MirrorCdkEmpty.ToLocalization());
        }
        var cdkD = onlyCheck ? string.Empty : $"cdk={cdk}&";
        var multiplatform = MaaProcessor.Interface?.Multiplatform == true;
        var os = OperatingSystem.IsAndroid() ? "android" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "unknown";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "386",
            Architecture.X64 => "amd64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "unknown"
        };
        var channel = versionType.GetName();
        var multiplatformString = multiplatform ? $"os={os}&arch={arch}&" : "";
        var current_version = version == "v0.0.0" ? "" : $"&current_version={version}";
        var releaseUrl = isUI
            ? $"https://mirrorchyan.com/api/resources/{resId}/latest?channel={channel}{current_version}&{cdkD}os={os}&arch={arch}&user_agent={userAgent}"
            : $"https://mirrorchyan.com/api/resources/{resId}/latest?channel={channel}{current_version}&{cdkD}{multiplatformString}user_agent={userAgent}";
        using var httpClient = CreateHttpClientWithProxy();
        httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("request");
        httpClient.DefaultRequestHeaders.Accept.TryParseAdd("application/json");

        try
        {

            var response = httpClient.GetAsync(releaseUrl).Result;
            var jsonResponse = response.Content.ReadAsStringAsync().Result;
            var responseData = JObject.Parse(jsonResponse);
            if (!onlyCheck)
                LoggerHelper.Info($"镜像接口原始响应摘要：长度={jsonResponse.Length}");
            Exception? exception = null;
            // 处理 HTTP 状态码
            if (!response.IsSuccessStatusCode)
            {
                exception = HandleHttpError(response.StatusCode, responseData);
            }

            // 处理业务错误码
            var responseCode = (int)responseData["code"]!;
            if (responseCode != 0)
            {
                HandleBusinessError(responseCode, responseData);
            }

            // 成功处理
            var data = responseData["data"]!;


            url = data["url"]?.ToString() ?? string.Empty;
            latestVersion = data["version_name"]?.ToString() ?? string.Empty;
            sha256 = data["sha256"]?.ToString() ?? string.Empty;
            var updateType = data["update_type"]?.ToString() ?? string.Empty;
            isFull = updateType.Equals("full", StringComparison.OrdinalIgnoreCase);
            LoggerHelper.Info($"Mirror 更新响应：rid={resId}，平台={os}，架构={arch}，版本={latestVersion}，类型={updateType}，返回平台={data["os"]}，返回架构={data["arch"]}，APK={url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)}，SHA256={(string.IsNullOrWhiteSpace(sha256) ? "缺失" : "存在")}");

            if (OperatingSystem.IsAndroid() && isUI && !url.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Android Mirror 更新资产不是 APK，已拒绝下载。");

            // 解析并存储 CDK 过期时间戳
            if (data["cdk_expired_time"] != null && long.TryParse(data["cdk_expired_time"]?.ToString(), out var cdkExpiredTime))
            {
                Instances.VersionUpdateSettingsUserControlModel.CdkExpiredTime = cdkExpiredTime;
                LoggerHelper.Info($"CDK 过期时间戳：{cdkExpiredTime}");
            }

            if (showResponse)
            {
                // 记录从 mirror 返回的关键信息
                LoggerHelper.Info($"镜像返回信息摘要：长度={jsonResponse.Length}");
                LoggerHelper.Info($"更新类型：类型={updateType}，是否全量更新={isFull}");
            }

            if (IsNewVersionAvailable(latestVersion, currentVersion) && saveAnnouncement)
            {
                if (onlyCheck && !isUI && data != null)
                {
                    SaveRelease(data, "release_note");
                }
                if (!onlyCheck && !isUI && data != null)
                {
                    SaveChangelog(data, "release_note");
                }
            }
            if (exception != null)
                throw exception;
        }
        catch (AggregateException ex) when (ex.InnerException is HttpRequestException httpEx)
        {
            throw new Exception($"NetworkError: {httpEx.Message}".ToLocalization());
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message);
        }
    }

    #region 错误处理逻辑

    private static Exception HandleHttpError(HttpStatusCode statusCode, JObject responseData)
    {
        var errorMsg = responseData["msg"]?.ToString() ?? LangKeys.UnknownError.ToLocalization();

        switch (statusCode)
        {
            case HttpStatusCode.BadRequest: // 400
                return new Exception($"InvalidRequest: {errorMsg}".ToLocalization());

            case HttpStatusCode.Forbidden: // 403
                return new Exception($"AccessDenied: {errorMsg}".ToLocalization());

            case HttpStatusCode.NotFound: // 404
                return new Exception($"ResourceNotFound: {errorMsg}".ToLocalization());

            default:
                return new Exception($"ServerError: [{(int)statusCode}] {errorMsg}".ToLocalization());
        }
    }

    private static void HandleBusinessError(int code, JObject responseData)
    {
        var errorMsg = responseData["msg"]?.ToString() ?? LangKeys.UndefinedError.ToLocalization();

        switch (code)
        {
            // 参数错误系列 (400)
            case 1001:
                throw new Exception($"InvalidParams: {errorMsg}".ToLocalization());

            // CDK 相关错误 (403)
            case 7001:
                throw new Exception("MirrorCdkExpired".ToLocalization());
            case 7002:
                throw new Exception("MirrorCdkInvalid".ToLocalization());
            case 7003:
                throw new Exception("MirrorUseLimitReached".ToLocalization());
            case 7004:
                throw new Exception("MirrorCdkMismatch".ToLocalization());
            case 7005:
                throw new Exception("MirrorCDKBanned".ToLocalization());
            // 资源相关错误 (404)
            case 8001:
                throw new Exception("CurrentResourcesNotSupportMirror".ToLocalization());

            // 参数校验错误 (400)
            case 8002:
                throw new Exception($"InvalidOS: {errorMsg}".ToLocalization());
            case 8003:
                throw new Exception($"InvalidArch: {errorMsg}".ToLocalization());
            case 8004:
                throw new Exception($"InvalidChannel: {errorMsg}".ToLocalization());

            // 未分类错误
            case 1:
                throw new Exception($"BusinessError: {errorMsg}".ToLocalization());

            default:
                throw new Exception($"UnknownErrorCode: [{code}] {errorMsg}".ToLocalization());
        }
    }

    #endregion

    private static string GetLocalVersion()
    {
        return RootViewModel.Version;
    }

    private static string GetResourceVersion()
    {
        return Instances.VersionUpdateSettingsUserControlModel.ResourceVersion;
    }


    private static string GetResourceID()
    {
        return MaaProcessor.Interface?.RID ?? string.Empty;
    }

    private static string GetMaxVersion()
    {
        return MaaProcessor.Interface?.MFAMaxVersion ?? string.Empty;
    }

    private static string GetMinVersion()
    {
        return MaaProcessor.Interface?.MFAMinVersion ?? string.Empty;
    }

    private static bool IsNewVersionAvailable(string latestVersion, string localVersion)
    {
        if (string.IsNullOrEmpty(latestVersion) || string.IsNullOrEmpty(localVersion))
            return false;
        try
        {
            var normalizedLatest = ParseAndNormalizeVersion(latestVersion);
            var normalizedLocal = ParseAndNormalizeVersion(localVersion);
            return normalizedLatest.ComparePrecedenceTo(normalizedLocal) > 0;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"比较版本号失败：latest={latestVersion}, local={localVersion}, reason={ex.Message}", ex);
            return false;
        }
    }

    private static SemVersion ParseAndNormalizeVersion(string version)
    {
        if (!version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            version = $"v{version}";
        var pattern = @"^[vV]?(?<major>\d+)(\.(?<minor>\d+))?(\.(?<patch>\d+))?(?:-(?<prerelease>[0-9a-zA-Z\-\.]+))?(?:\+(?<build>[0-9a-zA-Z\-\.]+))?$";
        var match = Regex.Match(version.Trim(), pattern);

        var major = match.Groups["major"].Success ? int.Parse(match.Groups["major"].Value) : 0;
        var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        var prerelease = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value.Split('.')
            : null;

        var build = match.Groups["build"].Success
            ? match.Groups["build"].Value.Split('.')
            : null;

        return new SemVersion(new BigInteger(major), new BigInteger(minor), new BigInteger(patch), prerelease, build);
    }

    async private static Task<(bool, string)> DownloadFileAsync(
        string url,
        string filePath,
        ProgressBar? progressBar,
        TextBlock? downloadSizeTextBlock = null,
        TextBlock? downloadSpeedTextBlock = null)
    {
        var targetFilePath = filePath;
        try
        {
            using var httpClient = CreateHttpClientWithProxy();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("request");
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsGitHubReleaseAssetApiUrl(url))
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                var gitHubToken = Instances.VersionUpdateSettingsUserControlModel.GitHubToken;
                if (!string.IsNullOrWhiteSpace(gitHubToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gitHubToken);
                }
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentDisposition != null)
            {
                var suggestedFileName = ParseFileNameFromContentDisposition(
                    response.Content.Headers.ContentDisposition.ToString());
                if (!string.IsNullOrEmpty(suggestedFileName))
                {
                    string dir = Path.GetDirectoryName(filePath)!;
                    string newFileName = Path.GetFileNameWithoutExtension(filePath) + Path.GetExtension(suggestedFileName);
                    targetFilePath = Path.Combine(dir, newFileName);
                }
            }

            var startTime = DateTime.Now;
            long totalBytesRead = 0;
            long bytesPerSecond = 0;
            long? totalBytes = response.Content.Headers.ContentLength;

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[8192];
            var stopwatch = Stopwatch.StartNew();
            var lastSpeedUpdateTime = startTime;
            long lastTotalBytesRead = 0;

            while (true)
            {
                var bytesRead = await contentStream.ReadAsync(buffer);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));

                totalBytesRead += bytesRead;
                var currentTime = DateTime.Now;


                var timeSinceLastUpdate = currentTime - lastSpeedUpdateTime;
                if (timeSinceLastUpdate.TotalSeconds >= 1)
                {
                    bytesPerSecond = (long)((totalBytesRead - lastTotalBytesRead) / timeSinceLastUpdate.TotalSeconds);
                    lastTotalBytesRead = totalBytesRead;
                    lastSpeedUpdateTime = currentTime;
                }


                double progressPercentage;
                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    progressPercentage = Math.Min((double)totalBytesRead / totalBytes.Value * 100, 100);
                }
                else
                {
                    if (bytesPerSecond > 0)
                    {
                        double estimatedTotal = totalBytesRead + bytesPerSecond * 5;
                        progressPercentage = Math.Min((double)totalBytesRead / estimatedTotal * 100, 99);
                    }
                    else
                    {
                        progressPercentage = Math.Min((currentTime - startTime).TotalSeconds / 30 * 100, 99);
                    }
                }

                SetProgress(progressBar, progressPercentage);
                if (stopwatch.ElapsedMilliseconds >= 100)
                {
                    SetDownloadInfo(downloadSizeTextBlock, downloadSpeedTextBlock, totalBytesRead, totalBytes ?? totalBytesRead, bytesPerSecond);
                    stopwatch.Restart();
                }
            }

            SetProgress(progressBar, 100);
            SetDownloadInfo(downloadSizeTextBlock, downloadSpeedTextBlock, totalBytesRead, totalBytes ?? totalBytesRead, bytesPerSecond);
            DispatcherHelper.PostOnMainThread(() =>
                Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.OutputDownloadProgress(
                    totalBytesRead,
                    totalBytes ?? totalBytesRead,
                    (int)bytesPerSecond,
                    (DateTime.Now - startTime).TotalSeconds
                ));

            return (true, targetFilePath);
        }
        catch (HttpRequestException httpEx)
        {
            LoggerHelper.Error($"HTTP 请求失败：原因={httpEx.Message}", httpEx);
            return (false, targetFilePath);
        }
        catch (IOException ioEx)
        {
            LoggerHelper.Error($"文件操作失败：原因={ioEx.Message}", ioEx);
            return (false, targetFilePath);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"下载文件时发生未知错误：原因={ex.Message}", ex);
            return (false, targetFilePath);
        }
    }

    private static bool IsGitHubReleaseAssetApiUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/releases/assets/", StringComparison.OrdinalIgnoreCase);
    }

    async private static Task<bool> VerifyFileSha256Async(string filePath, string expectedSha256)
    {
        if (string.IsNullOrEmpty(expectedSha256) || !File.Exists(filePath))
            return false;

        try
        {
            using var fileStream = File.OpenRead(filePath);
            using var sha256Algorithm = SHA256.Create();

            // 计算文件的SHA256哈希
            byte[] hashBytes = await sha256Algorithm.ComputeHashAsync(fileStream);
            string actualSha256 = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

            // 比较计算结果与预期值
            return actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"SHA256 校验失败：原因={ex.Message}", ex);
            return false;
        }
    }

    public class MirrorChangesJson
    {
        [JsonProperty("modified")] public List<string>? Modified;
        [JsonProperty("deleted")] public List<string>? Deleted;
        [JsonProperty("added")] public List<string>? Added;
        [JsonProperty("deleted_dir")] public List<string>? DeletedDirectories;
        [JsonProperty("added_dir")] public List<string>? AddedDirectories;
        [JsonExtensionData]
        public Dictionary<string, object>? AdditionalData { get; set; } = new();
    }

    private static bool ValidateExtractedResourcePackage(
        string tempExtractDir,
        bool isIncrementalPackage,
        out string originPath,
        out string interfacePath,
        out string resourceDirPath,
        out string validationMessage)
    {
        var candidateRoots = GetResourcePackageCandidateRoots(tempExtractDir);

        originPath = tempExtractDir;
        interfacePath = Path.Combine(tempExtractDir, "interface.json");
        resourceDirPath = Path.Combine(tempExtractDir, "resource");
        validationMessage = string.Empty;

        if (!isIncrementalPackage
            && TryFindFullResourcePackageRoot(tempExtractDir, out var fullPackageRoot,
                out var fullPackageInterfacePath, out var fullPackageResourcePath))
        {
            originPath = fullPackageRoot;
            interfacePath = fullPackageInterfacePath;
            resourceDirPath = fullPackageResourcePath;
        }
        else foreach (var candidateRoot in candidateRoots)
        {
            var candidateInterfacePath = Path.Combine(candidateRoot, "interface.json");
            var candidateResourcePath = Path.Combine(candidateRoot, "resource");
            var hasInterface = File.Exists(candidateInterfacePath);
            var hasResourceDir = Directory.Exists(candidateResourcePath);
            var hasChanges = File.Exists(Path.Combine(candidateRoot, "changes.json"));
            var hasPatchFiles = Directory.Exists(candidateRoot)
                && Directory.EnumerateFiles(candidateRoot, "*", SearchOption.AllDirectories)
                    .Any(file => !Path.GetFileName(file).Equals("changes.json", StringComparison.OrdinalIgnoreCase));

            if (hasInterface || hasResourceDir || hasChanges || hasPatchFiles)
            {
                originPath = candidateRoot;
                interfacePath = candidateInterfacePath;
                resourceDirPath = candidateResourcePath;
                break;
            }
        }

        if (!Directory.Exists(originPath))
        {
            validationMessage = $"资源包目录不存在: {originPath}";
            return false;
        }

        if (!isIncrementalPackage && !File.Exists(interfacePath))
        {
            validationMessage = "资源包缺少 interface.json";
            return false;
        }

        if (!isIncrementalPackage && !Directory.Exists(resourceDirPath))
        {
            validationMessage = "资源包缺少 resource 目录";
            return false;
        }

        if (File.Exists(interfacePath))
        {
            try
            {
                _ = JObject.Parse(File.ReadAllText(interfacePath), new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    LineInfoHandling = LineInfoHandling.Load
                });
            }
            catch (Exception ex)
            {
                validationMessage = $"资源包中的 interface.json 无法解析: {ex.Message}";
                return false;
            }
        }

        if (isIncrementalPackage)
        {
            bool hasResourceDir = Directory.Exists(resourceDirPath);
            bool hasInterface = File.Exists(interfacePath);
            bool hasPatchFiles = Directory.EnumerateFiles(originPath, "*", SearchOption.AllDirectories)
                .Any(file => !Path.GetFileName(file).Equals("changes.json", StringComparison.OrdinalIgnoreCase));

            if (!hasResourceDir && !hasInterface && !hasPatchFiles)
            {
                validationMessage = "增量资源包不包含可应用的更新内容";
                return false;
            }
        }

        return true;
    }

    private static bool TryCompareVersions(string leftVersion, string rightVersion, out int comparison)
    {
        comparison = 0;
        if (string.IsNullOrWhiteSpace(leftVersion) || string.IsNullOrWhiteSpace(rightVersion))
            return false;

        const string versionPattern = @"^[vV]?\d+(?:\.\d+)?(?:\.\d+)?(?:-[0-9a-zA-Z\-\.]+)?(?:\+[0-9a-zA-Z\-\.]+)?$";
        if (!Regex.IsMatch(leftVersion.Trim(), versionPattern)
            || !Regex.IsMatch(rightVersion.Trim(), versionPattern))
            return false;

        try
        {
            comparison = ParseAndNormalizeVersion(leftVersion).ComparePrecedenceTo(ParseAndNormalizeVersion(rightVersion));
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"比较拖拽资源包版本失败：package={leftVersion}, current={rightVersion}, reason={ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> GetResourcePackageCandidateRoots(string extractDirectory)
    {
        var candidateRoots = new List<string>
        {
            extractDirectory,
            Path.Combine(extractDirectory, "assets")
        };

        try
        {
            var directChildDirectories = Directory.Exists(extractDirectory)
                ? Directory.GetDirectories(extractDirectory, "*", SearchOption.TopDirectoryOnly)
                : [];

            if (directChildDirectories.Length == 1)
            {
                candidateRoots.Add(directChildDirectories[0]);
                candidateRoots.Add(Path.Combine(directChildDirectories[0], "assets"));
            }
        }
        catch
        {
            // Ignore probing failures and fall back to the direct package roots.
        }

        return candidateRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryFindFullResourcePackageRoot(
        string extractDirectory,
        out string packageRoot,
        out string interfacePath,
        out string resourceDirectory)
    {
        foreach (var candidateRoot in GetResourcePackageCandidateRoots(extractDirectory))
        {
            var candidateInterface = Path.Combine(candidateRoot, "interface.json");
            var candidateResource = Path.Combine(candidateRoot, "resource");
            if (!File.Exists(candidateInterface) || !Directory.Exists(candidateResource))
                continue;

            packageRoot = candidateRoot;
            interfacePath = candidateInterface;
            resourceDirectory = candidateResource;
            return true;
        }

        packageRoot = extractDirectory;
        interfacePath = Path.Combine(extractDirectory, "interface.json");
        resourceDirectory = Path.Combine(extractDirectory, "resource");
        return false;
    }

    private static bool ContainsCoreApplicationFiles(string packageRoot)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
            return false;

        foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(packageRoot, file);
            if (IsCoreApplicationFile(relativePath))
                return true;
        }

        return false;
    }

    private static bool IsDataRootRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);

        if (normalized.StartsWith("resource/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("backup/", StringComparison.OrdinalIgnoreCase))
            return true;

        return fileName.Equals("interface.json", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("interface.jsonc", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("changes.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCoreApplicationFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);

        if (normalized.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("libs/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (fileName.Equals("MFAAvalonia.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MFAAvalonia.deps.json", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MFAAvalonia.runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
            || IsCurrentProcessExecutableFileName(fileName)
            || fileName.Equals("MFAUpdater", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("MFAUpdater.exe", StringComparison.OrdinalIgnoreCase))
            return true;

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && fileName.Contains("MFAAvalonia", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsCurrentProcessExecutableFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        try
        {
            var currentProcessPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentProcessPath))
                currentProcessPath = Environment.ProcessPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(currentProcessPath))
                return false;

            return fileName.Equals(Path.GetFileName(currentProcessPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSafeUpdateTargetPath(string baseDirectory, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        try
        {
            var normalizedBaseDirectory = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
            return fullPath.StartsWith(normalizedBaseDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }

    private static void TryRenameExecutableToBackup(string executablePath, bool allowCurrentProcessExecutable = false)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        if (!allowCurrentProcessExecutable && IsCurrentProcessExecutable(executablePath))
        {
            LoggerHelper.Warning($"已阻止将当前正在运行的主程序文件改名为 backupMFA：文件={executablePath}");
            return;
        }

        try
        {
            var fullExecutablePath = Path.GetFullPath(executablePath);
            if (!File.Exists(fullExecutablePath))
            {
                LoggerHelper.Warning($"准备将旧主程序改名为 backup 时未找到文件：文件={fullExecutablePath}");
                return;
            }

            var backupPath = fullExecutablePath + ".backupMFA";
            if (File.Exists(backupPath))
            {
                try
                {
                    File.SetAttributes(backupPath, FileAttributes.Normal);
                    File.Delete(backupPath);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"删除已存在的旧主程序 backupMFA 失败：文件={backupPath}，原因={ex.Message}");
                    return;
                }
            }

            File.SetAttributes(fullExecutablePath, FileAttributes.Normal);
            File.Move(fullExecutablePath, backupPath);
            LoggerHelper.Info($"旧主程序已改名为 backupMFA：源文件={fullExecutablePath}，目标文件={backupPath}");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"将旧主程序改名为 backupMFA 失败：文件={executablePath}，原因={ex.Message}");
        }
    }

    private static string GetBackupMfaPath(string filePath)
    {
        int index = 0;
        string currentDate = DateTime.Now.ToString("yyyyMMddHHmm");
        string backupFilePath = $"{filePath}.{currentDate}.{index}.backupMFA";

        while (File.Exists(backupFilePath))
        {
            index++;
            backupFilePath = $"{filePath}.{currentDate}.{index}.backupMFA";
        }

        return backupFilePath;
    }

    private static async Task<(bool replaced, string? backupPath)> TryReplaceRunningExecutableAsync(
        string sourceFile,
        string targetFile,
        CancellationToken cancellationToken)
    {
        var maxRetries = 5;
        string? backupPath = null;
        void RestoreBackupAfterFailure()
        {
            if (backupPath == null || !File.Exists(backupPath) || File.Exists(targetFile))
                return;

            try
            {
                File.Move(backupPath, targetFile);
                backupPath = null;
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"恢复替换失败的主程序失败：目标={targetFile}，备份={backupPath}，原因={ex.Message}", ex);
            }
        }

        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (backupPath == null)
                    {
                        backupPath = GetBackupMfaPath(targetFile);
                        File.SetAttributes(targetFile, FileAttributes.Normal);
                        File.Move(targetFile, backupPath);
                        LoggerHelper.Info($"当前运行中的旧主程序已改名为 backupMFA：源文件={targetFile}，备份文件={backupPath}");
                    }

                    File.Copy(sourceFile, targetFile, overwrite: false);
                    File.SetAttributes(targetFile, FileAttributes.Normal);
                    RestoreUnixExecutablePermission(targetFile);
                    LoggerHelper.Info($"新的同名主程序已复制完成：源文件={sourceFile}，目标文件={targetFile}");
                }, cancellationToken);

                return (true, backupPath);
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                LoggerHelper.Warning($"替换当前运行中的同名主程序失败，准备重试：第 {i + 1}/{maxRetries} 次，源文件={sourceFile}，目标文件={targetFile}，原因={ex.Message}");
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch
                {
                    RestoreBackupAfterFailure();
                    throw;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                LoggerHelper.Warning($"替换当前运行中的同名主程序被拒绝，准备重试：第 {i + 1}/{maxRetries} 次，源文件={sourceFile}，目标文件={targetFile}，原因={ex.Message}");
                try
                {
                    await Task.Delay(1000, cancellationToken);
                }
                catch
                {
                    RestoreBackupAfterFailure();
                    throw;
                }
            }
            catch
            {
                RestoreBackupAfterFailure();
                throw;
            }
        }

        RestoreBackupAfterFailure();

        return (false, backupPath);
    }

    private static bool HasExecutableFileNameChanged(string currentExecutablePath, string nextExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(currentExecutablePath) || string.IsNullOrWhiteSpace(nextExecutablePath))
            return false;

        var currentFileName = Path.GetFileName(currentExecutablePath);
        var nextFileName = Path.GetFileName(nextExecutablePath);
        return !string.Equals(currentFileName, nextFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentProcessExecutable(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var currentProcessPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentProcessPath))
                currentProcessPath = Environment.ProcessPath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(currentProcessPath))
                return false;

            return string.Equals(
                Path.GetFullPath(filePath),
                Path.GetFullPath(currentProcessPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [Obsolete("旧的外部更新器探测逻辑，已废弃；保留仅为兼容仍引用 ApplySecureUpdate 的旧流程。")]
    private static string FindUpdaterExecutablePath(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
            return string.Empty;

        var preferredNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "M9A.exe", "MFAUpdater.exe" }
            : new[] { "M9A", "MFAUpdater" };

        foreach (var preferredName in preferredNames)
        {
            var preferredPath = Path.Combine(baseDirectory, preferredName);
            if (File.Exists(preferredPath))
            {
                LoggerHelper.Info($"已按优先名称定位更新器：文件={preferredPath}");
                return preferredPath;
            }
        }

        try
        {
            var currentProcessPath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            var executableFiles = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Directory.EnumerateFiles(baseDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                : Directory.EnumerateFiles(baseDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => !Path.HasExtension(file) && IsExecutable(file));

            foreach (var executableFile in executableFiles)
            {
                if (!string.IsNullOrWhiteSpace(currentProcessPath)
                    && string.Equals(
                        Path.GetFullPath(executableFile),
                        Path.GetFullPath(currentProcessPath),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(executableFile);
                    var productName = versionInfo.ProductName ?? string.Empty;
                    var internalName = versionInfo.InternalName ?? string.Empty;
                    var originalFilename = versionInfo.OriginalFilename ?? string.Empty;
                    var fileDescription = versionInfo.FileDescription ?? string.Empty;

                    if (productName.Contains("MFAUpdater", StringComparison.OrdinalIgnoreCase)
                        || internalName.Contains("MFAUpdater", StringComparison.OrdinalIgnoreCase)
                        || originalFilename.Contains("MFAUpdater", StringComparison.OrdinalIgnoreCase)
                        || fileDescription.Contains("MFAUpdater", StringComparison.OrdinalIgnoreCase))
                    {
                        LoggerHelper.Info($"已按文件版本信息定位更新器：文件={executableFile}，OriginalFilename={originalFilename}");
                        return executableFile;
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"读取候选更新器版本信息失败：文件={executableFile}，原因={ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"扫描更新器失败：目录={baseDirectory}，原因={ex.Message}");
        }

        return string.Empty;
    }

    private static bool TryGetSafeUpdateTargetPath(string relativePath, out string fullPath)
    {
        var baseDirectory = IsDataRootRelativePath(relativePath) ? AppPaths.DataRoot : AppPaths.InstallRoot;
        return TryGetSafeUpdateTargetPath(baseDirectory, relativePath, out fullPath);
    }

    private static string[] GetRepoFromUrl(string githubUrl)
    {
        var pattern = @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)$";
        var match = Regex.Match(githubUrl, pattern);

        if (match.Success)
        {
            string owner = match.Groups["owner"].Value;
            string repo = match.Groups["repo"].Value;

            return
            [
                owner,
                repo
            ];
        }

        throw new FormatException("输入的 GitHub URL 格式不正确: " + githubUrl);
    }

    private static string CDK()
    {
        return Instances.VersionUpdateSettingsUserControlModel.CdkPassword;
    }

    private static void SetText(TextBlock? block, string text)
    {
        if (block == null)
            return;
        DispatcherHelper.PostOnMainThread(() => block.Text = text);
    }

    private static void SetStatusText(TextBlock? leftBlock, TextBlock? rightBlock, string text)
    {
        if (leftBlock == null && rightBlock == null)
            return;

        DispatcherHelper.PostOnMainThread(() =>
        {
            if (leftBlock != null)
                leftBlock.Text = text;
            if (rightBlock != null)
                rightBlock.Text = string.Empty;
        });
    }

    private static void SetDownloadInfo(
        TextBlock? downloadSizeTextBlock,
        TextBlock? downloadSpeedTextBlock,
        long downloadedBytes,
        long totalBytes,
        long bytesPerSecond)
    {
        if (downloadSizeTextBlock == null && downloadSpeedTextBlock == null)
            return;

        var sizeText = $"{MaaProcessor.FormatFileSize(downloadedBytes)}/{MaaProcessor.FormatFileSize(totalBytes)}";
        var speedText = MaaProcessor.FormatDownloadSpeed(bytesPerSecond);

        DispatcherHelper.PostOnMainThread(() =>
        {
            if (downloadSizeTextBlock != null)
                downloadSizeTextBlock.Text = sizeText;
            if (downloadSpeedTextBlock != null)
                downloadSpeedTextBlock.Text = speedText;
        });
    }

    private static void SetProgress(ProgressBar? bar, double percentage)
    {
        if (bar == null)
            return;
        DispatcherHelper.PostOnMainThread(() => bar.Value = percentage);
    }

    private static void Dismiss(ISukiToast? toast)
    {
        if (toast == null)
            return;

        try
        {
            if (toast is SukiToast sukiToast)
                DispatcherHelper.PostOnMainThread(() => sukiToast.Dismiss());
            else
                DispatcherHelper.PostOnMainThread(() => Instances.ToastManager.Dismiss(toast));
        }
        catch (Exception e)
        {
            LoggerHelper.Warning($"关闭更新提示 Toast 失败：原因={e.Message}");
        }
    }

    private static void CopyFolder(string sourceFolder, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder))
        {
            Directory.CreateDirectory(destinationFolder);
        }
        var files = Directory.GetFiles(sourceFolder);
        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            string destinationFile = Path.Combine(destinationFolder, fileName);
            File.Copy(file, destinationFile, true);
        }
        var subDirectories = Directory.GetDirectories(sourceFolder);
        foreach (string subDirectory in subDirectories)
        {
            string subDirectoryName = Path.GetFileName(subDirectory);
            string destinationSubDirectory = Path.Combine(destinationFolder, subDirectoryName);
            CopyFolder(subDirectory, destinationSubDirectory);
        }
    }

    private static void SaveRelease(JToken? releaseData, string from)
    {
        try
        {
            var bodyContent = releaseData?[from]?.ToString();
            if (!string.IsNullOrWhiteSpace(bodyContent) && bodyContent != "placeholder")
            {
                var resourceDirectory = AppPaths.ResourceDirectory;
                Directory.CreateDirectory(resourceDirectory);
                var filePath = Path.Combine(resourceDirectory, ChangelogViewModel.ReleaseFileName);
                File.WriteAllText(filePath, bodyContent);
                LoggerHelper.Info($"已保存发布说明文件：文件={filePath}，长度={bodyContent.Length}");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"保存发布说明文件失败：文件={ChangelogViewModel.ReleaseFileName}，原因={ex.Message}", ex);
        }
    }

    private static void SaveChangelog(JToken? releaseData, string from)
    {
        try
        {
            var bodyContent = releaseData?[from]?.ToString();
            if (!string.IsNullOrWhiteSpace(bodyContent) && bodyContent != "placeholder")
            {
                var resourceDirectory = AppPaths.ResourceDirectory;
                Directory.CreateDirectory(resourceDirectory);
                var filePath = Path.Combine(resourceDirectory, ChangelogViewModel.ChangelogFileName);
                File.WriteAllText(filePath, bodyContent);
                LoggerHelper.Info($"已保存更新日志文件：文件={filePath}，长度={bodyContent.Length}");
                GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowChangelogAgain, bool.FalseString);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"保存更新日志文件失败：文件={ChangelogViewModel.ChangelogFileName}，原因={ex.Message}", ex);
        }
    }


    public static HttpClient CreateHttpClientWithProxy()
    {
        bool disableSSL = File.Exists(Path.Combine(AppContext.BaseDirectory, "NO_SSL"));

        var _proxyAddress = Instances.VersionUpdateSettingsUserControlModel.ProxyAddress;
        NetworkCredential? credentials = null;

        // 创建带有 SSL 处理的 HttpClientHandler
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (sender, cert, chain, errors) =>
            {
                // 如果禁用 SSL 验证，直接放行
                if (disableSSL)
                {
                    return true;
                }

                // 只在有错误时记录详细信息，避免日志冗余
                if (errors != SslPolicyErrors.None)
                {
                    LoggerHelper.Warning($"证书验证警告：证书主题={cert?.Subject ?? "null"}");
                    LoggerHelper.Warning($"证书错误类型：{errors}");

                    if (chain != null)
                    {
                        foreach (var status in chain.ChainStatus)
                        {
                            LoggerHelper.Warning($"证书链状态：状态={status.Status}，信息={status.StatusInformation}");
                        }
                    }
                }

                if (errors == SslPolicyErrors.RemoteCertificateChainErrors)
                {
                    bool onlyTimeError = (chain?.ChainStatus ?? []).All(s =>
                        s.Status == X509ChainStatusFlags.NotTimeValid || s.Status == X509ChainStatusFlags.NoError);

                    if (onlyTimeError)
                    {
                        LoggerHelper.Warning("证书时间无效，但已放行");
                        return true;
                    }
                }

                return errors == SslPolicyErrors.None;
            },
            UseCookies = false,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        // 如果没有代理地址，直接返回带SSL 处理的 HttpClient
        if (string.IsNullOrWhiteSpace(_proxyAddress))
        {
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version11
            };
        }

        try
        {
            var userHostParts = _proxyAddress.Split('@');
            string endpointPart;
            if (userHostParts.Length == 2)
            {
                var credentialsPart = userHostParts[0];
                endpointPart = userHostParts[1];
                var creds = credentialsPart.Split(':');
                if (creds.Length != 2)
                    throw new FormatException("认证信息格式错误，应为 '<username>:<password>'");
                credentials = new NetworkCredential(creds[0], creds[1]);
            }
            else if (userHostParts.Length == 1)
            {
                endpointPart = userHostParts[0];
            }
            else
            {
                throw new FormatException("代理地址格式错误，应为 '[<username>:<password>@]<host>:<port>'");
            }
            var hostParts = endpointPart.Split(':');
            if (hostParts.Length != 2)
                throw new FormatException("主机部分格式错误，应为 '<host>:<port>'");

            switch (Instances.VersionUpdateSettingsUserControlModel.ProxyType)
            {
                case VersionUpdateSettingsUserControlModel.UpdateProxyType.Socks5:
                    handler.Proxy = new WebProxy($"socks5://{_proxyAddress}", false, null, credentials);
                    handler.UseProxy = true;
                    return new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(60),
                        DefaultRequestVersion = HttpVersion.Version11
                    };
                default:
                    handler.Proxy = new WebProxy($"http://{_proxyAddress}", false, null, credentials);
                    handler.UseProxy = true;
                    return new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(60),
                        DefaultRequestVersion = HttpVersion.Version11
                    };
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"初始化网络代理失败：原因={ex.Message}", ex);
            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version11
            };
        }

    }


    /// <summary>
    /// 从URL中提取文件扩展名
    /// </summary>
    private static string GetFileExtensionFromUrl(string url)
    {
        try
        {
            // 解析URL路径部分
            Uri uri = new Uri(url);
            string path = Uri.UnescapeDataString(uri.LocalPath);

            // 提取扩展名（自动处理带查询参数的情况）
            return Path.GetExtension(path);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"解析 URL 扩展名失败：原因={ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// 从Content-Disposition头解析文件名（可选增强）
    /// </summary>
    private static string? ParseFileNameFromContentDisposition(string contentDisposition)
    {
        // 示例格式: "attachment; filename=resource.tar.gz"
        const string filenamePrefix = "filename=";
        int index = contentDisposition.IndexOf(filenamePrefix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            string filename = contentDisposition.Substring(index + filenamePrefix.Length);
            // 移除引号
            if (filename.StartsWith("\"") && filename.EndsWith("\""))
            {
                filename = filename[1..^1];
            }
            return filename;
        }
        return null;
    }

    /// <summary>
    /// 跨平台检测目录中是否包含 MFAAvalonia 可执行文件。
    /// 根据当前系统查找 .exe（Windows）或无后缀可执行文件（Linux/macOS），
    /// 通过 --identify 参数询问可执行文件是否为 MFAAvalonia。
    /// </summary>
    private static string FindMFAExecutableByAssemblyName(string directory, string targetIdentity = "MFAAvalonia")
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return string.Empty;

        try
        {
            var exeFiles = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
                : Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(f => !Path.HasExtension(f) && IsExecutable(f));

            var excludeNames = new[] { "MFAUpdater", "createdump", "MaaPiCli", "CONTACT", "LICENSE" };

            foreach (var exeFile in exeFiles)
            {
                var baseName = Path.GetFileNameWithoutExtension(exeFile);
                if (excludeNames.Any(e => baseName.Equals(e, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = exeFile,
                        Arguments = "--identify",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process == null) continue;
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    if (!process.WaitForExit(3000))
                    {
                        try { process.Kill(); } catch { }
                        LoggerHelper.Warning($"识别可执行文件超时：模式=--identify，文件={exeFile}，超时毫秒=3000");
                        continue;
                    }
                    outputTask.Wait(1000);
                    var output = outputTask.IsCompleted ? outputTask.Result.Trim() : string.Empty;

                    if (output.Equals(targetIdentity, StringComparison.OrdinalIgnoreCase))
                    {
                        LoggerHelper.Info($"已通过 --identify 识别到 MFAAvalonia 可执行文件：{exeFile}");
                        return exeFile;
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"识别可执行文件失败：文件={exeFile}，原因={ex.Message}");
                }
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"扫描 MFAAvalonia 可执行文件失败：根目录={directory}，原因={ex.Message}", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 从目录中查找 MFAAvalonia 的可执行文件
    ///通过查找 MFAAvalonia.dll 或 MFAAvalonia.deps.json 来定位，因为这些文件名是固定的
    /// </summary>
    /// <param name="directory">要搜索的目录</param>
    /// <returns>找到的可执行文件路径，如果未找到则返回空字符串</returns>
    private static string FindMFAExecutableInDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return string.Empty;

        try
        {
            // 方法1: 查找 MFAAvalonia.dll 所在目录，然后找同目录下的可执行文件
            var dllFiles = Directory.GetFiles(directory, "MFAAvalonia.dll", SearchOption.AllDirectories);
            if (dllFiles.Length > 0)
            {
                var dllDir = Path.GetDirectoryName(dllFiles[0]);
                if (!string.IsNullOrEmpty(dllDir))
                {
                    var exeFile = FindExecutableInSameDirectory(dllDir);
                    if (!string.IsNullOrEmpty(exeFile))
                    {
                        LoggerHelper.Info($"已通过 MFAAvalonia.dll 定位可执行文件：{exeFile}");
                        return exeFile;
                    }
                }
            }

            // 方法2: 查找 MFAAvalonia.deps.json 所在目录
            var depsFiles = Directory.GetFiles(directory, "MFAAvalonia.deps.json", SearchOption.AllDirectories);
            if (depsFiles.Length > 0)
            {
                var depsDir = Path.GetDirectoryName(depsFiles[0]);
                if (!string.IsNullOrEmpty(depsDir))
                {
                    var exeFile = FindExecutableInSameDirectory(depsDir);
                    if (!string.IsNullOrEmpty(exeFile))
                    {
                        LoggerHelper.Info($"已通过 MFAAvalonia.deps.json 定位可执行文件：{exeFile}");
                        return exeFile;
                    }
                }
            }

            // 方法3: 查找 MFAAvalonia.runtimeconfig.json 所在目录
            var runtimeConfigFiles = Directory.GetFiles(directory, "MFAAvalonia.runtimeconfig.json", SearchOption.AllDirectories);
            if (runtimeConfigFiles.Length > 0)
            {
                var configDir = Path.GetDirectoryName(runtimeConfigFiles[0]);
                if (!string.IsNullOrEmpty(configDir))
                {
                    var exeFile = FindExecutableInSameDirectory(configDir);
                    if (!string.IsNullOrEmpty(exeFile))
                    {
                        LoggerHelper.Info($"已通过 MFAAvalonia.runtimeconfig.json 定位可执行文件：{exeFile}");
                        return exeFile;
                    }
                }
            }

            LoggerHelper.Warning($"未在目录中找到 MFAAvalonia 可执行文件：目录={directory}");
            return string.Empty;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"查找 MFAAvalonia 可执行文件失败：目录={directory}，原因={ex.Message}", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 在指定目录中查找可执行文件（排除 MFAUpdater）
    /// </summary>
    private static string FindExecutableInSameDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return string.Empty;

        // 获取目录中的所有可执行文件
        IEnumerable<string> exeFiles;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            exeFiles = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        else
        {
            exeFiles = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.HasExtension(f) && IsExecutable(f));
        }

        // 排除 MFAUpdater 和其他已知的非主程序可执行文件
        var excludeNames = new[]
        {
            "MFAUpdater",
            "createdump"
        };

        foreach (var exeFile in exeFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(exeFile);
            if (excludeNames.Any(e => fileName.Equals(e, StringComparison.OrdinalIgnoreCase))) continue;

            // 检查是否有对应的 .dll 文件（.NET 应用的特征）
            var correspondingDll = Path.Combine(directory, fileName + ".dll");
            if (File.Exists(correspondingDll))
            {
                return exeFile;
            }
        }

        // 如果没有找到有对应 DLL 的可执行文件，返回第一个非排除的可执行文件
        foreach (var exeFile in exeFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(exeFile);
            if (!excludeNames.Any(e => fileName.Equals(e, StringComparison.OrdinalIgnoreCase)))
            {
                return exeFile;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 检查文件是否为可执行文件（用于非 Windows 系统）
    /// </summary>
    private static bool IsExecutable(string filePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            // 在 Unix 系统上，检查文件是否有执行权限或是 ELF/Mach-O 格式
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length < 4)
                return false;

            // 读取文件头来判断是否为可执行文件
            using var stream = File.OpenRead(filePath);
            var header = new byte[4];
            if (stream.Read(header, 0, 4) < 4)
                return false;

            // ELF 格式 (Linux)
            if (header[0] == 0x7F && header[1] == 'E' && header[2] == 'L' && header[3] == 'F')
                return true;

            // Mach-O 格式 (macOS)
            if ((header[0] == 0xCF && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE)
                || // 64-bit
                (header[0] == 0xCE && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE)
                || // 32-bit
                (header[0] == 0xFE && header[1] == 0xED && header[2] == 0xFA && header[3] == 0xCF)
                || // 64-bit reverse
                (header[0] == 0xFE && header[1] == 0xED && header[2] == 0xFA && header[3] == 0xCE)) // 32-bit reverse
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }
}
