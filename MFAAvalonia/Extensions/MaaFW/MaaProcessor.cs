using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Notification;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Windows;
using Microsoft.WindowsAPICodePack.Taskbar;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SukiUI.Dialogs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Brushes = Avalonia.Media.Brushes;
using MaaController = MaaFramework.Binding.MaaController;
using MaaGlobal = MaaFramework.Binding.MaaGlobal;
using MaaResource = MaaFramework.Binding.MaaResource;
using MaaTasker = MaaFramework.Binding.MaaTasker;
using MaaToolkit = MaaFramework.Binding.MaaToolkit;

namespace MFAAvalonia.Extensions.MaaFW;
#pragma warning  disable CS4014 // 由于此调用不会等待，因此在此调用完成之前将会继续执行当前方法.
#pragma warning  disable CS1998 // 此异步方法缺少 "await" 运算符，将以同步方式运行。
#pragma warning disable CA1416 //  可在 'linux', 'macOS/OSX', 'windows' 上访问此调用站点。
public class MaaProcessor
{
    #region 属性

    private static readonly Random Random = new();
    private int _taskQueueTotal;
    private readonly BlockingCollection<Func<Task>> _commandQueue = new();
    private readonly object _commandThreadLock = new();
    private readonly CancellationTokenSource _commandThreadCts = new();
    private Thread? _commandThread;
    public static string Resource => Path.Combine(AppContext.BaseDirectory, "resource");
    public static string ResourceBase => Path.Combine(Resource, "base");
    public static ObservableCollection<MaaProcessor> Processors { get; } = new();
    public static MaaToolkit Toolkit { get; } = new(true);
    public static MaaGlobal Global { get; } = new();
    public string InstanceId { get; }
    public InstanceConfiguration InstanceConfiguration { get; }
    public MaaFWConfiguration Config { get; } = new();
    public TaskQueueViewModel? ViewModel => MaaProcessorManager.Instance.GetViewModel(InstanceId);

    // public Dictionary<string, MaaNode> BaseNodes = new();
    //
    // public Dictionary<string, MaaNode> NodeDictionary = new();
    public ObservableQueue<MFATask> TaskQueue { get; } = new();
    public bool IsV3 = false;

    private const int MaxLogCount = 150;
    private const int LogCleanupBatchSize = 30;
    public DisposableObservableCollection<LogItemViewModel> LogItemViewModels { get; } = new();

    public const string INFO = "info:";
    public static readonly string[] ERROR = ["err:", "error:"];
    public static readonly string[] WARNING = ["warn:", "warning:"];
    public const string TRACE = "trace:";
    public const string DEBUG = "debug:";
    public const string CRITICAL = "critical:";
    public const string SUCCESS = "success:";

    public void ClearLogs()
    {
        LogItemViewModels.Clear();
    }

    private void TrimExcessLogs()
    {
        if (LogItemViewModels.Count <= MaxLogCount) return;

        var removeCount = Math.Min(LogCleanupBatchSize, LogItemViewModels.Count - MaxLogCount + LogCleanupBatchSize);
        LogItemViewModels.RemoveRange(0, removeCount);

        try
        {
            FontService.Instance.ClearFontCache();
            LoggerHelper.Info("[内存优化] 已清理字体缓存");
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"清理字体缓存失败: {ex.Message}");
        }
    }

    public static string FormatFileSize(long size)
    {
        string unit;
        double value;
        if (size >= 1024L * 1024 * 1024 * 1024)
        {
            value = (double)size / (1024L * 1024 * 1024 * 1024);
            unit = "TB";
        }
        else if (size >= 1024 * 1024 * 1024)
        {
            value = (double)size / (1024 * 1024 * 1024);
            unit = "GB";
        }
        else if (size >= 1024 * 1024)
        {
            value = (double)size / (1024 * 1024);
            unit = "MB";
        }
        else if (size >= 1024)
        {
            value = (double)size / 1024;
            unit = "KB";
        }
        else
        {
            value = size;
            unit = "B";
        }

        return $"{value:F} {unit}";
    }

    public static string FormatDownloadSpeed(double speed)
    {
        string unit;
        double value = speed;
        if (value >= 1024L * 1024 * 1024 * 1024)
        {
            value /= 1024L * 1024 * 1024 * 1024;
            unit = "TB/s";
        }
        else if (value >= 1024L * 1024 * 1024)
        {
            value /= 1024L * 1024 * 1024;
            unit = "GB/s";
        }
        else if (value >= 1024 * 1024)
        {
            value /= 1024 * 1024;
            unit = "MB/s";
        }
        else if (value >= 1024)
        {
            value /= 1024;
            unit = "KB/s";
        }
        else
        {
            unit = "B/s";
        }

        return $"{value:F} {unit}";
    }

    public void OutputDownloadProgress(long value = 0, long maximum = 1, int len = 0, double ts = 1)
    {
        string sizeValueStr = FormatFileSize(value);
        string maxSizeValueStr = FormatFileSize(maximum);
        string speedValueStr = FormatDownloadSpeed(len / ts);

        string progressInfo = $"[{sizeValueStr}/{maxSizeValueStr}({100 * value / maximum}%) {speedValueStr}]";
        OutputDownloadProgress(progressInfo);
    }

    public void ClearDownloadProgress()
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            if (LogItemViewModels.Count > 0 && LogItemViewModels[0].IsDownloading)
            {
                LogItemViewModels.RemoveAt(0);
            }
        });
    }

    public void OutputDownloadProgress(string output, bool downloading = true)
    {
        // DispatcherHelper.RunOnMainThread(() =>
        // {
        //     var log = new LogItemViewModel(downloading ? LangKeys.NewVersionFoundDescDownloading.ToLocalization() + "\n" + output : output, Instances.RootView.FindResource("SukiAccentColor") as IBrush,
        //         dateFormat: "HH':'mm':'ss")
        //     {
        //         IsDownloading = true,
        //     };
        //     if (LogItemViewModels.Count > 0 && LogItemViewModels[0].IsDownloading)
        //     {
        //         if (!string.IsNullOrEmpty(output))
        //         {
        //             LogItemViewModels[0] = log;
        //         }
        //         else
        //         {
        //             LogItemViewModels.RemoveAt(0);
        //         }
        //     }
        //     else if (!string.IsNullOrEmpty(output))
        //     {
        //         LogItemViewModels.Insert(0, log);
        //     }
        // });
    }

    public static bool CheckShouldLog(string content)
    {
        const StringComparison comparison = StringComparison.Ordinal;

        if (content.StartsWith(TRACE, comparison))
        {
            return true;
        }

        if (content.StartsWith(DEBUG, comparison))
        {
            return true;
        }

        if (content.StartsWith(SUCCESS, comparison))
        {
            return true;
        }

        if (content.StartsWith(INFO, comparison))
        {
            return true;
        }

        var warnPrefix = WARNING.FirstOrDefault(prefix =>
            !string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, comparison)
        );
        if (warnPrefix != null)
        {
            return true;
        }

        var errorPrefix = ERROR.FirstOrDefault(prefix =>
            !string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, comparison)
        );

        if (errorPrefix != null)
        {
            return true;
        }

        if (content.StartsWith(CRITICAL, comparison))
        {
            return true;
        }
        return false;
    }

    public void AddLog(string content,
        IBrush? brush,
        string weight = "Regular",
        bool changeColor = true,
        bool showTime = true)
    {
        brush ??= Brushes.Black;

        var backGroundBrush = Brushes.Transparent;
        const StringComparison comparison = StringComparison.Ordinal;

        if (content.StartsWith(TRACE, comparison))
        {
            brush = Brushes.MediumAquamarine;
            content = content.Substring(TRACE.Length).TrimStart();
            changeColor = false;
        }

        if (content.StartsWith(DEBUG, comparison))
        {
            brush = Brushes.DeepSkyBlue;
            content = content.Substring(DEBUG.Length).TrimStart();
            changeColor = false;
        }

        if (content.StartsWith(SUCCESS, comparison))
        {
            brush = Brushes.LimeGreen;
            content = content.Substring(SUCCESS.Length).TrimStart();
            changeColor = false;
        }

        if (content.StartsWith(INFO, comparison))
        {
            content = content.Substring(INFO.Length).TrimStart();
        }

        var warnPrefix = WARNING.FirstOrDefault(prefix =>
            !string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, comparison)
        );
        if (warnPrefix != null)
        {
            brush = Brushes.Orange;
            content = content.Substring(warnPrefix.Length).TrimStart();
            changeColor = false;
        }

        var errorPrefix = ERROR.FirstOrDefault(prefix =>
            !string.IsNullOrEmpty(prefix) && content.StartsWith(prefix, comparison)
        );

        if (errorPrefix != null)
        {
            brush = Brushes.OrangeRed;
            content = content.Substring(errorPrefix.Length).TrimStart();
            changeColor = false;
        }

        if (content.StartsWith(CRITICAL, comparison))
        {
            var color = DispatcherHelper.RunOnMainThread(() => MFAExtensions.FindSukiUiResource<Color>(
                "SukiLightBorderBrush"
            ));
            if (color != null)
                brush = DispatcherHelper.RunOnMainThread(() => new SolidColorBrush(color.Value));
            else
                brush = Brushes.White;
            backGroundBrush = Brushes.OrangeRed;
            content = content.Substring(CRITICAL.Length).TrimStart();
        }

        DispatcherHelper.PostOnMainThread(() =>
        {
            LogItemViewModels.Add(new LogItemViewModel(content, brush, weight, "HH':'mm':'ss",
                showTime: showTime, changeColor: changeColor)
            {
                BackgroundColor = backGroundBrush
            });
            LoggerHelper.Info($"[Record] {content}");

            TrimExcessLogs();
        });
    }

    public void AddLog(string content,
        string color = "",
        string weight = "Regular",
        bool changeColor = true,
        bool showTime = true)
    {
        var brush = BrushHelper.ConvertToBrush(color, Brushes.Black);
        AddLog(content, brush, weight, changeColor, showTime);
    }

    public void AddLogByKey(string key, IBrush? brush = null, bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        brush ??= Brushes.Black;
        Task.Run(() =>
        {
            DispatcherHelper.PostOnMainThread(() =>
            {
                var log = new LogItemViewModel(key, brush, "Regular", true, "HH':'mm':'ss", changeColor: changeColor, showTime: true, transformKey: transformKey, formatArgsKeys);
                LogItemViewModels.Add(log);
                LoggerHelper.Info(log.Content);
                TrimExcessLogs();
            });
        });
    }

    public void AddLogByKey(string key, string color = "", bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        var brush = BrushHelper.ConvertToBrush(color, Brushes.Black);
        AddLogByKey(key, brush, changeColor, transformKey, formatArgsKeys);
    }

    public void AddMarkdown(string key, IBrush? brush = null, bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        brush ??= Brushes.Black;
        Task.Run(() =>
        {
            DispatcherHelper.PostOnMainThread(() =>
            {
                var log = new LogItemViewModel(key, brush, "Regular", true, "HH':'mm':'ss", changeColor: changeColor, showTime: true, transformKey: transformKey, formatArgsKeys)
                {
                    UseMarkdown = true
                };
                LogItemViewModels.Add(log);
                LoggerHelper.Info(log.Content);
                TrimExcessLogs();
            });
        });
    }

    /// <summary>
    /// JSON 加载设置，忽略注释（支持 JSONC 格式）
    /// </summary>
    private static readonly JsonLoadSettings JsoncLoadSettings = new()
    {
        CommentHandling = CommentHandling.Ignore
    };

    /// <summary>
    /// 获取 interface 文件路径，优先返回 .jsonc，其次 .json
    /// </summary>
    public static string? GetInterfaceFilePath()
    {
        var jsoncPath = Path.Combine(AppContext.BaseDirectory, "interface.jsonc");
        if (File.Exists(jsoncPath))
            return jsoncPath;

        var jsonPath = Path.Combine(AppContext.BaseDirectory, "interface.json");
        if (File.Exists(jsonPath))
            return jsonPath;

        return null;
    }

    private static bool? _cachedIsV3;

    public MaaProcessor(string instanceId)
    {
        InstanceId = instanceId;
        InstanceConfiguration = new InstanceConfiguration(instanceId);
        DispatcherHelper.RunOnMainThread(() => Processors.Add(this));

        TaskQueue.CountChanged += (_, args) =>
        {
            DispatcherHelper.PostOnMainThread(() =>
            {
                Instances.RootViewModel.IsRunning = Processors.Any(p => p.TaskQueue.Count > 0);
            });

            if (_taskQueueTotal <= 0)
            {
                ClearTaskbarProgress();
                return;
            }

            var completed = _taskQueueTotal - args.NewValue;
            SetTaskbarProgress(completed, _taskQueueTotal);

            if (args.NewValue <= 0)
            {
                ClearTaskbarProgress();
                _taskQueueTotal = 0;
            }
        };

        // 使用缓存避免每个实例都重复读取 interface.json
        if (_cachedIsV3.HasValue)
        {
            IsV3 = _cachedIsV3.Value;
        }
        else
        {
            CheckInterface(out _, out _, out _, out _, out _);
            try
            {
                var filePath = GetInterfaceFilePath();
                if (filePath != null)
                {
                    var content = File.ReadAllText(filePath);
                    var @interface = JObject.Parse(content, JsoncLoadSettings);
                    var interfaceVersion = @interface["interface_version"]?.ToString();
                    if (int.TryParse(interfaceVersion, out var result) && result >= 3)
                    {
                        IsV3 = true;
                    }
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
            }
            _cachedIsV3 = IsV3;
        }
    }
    private bool _isClosed = false;
    public bool IsClosed => _isClosed;

    /// <summary>
    /// modal 弹窗等待标志：FocusHandler 触发 modal 时设为 true，用户确认后设为 false
    /// </summary>
    private volatile bool _isWaitingForModal = false;

    /// <summary>
    /// 设置 modal 等待状态（由 FocusHandler 调用）
    /// </summary>
    public void SetWaitingForModal(bool waiting) => _isWaitingForModal = waiting;

    public void Dispose()
    {
        _isClosed = true;
        DispatcherHelper.RunOnMainThread(() => Processors.Remove(this));
        StopCommandThread();
    }

    public static MaaInterface? Interface
    {
        get => field;
        private set
        {
            // 释放旧 Preset 的事件订阅，防止 LanguageChanged 泄漏
            field?.Preset?.ForEach(p => p.Dispose());
            field = value;

            foreach (var customResource in value?.Resource ?? Enumerable.Empty<MaaInterface.MaaInterfaceResource>())
            {
                var nameKey = customResource.Name?.Trim() ?? string.Empty;
                var paths = MaaInterface.ReplacePlaceholder(customResource.Path ?? new(), AppContext.BaseDirectory);
                customResource.ResolvedPath = paths;
                value!.Resources[nameKey] = customResource;
            }

            // 为 Option 字典中的每个项设置 Name（因为 Name 是 JsonIgnore 的）
            if (value?.Option != null)
            {
                foreach (var kvp in value.Option)
                {
                    kvp.Value.Name = kvp.Key;
                }
            }

            // 为 Advanced 字典中的每个项设置 Name（因为 Name 是 JsonIgnore 的）
            if (value?.Advanced != null)
            {
                foreach (var kvp in value.Advanced)
                {
                    kvp.Value.Name = kvp.Key;
                }
            }

            if (value != null)
            {
                Instances.SettingsViewModel.ShowResourceIssues = !string.IsNullOrWhiteSpace(value.Url) || !string.IsNullOrWhiteSpace(value.Github);
                Instances.SettingsViewModel.ResourceGithub = (!string.IsNullOrWhiteSpace(value.Github) ? value.Github : value.Url) ?? "";
                Instances.SettingsViewModel.ResourceIssues = $"{(!string.IsNullOrWhiteSpace(value.Github) ? value.Github : value.Url)}/issues";

                // 加载多语言配置
                if (value.Languages is { Count: > 0 })
                {
                    LanguageHelper.LoadLanguagesFromInterface(value.Languages, AppContext.BaseDirectory);
                }

                if (MaaProcessorManager.IsInstanceCreated)
                {
                    DispatcherHelper.PostOnMainThread(() =>
                    {
                        foreach (var processor in Processors)
                        {
                            processor.ViewModel?.InitializeControllerOptions();
                            processor.ViewModel?.RefreshPresets();
                        }
                        Instances.InstanceTabBarViewModel.RefreshInstancePresets();
                    });
                }

                // 异步加载 Contact 和 Description 内容
                _ = LoadContactAndDescriptionAsync(value);
            }

        }
    }

    /// <summary>
    /// 异步加载 Contact 和 Description 内容
    /// </summary>
    async private static Task LoadContactAndDescriptionAsync(MaaInterface maaInterface)
    {
        var projectDir = AppContext.BaseDirectory;

        // 加载 Description
        if (!string.IsNullOrWhiteSpace(maaInterface.Description))
        {
            var description = await maaInterface.Description.ResolveContentAsync(projectDir);
            Instances.SettingsViewModel.ResourceDescription = description;
            Instances.SettingsViewModel.HasResourceDescription = !string.IsNullOrWhiteSpace(description);
        }
        else
        {
            Instances.SettingsViewModel.ResourceDescription = string.Empty;
            Instances.SettingsViewModel.HasResourceDescription = false;
        }

        // 加载 Contact
        if (!string.IsNullOrWhiteSpace(maaInterface.Contact))
        {
            var contact = await maaInterface.Contact.ResolveContentAsync(projectDir);
            Instances.SettingsViewModel.ResourceContact = contact;
            Instances.SettingsViewModel.HasResourceContact = !string.IsNullOrWhiteSpace(contact);
        }
        else
        {
            Instances.SettingsViewModel.ResourceContact = string.Empty;
            Instances.SettingsViewModel.HasResourceContact = false;
        }

        // 加载 License
        if (!string.IsNullOrWhiteSpace(maaInterface.License))
        {
            var license = await maaInterface.License.ResolveContentAsync(projectDir);
            Instances.SettingsViewModel.ResourceLicense = license;
            Instances.SettingsViewModel.HasResourceLicense = !string.IsNullOrWhiteSpace(license);
        }
        else
        {
            Instances.SettingsViewModel.ResourceLicense = string.Empty;
            Instances.SettingsViewModel.HasResourceLicense = false;
        }
    }

    public MaaTasker? MaaTasker { get; set; }
    private MaaTasker? _screenshotTasker;
    private Task<MaaTasker?>? _screenshotTaskerInitTask;
    private readonly Lock _screenshotTaskerInitLock = new();
    public MaaTasker? ScreenshotTasker => _screenshotTasker;
    public void SetTasker(MaaTasker? maaTasker = null)
    {
        ResetActionFailedCount();
        if (maaTasker == null && MaaTasker != null)
        {
            var oldTasker = MaaTasker;
            MaaTasker = null; // 先设置为 null，防止重复释放

            try
            {
                // 使用超时机制避免无限等待，最多等待 5 秒
                var stopTask = Task.Run(() =>
                {
                    try
                    {
                        oldTasker.Stop().Wait();
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"MaaTasker Stop inner failed: {ex.Message}");
                    }
                });

                if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    LoggerHelper.Warning("MaaTasker Stop timed out after 5 seconds");
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Warning($"MaaTasker Stop failed: {e.Message}");
            }

            _agentStarted = false;
            AgentHelper.KillAllAgents(_agentContexts, oldTasker);
            ViewModel?.SetConnected(false);
            DisposeScreenshotTasker();
        }
        else if (maaTasker == null)
        {
            // 即使主 Tasker 已经为空，也要确保截图 Tasker 被清理。
            DisposeScreenshotTasker();
        }
        else if (maaTasker != null)
        {
            MaaTasker = maaTasker;
            DisposeScreenshotTasker();
            ResetScreencapFailureLogFlags();
        }
    }

    public MaaTasker? GetTasker(CancellationToken token = default)
    {
        var task = GetTaskerAsync(token);
        task.Wait(token);
        return task.Result;
    }

    public async Task<MaaTasker?> GetTaskerAsync(CancellationToken token = default)
    {
        if (MaaTasker == null)
        {
            var initializedTasker = (await InitializeMaaTasker(token)).Item1;
            if (initializedTasker != null)
            {
                MaaTasker = initializedTasker;
                // Ensure screenshot tasker is recreated from this processor's latest connection context.
                DisposeScreenshotTasker();
                ResetScreencapFailureLogFlags();
                await PrewarmScreenshotTaskerAsync(token);
            }
        }
        return MaaTasker;
    }

    public async Task<(MaaTasker?, bool, bool)> GetTaskerAndBoolAsync(CancellationToken token = default)
    {
        var tuple = MaaTasker != null ? (MaaTasker, false, false) : await InitializeMaaTasker(token);
        if (MaaTasker == null && tuple.Item1 != null)
        {
            MaaTasker = tuple.Item1;
            // Ensure screenshot tasker is recreated from this processor's latest connection context.
            DisposeScreenshotTasker();
            ResetScreencapFailureLogFlags();
            await PrewarmScreenshotTaskerAsync(token);
        }
        return (MaaTasker, tuple.Item2, tuple.Item3);
    }

    private async Task PrewarmScreenshotTaskerAsync(CancellationToken token)
    {
        if (!UseSeparateScreenshotTasker || _isClosed || MaaTasker == null || _screenshotTasker != null)
            return;

        Task<MaaTasker?> initTask;
        lock (_screenshotTaskerInitLock)
        {
            _screenshotTaskerInitTask ??= InitializeScreenshotTaskerAsync(token);
            initTask = _screenshotTaskerInitTask;
        }

        MaaTasker? tasker = null;
        try
        {
            tasker = await initTask;
        }
        catch (OperationCanceledException)
        {
            // Keep main tasker usable even if screenshot prewarm gets canceled.
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker prewarm failed: {ex.Message}");
        }

        lock (_screenshotTaskerInitLock)
        {
            if (_screenshotTasker == null)
            {
                _screenshotTasker = tasker;
            }
            _screenshotTaskerInitTask = null;
        }
    }

    private bool UseSeparateScreenshotTasker =>
        InstanceConfiguration.GetValue(ConfigurationKeys.UseSeparateScreenshotTasker, true);

    private MaaTasker? GetScreenshotTasker(CancellationToken token = default)
    {
        if (!UseSeparateScreenshotTasker)
        {
            DisposeScreenshotTasker();
            return MaaTasker;
        }

        if (_screenshotTasker == null && !_isClosed)
        {
            Task<MaaTasker?> initTask;
            lock (_screenshotTaskerInitLock)
            {
                _screenshotTaskerInitTask ??= InitializeScreenshotTaskerAsync(token);
                initTask = _screenshotTaskerInitTask;
            }

            initTask.Wait(token);
            var tasker = initTask.Result;

            lock (_screenshotTaskerInitLock)
            {
                if (_screenshotTasker == null)
                {
                    _screenshotTasker = tasker;
                }
                _screenshotTaskerInitTask = null;
            }
        }

        return _screenshotTasker;
    }

    private void DisposeScreenshotTasker()
    {
        if (_screenshotTasker == null)
            return;

        var screenshotTasker = _screenshotTasker;
        _screenshotTasker = null;
        lock (_screenshotTaskerInitLock)
        {
            _screenshotTaskerInitTask = null;
        }

        try
        {
            if (screenshotTasker.IsRunning && !screenshotTasker.IsStopping)
            {
                screenshotTasker.Stop().Wait();
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker Stop failed: {ex.Message}");
        }

        try
        {
            screenshotTasker.Dispose();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker Dispose failed: {ex.Message}");
        }
    }

    public ObservableCollection<DragItemViewModel> TasksSource { get; private set; } =
        [];
    public AutoInitDictionary AutoInitDictionary { get; } = new();
    private FocusHandler? _focusHandler;
    private TaskLoader? _taskLoader;

    private List<AgentContext> _agentContexts = [];
    private bool _agentStarted;
    private MFATask.MFATaskStatus Status = MFATask.MFATaskStatus.NOT_STARTED;
    private int _stopCompletionMessageHandled;
    private const int ActionFailedLimit = 20;
    private int _screencapFailedCount;
    private readonly Lock _screencapLogLock = new();
    private bool _screencapAbortLogPending;
    private bool _screencapDisconnectedLogPending;
    private bool _screencapFailureLogged;
    private int _isConnecting;
    public bool IsConnecting => _isConnecting != 0;

    private IMaaController? GetScreenshotController(bool test)
    {
        if (test && !_isClosed)
            TryConnectAsync(CancellationToken.None);

        return GetScreenshotTasker(CancellationToken.None)?.Controller;
    }

    private bool ShouldScreencapForLiveView()
    {
        return MaaTasker?.IsRunning != true && !_isClosed;
    }

    public Bitmap? GetBitmapImage(bool test = true)
    {
        var controller = GetScreenshotController(test);
        using var buffer = GetImage(controller);
        return buffer?.ToBitmap();
    }

    public Bitmap? GetLiveView(bool test = true)
    {
        var controller = GetScreenshotController(test);
        if (controller == null || !controller.IsConnected)
            return null;
        using var buffer = GetImage(controller, ShouldScreencapForLiveView());
        return buffer?.ToBitmap();
    }

    public Bitmap? GetLiveViewCached()
    {
        var controller = GetScreenshotController(false);
        if (controller == null || !controller.IsConnected)
            return null;

        using var buffer = GetImage(controller, false);
        return buffer?.ToBitmap();
    }

    public MaaJobStatus PostScreencap()
    {
        var controller = GetScreenshotController(false);

        if (controller == null || !controller.IsConnected)
            return MaaJobStatus.Invalid;

        try
        {
            return controller.Screencap().Wait();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"PostScreencap failed: {ex.Message}");
            return MaaJobStatus.Invalid;
        }
    }

    public MaaImageBuffer? GetLiveViewBuffer(bool test = true)
    {
        var controller = GetScreenshotController(test);
        return GetImage(controller, false);
    }

    /// <summary>
    /// 获取截图的MaaImageBuffer。调用者必须负责释放返回的 buffer。
    /// </summary>
    /// <param name="maaController">控制器实例</param>
    /// <param name="screencap">是否主动截图</param>
    /// <returns>包含截图的 MaaImageBuffer，如果失败则返回 null</returns>
    public MaaImageBuffer? GetImage(IMaaController? maaController, bool screencap = true)
    {
        if (maaController == null)
            return null;

        var buffer = new MaaImageBuffer();
        try
        {
            if (screencap)
            {
                var status = maaController.Screencap().Wait();
                if (status != MaaJobStatus.Succeeded)
                {
                    buffer.Dispose();
                    return null;
                }
            }
            if (!maaController.GetCachedImage(buffer))
            {
                buffer.Dispose();
                return null;
            }

            return buffer;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"GetImage failed: {ex.Message}");
            buffer.Dispose();
            return null;
        }
    }

    #endregion

    #region MaaTasker初始化

    private async Task<IMaaController?> InitializeScreenshotControllerAsync(CancellationToken token)
    {
        if (!UseSeparateScreenshotTasker)
            return MaaTasker?.Controller;

        if (Design.IsDesignMode)
            return null;

        MaaController controller = null;
        try
        {
            controller = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return InitializeController(ViewModel?.CurrentController ?? MaaControllerTypes.Adb, logConfig: false);
            }, token: token, name: "截图控制器检测", catchException: true, shouldLog: false, noMessage: true);

            var displayShortSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayShortSide;
            var displayLongSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayLongSide;
            var displayRaw = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayRaw;

            if (displayLongSide != null && displayShortSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetLongSide(Convert.ToInt32(displayLongSide.Value));
            if (displayShortSide != null && displayLongSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetShortSide(Convert.ToInt32(displayShortSide.Value));
            if (displayRaw != null && displayShortSide == null && displayLongSide == null)
                controller.SetOption_ScreenshotUseRawSize(displayRaw.Value);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot controller init failed: {ex.Message}");
            try
            {
                controller?.Dispose();
            }
            catch (Exception disposeEx)
            {
                LoggerHelper.Warning($"Screenshot controller dispose failed: {disposeEx.Message}");
            }
            return null;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            var linkStatus = controller.LinkStart().Wait();
            if (linkStatus != MaaJobStatus.Succeeded)
            {
                controller.Dispose();
                return null;
            }

            return controller;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot controller link failed: {ex.Message}");
            return null;
        }
    }

    private async Task<MaaTasker?> InitializeScreenshotTaskerAsync(CancellationToken token)
    {
        if (!UseSeparateScreenshotTasker)
            return MaaTasker;

        if (Design.IsDesignMode)
            return null;

        MaaResource? maaResource = null;
        try
        {
            // var currentResource = ViewModel?.CurrentResources
            //     .FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            // var resources = currentResource?.ResolvedPath ?? currentResource?.Path ?? [];
            // resources = resources.Select(Path.GetFullPath).ToList();

            var resources = new List<string>();
            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
            var controllerName = controllerType.ToJsonKey();
            var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
                c.Type != null && c.Type.Equals(controllerName, StringComparison.OrdinalIgnoreCase));

            if (controllerConfig?.AttachResourcePath != null)
            {
                var attachedPaths = MaaInterface.ReplacePlaceholder(controllerConfig.AttachResourcePath, AppContext.BaseDirectory);
                if (attachedPaths != null)
                {
                    resources.AddRange(attachedPaths.Select(Path.GetFullPath));
                }
            }

            maaResource = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (resources.Count > 0)
                {
                    return new MaaResource(resources);
                }
                return new MaaResource();
            }, token: token, name: "截图资源检测", catchException: true, shouldLog: false, noMessage: true);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker resource init failed: {ex.Message}");
            return null;
        }

        MaaController controller = null;
        try
        {
            controller = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return InitializeController(ViewModel?.CurrentController ?? MaaControllerTypes.Adb, logConfig: false);
            }, token: token, name: "截图控制器检测", catchException: true, shouldLog: false, noMessage: true);

            var displayShortSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayShortSide;
            var displayLongSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayLongSide;
            var displayRaw = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayRaw;

            if (displayLongSide != null && displayShortSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetLongSide(Convert.ToInt32(displayLongSide.Value));
            if (displayShortSide != null && displayLongSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetShortSide(Convert.ToInt32(displayShortSide.Value));
            if (displayRaw != null && displayShortSide == null && displayLongSide == null)
                controller.SetOption_ScreenshotUseRawSize(displayRaw.Value);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker controller init failed: {ex.Message}");
            return null;
        }

        try
        {
            token.ThrowIfCancellationRequested();

            var tasker = new MaaTasker
            {
                Controller = controller,
                Resource = maaResource,
                Toolkit = MaaProcessor.Toolkit,
                Global = MaaProcessor.Global,
                DisposeOptions = DisposeOptions.All,
            };

            // ConfigureScreenshotTasker(tasker);

            var linkStatus = tasker.Controller?.LinkStart().Wait();
            if (linkStatus != MaaJobStatus.Succeeded)
            {
                tasker.Dispose();
                return null;
            }

            return tasker;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Screenshot tasker init failed: {ex.Message}");
            return null;
        }
    }

    private void ConfigureScreenshotTasker(MaaTasker tasker)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs", "log_screencap");
        // if (!Directory.Exists(logDir))
        //     Directory.CreateDirectory(logDir);
        LoggerHelper.Info("screenlog dir:" + logDir);
        tasker.Global.SetOption_LogDir(logDir);
    }

    async private Task<(MaaTasker?, bool, bool)> InitializeMaaTasker(CancellationToken token) // 添加 async 和 token
    {
        var InvalidResource = false;
        var ShouldRetry = true;
        AutoInitDictionary.Clear();
        LoggerHelper.Info(LangKeys.LoadingResources.ToLocalization());

        if (Design.IsDesignMode)
        {
            return (null, false, false);
        }
        MaaResource maaResource = null;
        try
        {
            var currentResource = ViewModel?.CurrentResources
                .FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            // 优先使用 ResolvedPath（运行时路径），如果没有则使用 Path
            var resources = currentResource?.ResolvedPath ?? currentResource?.Path ?? [];
            resources = resources.Select(Path.GetFullPath).ToList();

            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
            var controllerName = controllerType.ToJsonKey();
            var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
                c.Type != null && c.Type.Equals(controllerName, StringComparison.OrdinalIgnoreCase));

            if (controllerConfig?.AttachResourcePath != null)
            {
                var attachedPaths = MaaInterface.ReplacePlaceholder(controllerConfig.AttachResourcePath, AppContext.BaseDirectory);
                if (attachedPaths != null)
                {
                    resources.AddRange(attachedPaths.Select(Path.GetFullPath));
                }
            }

            LoggerHelper.Info($"Resource: {string.Join(",", resources)}");


            maaResource = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return new MaaResource(resources);
            }, token: token, name: "资源检测", catchException: true, shouldLog: false, handleError: exception =>
            {
                HandleInitializationError(exception, LangKeys.LoadResourcesFailed.ToLocalization(), LangKeys.LoadResourcesFailedDetail.ToLocalization());
                AddLog(LangKeys.LoadResourcesFailed.ToLocalization(), Brushes.OrangeRed, changeColor: false);
                InvalidResource = true;
                throw exception;
            });

            Instances.PerformanceUserControlModel.ChangeGpuOption(maaResource, Instances.PerformanceUserControlModel.GpuOption);

            LoggerHelper.Info(
                $"GPU acceleration: {(Instances.PerformanceUserControlModel.GpuOption.IsDirectML ? Instances.PerformanceUserControlModel.GpuOption.Adapter.AdapterName : Instances.PerformanceUserControlModel.GpuOption.Device.ToString())}{(Instances.PerformanceUserControlModel.GpuOption.IsDirectML ? $",Adapter Id: {Instances.PerformanceUserControlModel.GpuOption.Adapter.AdapterId}" : "")}");

        }
        catch (OperationCanceledException)
        {
            ShouldRetry = false;
            LoggerHelper.Warning("Resource loading was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaJobStatusException)
        {
            ShouldRetry = false;
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            ShouldRetry = false;
            LoggerHelper.Error("Initialization resource error", e);
            return (null, InvalidResource, ShouldRetry);
        }

        // 初始化控制器部分同理
        MaaController controller = null;
        try
        {
            controller = await TaskManager.RunTaskAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return InitializeController(ViewModel?.CurrentController ?? MaaControllerTypes.Adb, logConfig: true);
            }, token: token, name: "控制器检测", catchException: true, shouldLog: false, handleError: exception => HandleInitializationError(exception,
                LangKeys.ConnectingEmulatorOrWindow.ToLocalization()
                    .FormatWith((ViewModel?.CurrentController ?? MaaControllerTypes.Adb) == MaaControllerTypes.Adb
                        ? LangKeys.Emulator.ToLocalization()
                        : LangKeys.Window.ToLocalization()), true,
                LangKeys.InitControllerFailed.ToLocalization()));

            var displayShortSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayShortSide;

            var displayLongSide = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayLongSide;
            var displayRaw = Interface?.Controller?.Find(c => c.Type != null && c.Type.Equals(ViewModel?.CurrentController.ToJsonKey(), StringComparison.OrdinalIgnoreCase))?.DisplayRaw;
            if (displayLongSide != null && displayShortSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetLongSide(Convert.ToInt32(displayLongSide.Value));
            if (displayShortSide != null && displayLongSide == null && displayRaw == null)
                controller.SetOption_ScreenshotTargetShortSide(Convert.ToInt32(displayShortSide.Value));
            if (displayRaw != null && displayShortSide == null && displayLongSide == null)
                controller.SetOption_ScreenshotUseRawSize(displayRaw.Value);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning("Controller initialization was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException)
        {
            return (null, InvalidResource, ShouldRetry); // 控制器异常可以重试
        }
        catch (Exception e)
        {
            LoggerHelper.Error("Initialization controller error", e);
            return (null, InvalidResource, ShouldRetry); // 控制器错误可以重试
        }

        try
        {
            token.ThrowIfCancellationRequested();


            var tasker = new MaaTasker
            {
                Controller = controller,
                Resource = maaResource,
                Toolkit = Toolkit,
                Global = Global,
                DisposeOptions = DisposeOptions.All,
            };

            // 尝试连接控制器，验证连接是否成功
            // 这对于 Win32 控制器特别重要，因为当 HWnd 为 IntPtr.Zero 时，
            // MaaWin32Controller 创建成功但LinkStart 会失败
            var linkStatus = tasker.Controller?.LinkStart().Wait();
            if (linkStatus != MaaJobStatus.Succeeded)
            {
                LoggerHelper.Warning($"Controller LinkStart failed with status: {linkStatus}");
                tasker.Dispose();
                return (null, InvalidResource, ShouldRetry);
            }

            tasker.Releasing += (_, _) =>
            {
                tasker.Callback -= HandleCallBack;
            };

            try
            {
                var tempMFADir = Path.Combine(AppContext.BaseDirectory, "temp_mfa");
                if (Directory.Exists(tempMFADir))
                    Directory.Delete(tempMFADir, true);

                var tempMaaDir = Path.Combine(AppContext.BaseDirectory, "temp_maafw");
                if (Directory.Exists(tempMaaDir))
                    Directory.Delete(tempMaaDir, true);

                var tempResDir = Path.Combine(AppContext.BaseDirectory, "temp_res");
                if (Directory.Exists(tempResDir))
                    Directory.Delete(tempResDir, true);
            }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
            }

            // 获取代理配置并启动 Agent（支持多 Agent）
            var agentConfigs = Interface?.Agent;
            if (AgentHelper.HasAgentConfigs(agentConfigs) && !_agentStarted)
            {
                try
                {
                    AgentHelper.KillAllAgents(_agentContexts);
                    _agentContexts = await AgentHelper.StartAgentsAsync(tasker, agentConfigs!, InstanceConfiguration, this, token);
                    if (_agentContexts.Count == 0 && agentConfigs!.Any(a => a.ChildExec != null))
                    {
                        // Agent 启动失败（StartAgentsAsync 内部已处理错误日志）
                        ShouldRetry = false;
                        return (null, InvalidResource, ShouldRetry);
                    }
                    _agentStarted = true;
                }
                catch (OperationCanceledException)
                {
                    LoggerHelper.Info("Agent initialization was canceled by user");
                    AgentHelper.KillAllAgents(_agentContexts);
                    throw;
                }
                catch (Exception ex)
                {
                    AddLogByKey(LangKeys.AgentStartFailed, Brushes.OrangeRed, changeColor: false);
                    LoggerHelper.Error(ex);
                    var isNullReference = ex is NullReferenceException
                        || ex.Message.Contains("Object reference not set to an instance of an object.", StringComparison.OrdinalIgnoreCase);
                    if (isNullReference)
                        ToastHelper.Error(LangKeys.AgentStartFailed.ToLocalization());
                    else
                        ToastHelper.Error(LangKeys.AgentStartFailed.ToLocalization(), ex.Message);
                    AgentHelper.KillAllAgents(_agentContexts);
                    ShouldRetry = false;
                    return (null, InvalidResource, ShouldRetry);
                }
            }
            RegisterCustomRecognitionsAndActions(tasker);
            ViewModel?.SetConnected(true);
            //  tasker.Utility.SetOption_Recording(ConfigurationManager.Maa.GetValue(ConfigurationKeys.Recording, false));
            tasker.Global.SetOption_SaveDraw(ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveDraw, false));
            tasker.Global.SetOption(GlobalOption.SaveOnError, ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveOnError, true));
            tasker.Global.SetOption_DebugMode(ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false));
            LoggerHelper.Info("Maafw debug mode: " + ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false));
            // 注意：只订阅一次回调，避免嵌套订阅导致内存泄漏
            tasker.Callback += HandleCallBack;
            ResetScreencapFailureLogFlags();
            return (tasker, InvalidResource, ShouldRetry);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning("Tasker initialization was canceled");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException)
        {
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            LoggerHelper.Error("Initialization tasker error", e);
            return (null, InvalidResource, ShouldRetry);
        }

    }
// public void HandleControllerCallBack(object? sender, MaaCallbackEventArgs args)
// {
//     var message = args.Message;
//     if (message == MaaMsg.Controller.Action.Failed)
//     {
//         HandleScreencapFailure(true);
//     }
// }

    public void HandleCallBack(object? sender, MaaCallbackEventArgs args)
    {
        JObject jObject;
        try
        {
            jObject = JObject.Parse(args.Details);
        }
        catch
        {
            jObject = new JObject();
        }

        MaaTasker? tasker = null;
        if (sender is MaaTasker t)
            tasker = t;
        if (sender is MaaContext context)
            tasker = context.Tasker;
        if (tasker != null && Instances.GameSettingsUserControlModel.ShowHitDraw)
        {
            var name = jObject["name"]?.ToString() ?? string.Empty;
            if (args.Message.StartsWith(MaaMsg.Node.Recognition.Succeeded) || args.Message.StartsWith(MaaMsg.Node.Action.Succeeded))
            {
                if (jObject["reco_id"] != null)
                {
                    var recoId = Convert.ToInt64(jObject["reco_id"]?.ToString() ?? string.Empty);
                    if (recoId > 0)
                    {
                        Bitmap? bitmapToSet = null;
                        try
                        {
                            //使用 using 确保资源正确释放
                            using var rect = new MaaRectBuffer();
                            var imageBuffer = new MaaImageBuffer();
                            using var imageListBuffer = new MaaImageListBuffer();
                            tasker.GetRecognitionDetail(recoId, out string node,
                                out var algorithm,
                                out var hit,
                                rect,
                                out var detailJson,
                                imageBuffer, imageListBuffer);
                            var bitmap = imageBuffer.ToBitmap();
                            if (bitmap != null)
                            {
                                if (hit)
                                {
                                    var newBitmap = bitmap.DrawRectangle(rect, Brushes.LightGreen, 1.5f);
                                    // 如果 DrawRectangle 返回了新的 Bitmap，释放原始的
                                    if (!ReferenceEquals(newBitmap, bitmap))
                                    {
                                        bitmap.Dispose();
                                    }
                                    bitmap = newBitmap;
                                }
                                bitmapToSet = bitmap;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Warning($"HandleCallBack recognition error: {ex.Message}");
                            bitmapToSet?.Dispose();
                            bitmapToSet = null;
                        }


                        if (bitmapToSet != null)
                        {
                            var finalBitmap = bitmapToSet;
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                if (MaaProcessorManager.Instance.Current == this)
                                {
                                    // 释放旧的截图
                                    var oldImage = Instances.ScreenshotViewModel.ScreenshotImage;
                                    Instances.ScreenshotViewModel.ScreenshotImage = finalBitmap;
                                    Instances.ScreenshotViewModel.TaskName = name;
                                    oldImage?.Dispose();
                                }
                                else
                                {
                                    finalBitmap.Dispose();
                                }
                            });
                        }
                    }
                }

                if (jObject["action_id"] != null)
                {
                    var actionId = Convert.ToInt64(jObject["action_id"]?.ToString() ?? string.Empty);
                    if (actionId > 0)
                    {
                        Bitmap? bitmapToSet = null;
                        try
                        {
                            // 使用 using 确保资源正确释放
                            using var rect = new MaaRectBuffer();
                            using var imageBuffer = new MaaImageBuffer();
                            tasker.GetCachedImage(imageBuffer);
                            var bitmap = imageBuffer.ToBitmap();
                            tasker.GetActionDetail(actionId, out _, out _, rect, out var isSucceeded, out _);
                            if (bitmap != null)
                            {
                                if (isSucceeded)
                                {
                                    var newBitmap = bitmap.DrawRectangle(rect, Brushes.LightGreen, 1.5f);
                                    // 如果 DrawRectangle 返回了新的 Bitmap，释放原始的
                                    if (!ReferenceEquals(newBitmap, bitmap))
                                    {
                                        bitmap.Dispose();
                                    }
                                    bitmap = newBitmap;
                                }
                                bitmapToSet = bitmap;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Warning($"HandleCallBack action error: {ex.Message}");
                            bitmapToSet?.Dispose();
                            bitmapToSet = null;
                        }


                        if (bitmapToSet != null)
                        {
                            var finalBitmap = bitmapToSet;
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                if (MaaProcessorManager.Instance.Current == this)
                                {
                                    // 释放旧的截图
                                    var oldImage = Instances.ScreenshotViewModel.ScreenshotImage;
                                    Instances.ScreenshotViewModel.ScreenshotImage = finalBitmap;
                                    Instances.ScreenshotViewModel.TaskName = name;
                                    oldImage?.Dispose();
                                }
                                else
                                {
                                    finalBitmap.Dispose();
                                }
                            });
                        }
                    }
                }
            }
        }

        if (jObject.ContainsKey("focus"))
        {
            _focusHandler ??= new FocusHandler(AutoInitDictionary, ViewModel!);
            _focusHandler.UpdateDictionary(AutoInitDictionary);

            // 获取当前截图用于新协议 {image} 占位符替换
            // 优先通过 GetRecognitionDetail 获取识别结果图片，失败后 fallback 到 GetCachedImage
            MaaImageBuffer? focusImageBuffer = null;
            if (tasker != null)
            {
                try
                {
                    // 先尝试通过 reco_id 获取识别结果图片
                    var recoIdToken = jObject["reco_id"];
                    if (recoIdToken != null)
                    {
                        var recoId = Convert.ToInt64(recoIdToken.ToString());
                        if (recoId > 0)
                        {
                            try
                            {
                                focusImageBuffer = new MaaImageBuffer();
                                using var imageListBuffer = new MaaImageListBuffer();
                                using var rect = new MaaRectBuffer();
                                var recoSuccess = tasker.GetRecognitionDetail(recoId, out _, out _, out _, rect, out _, focusImageBuffer, imageListBuffer);
                                if (!recoSuccess || focusImageBuffer.IsInvalid || focusImageBuffer.IsEmpty)
                                {
                                    focusImageBuffer.Dispose();
                                    focusImageBuffer = null;
                                }
                            }
                            catch (Exception ex)
                            {
                                LoggerHelper.Warning($"HandleCallBack focus GetRecognitionDetail error: {ex.Message}");
                                focusImageBuffer?.Dispose();
                                focusImageBuffer = null;
                            }
                        }
                    }

                    // fallback: 如果识别结果图片获取失败，使用 GetCachedImage
                    if (focusImageBuffer == null)
                    {
                        focusImageBuffer = new MaaImageBuffer();
                        if (!tasker.GetCachedImage(focusImageBuffer))
                        {
                            focusImageBuffer.Dispose();
                            focusImageBuffer = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"HandleCallBack focus image error: {ex.Message}");
                    focusImageBuffer?.Dispose();
                    focusImageBuffer = null;
                }
            }

            try
            {
                _focusHandler.DisplayFocus(jObject, args.Message, args.Details, focusImageBuffer);
            }
            finally
            {
                focusImageBuffer?.Dispose();
            }
        }
    }

    private void HandleInitializationError(Exception e,
        string message,
        bool hasWarning = false,
        string waringMessage = "")
    {
        ToastHelper.Error(message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error(e.ToString());
    }

    private void HandleInitializationError(Exception e,
        string title,
        string message,
        bool hasWarning = false,
        string waringMessage = "")
    {
        ToastHelper.Error(title, message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error(e.ToString());
    }

    private MaaController InitializeController(MaaControllerTypes controllerType, bool logConfig)
    {
        ConnectToMAA(logConfig);

        // 对于 Win32 和 Gamepad 控制器，检查管理员权限
        if (OperatingSystem.IsWindows() && (controllerType == MaaControllerTypes.Win32 || controllerType == MaaControllerTypes.Gamepad))
        {
            if (!CheckTargetProcessAdminPermission())
            {
                throw new MaaException("目标进程以管理员权限运行，需要以管理员身份运行本程序");
            }
        }

        switch (controllerType)
        {
            case MaaControllerTypes.Adb:
                if (logConfig)
                {
                    LoggerHelper.Info($"Name: {Config.AdbDevice.Name}");
                    LoggerHelper.Info($"AdbPath: {Config.AdbDevice.AdbPath}");
                    LoggerHelper.Info($"AdbSerial: {Config.AdbDevice.AdbSerial}");
                    LoggerHelper.Info($"ScreenCap: {Config.AdbDevice.ScreenCap}");
                    LoggerHelper.Info($"Input: {Config.AdbDevice.Input}");
                    LoggerHelper.Info($"Config: {Config.AdbDevice.Config}");
                }

                return new MaaAdbController(
                    Config.AdbDevice.AdbPath,
                    Config.AdbDevice.AdbSerial,
                    Config.AdbDevice.ScreenCap, Config.AdbDevice.Input,
                    !string.IsNullOrWhiteSpace(Config.AdbDevice.Config) ? Config.AdbDevice.Config : "{}",
                    Path.Combine(AppContext.BaseDirectory, "libs", "MaaAgentBinary")
                );

            case MaaControllerTypes.PlayCover:
                if (logConfig)
                {
                    LoggerHelper.Info($"PlayCover Address: {Config.PlayCover.PlayCoverAddress}");
                    LoggerHelper.Info($"PlayCover BundleId: {Config.PlayCover.UUID}");
                }

                return new MaaPlayCoverController(Config.PlayCover.PlayCoverAddress, Config.PlayCover.UUID);

            case MaaControllerTypes.Gamepad:
                // Gamepad 控制器使用 Win32 控制器的配置，但会创建虚拟手柄
                if (logConfig)
                {
                    LoggerHelper.Info($"Gamepad Controller");
                    LoggerHelper.Info($"Name: {Config.DesktopWindow.Name}");
                    LoggerHelper.Info($"HWnd: {Config.DesktopWindow.HWnd}");
                    LoggerHelper.Info($"ScreenCap: {Config.DesktopWindow.ScreenCap}");

                    // 获取 Gamepad 特定配置
                    var gamepadConfig = Interface?.Controller?.FirstOrDefault(c =>
                        c.Type?.Equals("gamepad", StringComparison.OrdinalIgnoreCase) == true)?.Gamepad;
                    if (gamepadConfig != null)
                    {
                        LoggerHelper.Info($"GamepadType: {gamepadConfig.GamepadType ?? "Xbox360"}");
                        LoggerHelper.Info($"ClassRegex: {gamepadConfig.ClassRegex}");
                        LoggerHelper.Info($"WindowRegex: {gamepadConfig.WindowRegex}");
                    }
                }

                // Gamepad 控制器目前使用 Win32 控制器实现
                // TODO: 当MaaFramework 支持 Gamepad 控制器时，替换为专用实现
                return new MaaWin32Controller(
                    Config.DesktopWindow.HWnd,
                    Config.DesktopWindow.ScreenCap, Config.DesktopWindow.Mouse, Config.DesktopWindow.KeyBoard,
                    Config.DesktopWindow.Link,
                    Config.DesktopWindow.Check);

            case MaaControllerTypes.Win32:
            default:
                if (logConfig)
                {
                    LoggerHelper.Info($"Name: {Config.DesktopWindow.Name}");
                    LoggerHelper.Info($"HWnd: {Config.DesktopWindow.HWnd}");
                    LoggerHelper.Info($"ScreenCap: {Config.DesktopWindow.ScreenCap}");
                    LoggerHelper.Info($"MouseInput: {Config.DesktopWindow.Mouse}");
                    LoggerHelper.Info($"KeyboardInput: {Config.DesktopWindow.KeyBoard}");
                    LoggerHelper.Info($"Link: {Config.DesktopWindow.Link}");
                    LoggerHelper.Info($"Check: {Config.DesktopWindow.Check}");
                }

                return new MaaWin32Controller(
                    Config.DesktopWindow.HWnd,
                    Config.DesktopWindow.ScreenCap, Config.DesktopWindow.Mouse, Config.DesktopWindow.KeyBoard,
                    Config.DesktopWindow.Link,
                    Config.DesktopWindow.Check);
        }
    }


    public static bool CheckInterface(out string Name, out string NameFallBack, out string Version, out string CustomTitle, out string CustomTitleFallBack)
    {
        // 支持 interface.json 和 interface.jsonc
        if (GetInterfaceFilePath() == null)
        {
            LoggerHelper.Info("未找到interface文件，生成interface.json...");
            Interface = new MaaInterface
            {
                Version = "1.0",
                Name = "Debug",
                Task = [],
                Resource =
                [
                    new MaaInterface.MaaInterfaceResource()
                    {
                        Name = "默认",
                        Path =
                        [
                            "{PROJECT_DIR}/resource/base",
                        ],
                    },
                ],
                Option = new Dictionary<string, MaaInterface.MaaInterfaceOption>
                {
                    {
                        "测试", new MaaInterface.MaaInterfaceOption()
                        {
                            Cases =
                            [

                                new MaaInterface.MaaInterfaceOptionCase
                                {
                                    Name = "测试1",
                                    PipelineOverride = new Dictionary<string, JToken>()
                                },
                                new MaaInterface.MaaInterfaceOptionCase
                                {
                                    Name = "测试2",
                                    PipelineOverride = new Dictionary<string, JToken>()
                                }
                            ]
                        }
                    }
                }
            };
            string resourceDir = Path.Combine(AppContext.BaseDirectory, "resource", "base");
            if (!Directory.Exists(resourceDir))
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, resourceDir));
            JsonHelper.SaveJson(Path.Combine(AppContext.BaseDirectory, "interface.json"),
                Interface, new MaaInterfaceSelectAdvancedConverter(true), new MaaInterfaceSelectOptionConverter(true));
            Name = Interface?.Label ?? string.Empty;
            NameFallBack = Interface?.Name ?? string.Empty;
            Version = Interface?.Version ?? string.Empty;
            CustomTitle = Interface?.Title ?? string.Empty;
            CustomTitleFallBack = Interface?.CustomTitle ?? string.Empty;
            return true;
        }
        Name = string.Empty;
        Version = string.Empty;
        CustomTitle = string.Empty;
        NameFallBack = string.Empty;
        CustomTitleFallBack = string.Empty;
        return false;
    }

// 防止 interface 加载失败时 Toast 重复显示
    private static bool _interfaceLoadErrorShown = false;

    public static (string Key, string Fallback, string Version, string CustomTitle, string CustomFallback) ReadInterface()
    {
        if (CheckInterface(out string name, out string back, out string version, out string customTitle, out var fallBack))
        {
            return (name, back, version, customTitle, fallBack);
        }

        var interfacePath = GetInterfaceFilePath() ?? Path.Combine(AppContext.BaseDirectory, "interface.json");
        var interfaceFileName = Path.GetFileName(interfacePath);
        var defaultValue = new MaaInterface();

        try
        {
            // 使用递归加载支持 import
            Interface = LoadMaaInterfaceRecursive(interfacePath);
        }
        catch (Exception ex)
        {
            Interface = defaultValue;
            var error = "";

            try
            {
                if (File.Exists(interfacePath))
                {
                    var content = File.ReadAllText(interfacePath);
                    // 使用 JsonLoadSettings 忽略注释，支持 JSONC 格式
                    var @interface = JObject.Parse(content, JsoncLoadSettings);
                    if (@interface != null)
                    {
                        defaultValue.MFAMinVersion = @interface["mfa_min_version"]?.ToString();
                        defaultValue.MFAMaxVersion = @interface["mfa_max_version"]?.ToString();
                        defaultValue.CustomTitle = @interface["custom_title"]?.ToString();
                        defaultValue.Title = @interface["title"]?.ToString();
                        defaultValue.Name = @interface["name"]?.ToString();
                        defaultValue.Url = @interface["url"]?.ToString();
                        defaultValue.Github = @interface["github"]?.ToString();
                    }
                }
                // 在 UI 层面显示 Toast 错误提示（只显示一次）
                if (!_interfaceLoadErrorShown)
                {
                    _interfaceLoadErrorShown = true;
                    error = LangKeys.FileLoadFailed.ToLocalizationFormatted(false, interfaceFileName);
                    var errorDetail = LangKeys.FileLoadFailedDetail.ToLocalizationFormatted(false, interfaceFileName);
                    // 延迟添加 UI 日志，确保 TaskQueueViewModel 已初始化
                    foreach (var processor in Processors)
                    {
                        processor.ViewModel?.AddLog($"error:{error}", (IBrush?)null);
                    }
                    ToastHelper.Error(error, errorDetail, duration: 15);
                }
            }
            catch (Exception e)
            {
                LoggerHelper.Error(e);
                // 即使解析失败也显示 Toast 错误提示（只显示一次）
                if (!_interfaceLoadErrorShown)
                {
                    _interfaceLoadErrorShown = true;
                    error = LangKeys.FileLoadFailed.ToLocalizationFormatted(false, interfaceFileName);
                    foreach (var processor in Processors)
                    {
                        processor.ViewModel?.AddLog($"error:{error}", (IBrush?)null);
                    }
                    ToastHelper.Error(
                        error,
                        e.Message,
                        duration: 10);
                }
            }
        }

        return (Interface?.Label ?? string.Empty, Interface?.Name ?? string.Empty, Interface?.Version ?? string.Empty, Interface?.Title ?? string.Empty, Interface?.CustomTitle ?? string.Empty);
    }

    private static MaaInterface LoadMaaInterfaceRecursive(string path, HashSet<string>? loadedPaths = null)
    {
        loadedPaths ??= new HashSet<string>();
        var fullPath = Path.GetFullPath(path);

        if (loadedPaths.Contains(fullPath))
        {
            LoggerHelper.Warning($"Circular dependency detected: {fullPath}");
            return new MaaInterface();
        }
        loadedPaths.Add(fullPath);

        // 使用 JsonHelper 加载，如果失败会抛出异常
        var loaded = JsonHelper.LoadJson<MaaInterface>(fullPath, null, new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false));

        if (loaded == null)
            throw new Exception($"Failed to load interface file: {fullPath}");

        var result = new MaaInterface();

        if (loaded.Import != null)
        {
            foreach (var importPath in loaded.Import)
            {
                var resolvedPath = MaaInterface.ReplacePlaceholder(importPath, Path.GetDirectoryName(fullPath));
                if (string.IsNullOrWhiteSpace(resolvedPath)) continue;

                try
                {
                    var imported = LoadMaaInterfaceRecursive(resolvedPath, loadedPaths);

                    // 仅支持导入 task、option 和 preset 字段（任务 16）
                    var filteredImport = new MaaInterface
                    {
                        Task = imported.Task,
                        Option = imported.Option,
                        Preset = imported.Preset
                    };

                    result.Merge(filteredImport);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"Failed to load imported interface '{resolvedPath}': {ex.Message}");
                }
            }
        }

        result.Merge(loaded);
        return result;
    }

    public bool InitializeData(Collection<DragItemViewModel>? dragItem = null)
    {
        // 如果 Interface 已加载（静态缓存），直接提取元数据，避免重复读取和解析 interface.json
        string name, back, version, customTitle, fallback;
        if (Interface != null)
        {
            name = Interface.Label ?? string.Empty;
            back = Interface.Name ?? string.Empty;
            version = Interface.Version ?? string.Empty;
            customTitle = Interface.Title ?? string.Empty;
            fallback = Interface.CustomTitle ?? string.Empty;
        }
        else
        {
            (name, back, version, customTitle, fallback) = ReadInterface();
        }
        if ((!string.IsNullOrWhiteSpace(name) && !name.Equals("debug", StringComparison.OrdinalIgnoreCase)) || !string.IsNullOrWhiteSpace(back))
            Instances.RootViewModel.ShowResourceKeyAndFallBack(name, back);
        if (!string.IsNullOrWhiteSpace(version) && !version.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            Instances.RootViewModel.ShowResourceVersion(version);
            Instances.VersionUpdateSettingsUserControlModel.ResourceVersion = version;

            // 首次初始化时，根据资源版本自动设置更新来源
            // 优先级：内测(Alpha=0) > 公测(Beta=1) > 稳定(Stable=2)
            // 只有当资源版本的优先级高于当前设置时才修改
            if (!ConfigurationManager.Current.GetValue(ConfigurationKeys.ResourceUpdateChannelInitialized, false))
            {
                var resourceVersionType = version.ToVersionType();
                var currentChannelIndex = Instances.VersionUpdateSettingsUserControlModel.ResourceUpdateChannelIndex;
                var currentChannelType = currentChannelIndex.ToVersionType();

                // 如果资源版本类型的优先级更高（数值更小），则更新设置
                if (resourceVersionType < currentChannelType)
                {
                    var newIndex = (int)resourceVersionType;
                    Instances.VersionUpdateSettingsUserControlModel.ResourceUpdateChannelIndex = newIndex;
                    LoggerHelper.Info($"根据资源版本 '{version}' 自动将更新来源设置为 {resourceVersionType}");
                }
                // 标记已初始化
                ConfigurationManager.Current.SetValue(ConfigurationKeys.ResourceUpdateChannelInitialized, true);
            }
        }

        if (!string.IsNullOrWhiteSpace(customTitle) || !string.IsNullOrWhiteSpace(fallback))
            Instances.RootViewModel.ShowCustomTitleAndFallBack(customTitle, fallback);

        if (Interface != null)
        {
            AppendVersionLog(Interface.Version);
            TasksSource.Clear();
            LoadTasks(Interface.Task ?? new List<MaaInterface.MaaInterfaceTask>(), dragItem);
        }
        return LoadTask();
    }

    private bool LoadTask()
    {
        try
        {
            var fileCount = 0;
            if (ViewModel?.CurrentResources.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(ViewModel?.CurrentResource) && !string.IsNullOrWhiteSpace(ViewModel?.CurrentResources[0].Name))
                    ViewModel!.CurrentResource = ViewModel.CurrentResources[0].Name;
            }
            if (ViewModel?.CurrentResources.Any(r => r.Name == ViewModel.CurrentResource) == true)
            {
                var resources = ViewModel.CurrentResources.FirstOrDefault(r => r.Name == ViewModel.CurrentResource);
                // 优先使用 ResolvedPath（运行时路径），如果没有则使用 Path
                var resourcePaths = resources?.ResolvedPath ?? resources?.Path;
                if (resourcePaths != null)
                {
                    foreach (var resourcePath in resourcePaths)
                    {
                        var pipeline = Path.Combine(resourcePath, "pipeline");
                        if (!Path.Exists(pipeline))
                            break;
                        var jsonFiles = Directory.GetFiles(Path.GetFullPath(pipeline), "*.json", SearchOption.AllDirectories);
                        var jsoncFiles = Directory.GetFiles(Path.GetFullPath(pipeline), "*.jsonc", SearchOption.AllDirectories);
                        var allFiles = jsonFiles.Concat(jsoncFiles).ToArray();
                        fileCount = allFiles.Length;
                        // var taskDictionaryA = new Dictionary<string, MaaNode>();
                        // foreach (var file in allFiles)
                        // {
                        //     if (file.Contains("default_pipeline.json", StringComparison.OrdinalIgnoreCase))
                        //         continue;
                        //     var content = File.ReadAllText(file);
                        //     LoggerHelper.Info($"Loading Pipeline: {file}");
                        //     try
                        //     {
                        //         var taskData = JsonConvert.DeserializeObject<Dictionary<string, MaaNode>>(content);
                        //         if (taskData == null || taskData.Count == 0)
                        //             continue;
                        //         foreach (var task in taskData)
                        //         {
                        //             if (!taskDictionaryA.TryAdd(task.Key, task.Value))
                        //             {
                        //                 ToastHelper.Error(LangKeys.DuplicateTaskError.ToLocalizationFormatted(false, task.Key));
                        //                 return false;
                        //             }
                        //         }
                        //     }
                        //     catch (Exception e)
                        //     {
                        //         LoggerHelper.Warning(e);
                        //     }
                        // }

                        // taskDictionary = taskDictionary.MergeMaaNodes(taskDictionaryA);
                    }
                }
            }
            // 优先使用 ResolvedPath（运行时路径），如果没有则使用 Path
            var currentRes = ViewModel?.CurrentResources.FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            var resourceP = string.IsNullOrWhiteSpace(ViewModel?.CurrentResource)
                ? ResourceBase
                : (currentRes?.ResolvedPath?[0] ?? currentRes?.Path?[0]) ?? ResourceBase;
            var resourcePs = string.IsNullOrWhiteSpace(ViewModel?.CurrentResource)
                ? [ResourceBase]
                : (currentRes?.ResolvedPath ?? currentRes?.Path);

            if (resourcePs is { Count: > 0 })
            {
                foreach (var rp in resourcePs)
                {
                    if (string.IsNullOrWhiteSpace(rp))
                        continue;

                    try
                    {
                        // 验证路径是否有效
                        var fullPath = Path.GetFullPath(rp);
                        if (!Directory.Exists(fullPath))
                            Directory.CreateDirectory(fullPath);
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"Failed to create resource directory '{rp}': {ex.Message}");
                    }
                }
            }
            if (fileCount == 0)
            {
                var pipeline = Path.Combine(resourceP, "pipeline");
                if (!string.IsNullOrWhiteSpace(pipeline) && !Directory.Exists(pipeline))
                {
                    try
                    {
                        Directory.CreateDirectory(pipeline);
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error(ex);
                    }
                }

                if (!File.Exists(Path.Combine(pipeline, "sample.json")))
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(pipeline, "sample.json"),
                            JsonConvert.SerializeObject(new Dictionary<string, MaaNode>
                            {
                                {
                                    "MFAAvalonia", new MaaNode
                                    {
                                        Action = "DoNothing"
                                    }
                                }
                            }, new JsonSerializerSettings()
                            {
                                Formatting = Formatting.Indented,
                                NullValueHandling = NullValueHandling.Ignore,
                                DefaultValueHandling = DefaultValueHandling.Ignore
                            }));
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error(ex);
                    }
                }
            }

            // PopulateTasks(taskDictionary);

            return true;
        }
        catch (Exception ex)
        {
            ToastHelper.Error(LangKeys.PipelineLoadError.ToLocalizationFormatted(false, ex.Message)
            );
            LoggerHelper.Error(ex);
            return false;
        }
    }

    private void PopulateTasks(Dictionary<string, MaaNode> taskDictionary)
    {
        // BaseNodes = taskDictionary;
        // foreach (var task in taskDictionary)
        // {
        //     task.Value.Name = task.Key;
        //     ValidateTaskLinks(taskDictionary, task);
        // }
    }

    private void ValidateTaskLinks(Dictionary<string, MaaNode> taskDictionary,
        KeyValuePair<string, MaaNode> task)
    {
        ValidateNextTasks(taskDictionary, task.Value.Name, task.Value.Next);
        ValidateNextTasks(taskDictionary, task.Value.Name, task.Value.OnError, "on_error");
        ValidateNextTasks(taskDictionary, task.Value.Name, task.Value.Interrupt, "interrupt");
    }

    private void ValidateNextTasks(Dictionary<string, MaaNode> taskDictionary,
        string? taskName,
        object? nextTasks,
        string name = "next")
    {
        if (nextTasks is List<string> tasks)
        {
            foreach (var task in tasks)
            {
                if (!taskDictionary.ContainsKey(task))
                {
                    ToastHelper.Error(LangKeys.Error.ToLocalization(), LangKeys.TaskNotFoundError.ToLocalizationFormatted(false, taskName, name, task));
                    LoggerHelper.Error(LangKeys.TaskNotFoundError.ToLocalizationFormatted(false, taskName, name, task));
                }
            }
        }
    }

    public void ConnectToMAA(bool logConfig)
    {
        if (logConfig)
        {
            LoggerHelper.Info("Loading MAA Controller Configuration...");
        }
        ConfigureMaaProcessorForADB(logConfig);
        ConfigureMaaProcessorForWin32(logConfig);
        ConfigureMaaProcessorForPlayCover();
    }

    private void ConfigureMaaProcessorForADB(bool logConfig)
    {
        if (ViewModel?.CurrentController == MaaControllerTypes.Adb)
        {
            var adbInputType = ConfigureAdbInputTypes();
            var adbScreenCapType = ConfigureAdbScreenCapTypes();

            Config.AdbDevice.Input = adbInputType;
            Config.AdbDevice.ScreenCap = adbScreenCapType;
            if (logConfig)
            {
                LoggerHelper.Info(
                    $"{LangKeys.AdbInputMode.ToLocalization()}{adbInputType},{LangKeys.AdbCaptureMode.ToLocalization()}{adbScreenCapType}");
            }
        }
    }

    public string ScreenshotType()
    {
        if (ViewModel?.CurrentController == MaaControllerTypes.Adb)
            return ConfigureAdbScreenCapTypes().ToString();
        return ConfigureWin32ScreenCapTypes().ToString();
    }


    private AdbInputMethods ConfigureAdbInputTypes()
    {
        var inputType = InstanceConfiguration.GetValue(ConfigurationKeys.AdbControlInputType,
            AdbInputMethods.None, [AdbInputMethods.All, AdbInputMethods.Default],
            new UniversalEnumConverter<AdbInputMethods>());
        return inputType switch
        {
            AdbInputMethods.None => Config.AdbDevice.Info?.InputMethods ?? AdbInputMethods.Default,
            _ => inputType
        };
    }

    private AdbScreencapMethods ConfigureAdbScreenCapTypes()
    {
        var screenCapType = InstanceConfiguration.GetValue(ConfigurationKeys.AdbControlScreenCapType,
            AdbScreencapMethods.None, [AdbScreencapMethods.All, AdbScreencapMethods.Default],
            new UniversalEnumConverter<AdbScreencapMethods>());
        return screenCapType switch
        {
            AdbScreencapMethods.None => Config.AdbDevice.Info?.ScreencapMethods ?? AdbScreencapMethods.Default,
            _ => screenCapType
        };
    }

    private void ConfigureMaaProcessorForWin32(bool logConfig)
    {
        if (ViewModel?.CurrentController == MaaControllerTypes.Win32)
        {
            var win32MouseInputType = ConfigureWin32MouseInputTypes();
            var win32KeyboardInputType = ConfigureWin32KeyboardInputTypes();
            var winScreenCapType = ConfigureWin32ScreenCapTypes();

            Config.DesktopWindow.Mouse = win32MouseInputType;
            Config.DesktopWindow.KeyBoard = win32KeyboardInputType;
            Config.DesktopWindow.ScreenCap = winScreenCapType;

            if (logConfig)
            {
                LoggerHelper.Info(
                    $"{LangKeys.MouseInput.ToLocalization()}:{win32MouseInputType},{LangKeys.KeyboardInput.ToLocalization()}:{win32KeyboardInputType},{LangKeys.AdbCaptureMode.ToLocalization()}{winScreenCapType}");
            }
        }
    }

    private void ConfigureMaaProcessorForPlayCover()
    {
        if (ViewModel?.CurrentController != MaaControllerTypes.PlayCover)
            return;

        var controller = Interface?.Controller?.FirstOrDefault(c =>
            c.Type?.Equals("playcover", StringComparison.OrdinalIgnoreCase) == true);

        if (!string.IsNullOrWhiteSpace(controller?.PlayCover?.Uuid))
        {
            Config.PlayCover.UUID = controller.PlayCover.Uuid;
        }
    }

    private Win32ScreencapMethod ConfigureWin32ScreenCapTypes()
    {
        return InstanceConfiguration.GetValue(ConfigurationKeys.Win32ControlScreenCapType,
            Win32ScreencapMethod.FramePool, Win32ScreencapMethod.None,
            new UniversalEnumConverter<Win32ScreencapMethod>());
    }

    private Win32InputMethod ConfigureWin32MouseInputTypes()
    {
        return InstanceConfiguration.GetValue(ConfigurationKeys.Win32ControlMouseType,
            Win32InputMethod.SendMessage, Win32InputMethod.None,
            new UniversalEnumConverter<Win32InputMethod>());
    }

    private Win32InputMethod ConfigureWin32KeyboardInputTypes()
    {
        return InstanceConfiguration.GetValue(ConfigurationKeys.Win32ControlKeyboardType,
            Win32InputMethod.SendMessage, Win32InputMethod.None,
            new UniversalEnumConverter<Win32InputMethod>());
    }
    private bool FirstTask = true;
    public const string NEW_SEPARATOR = "<|||>";
    public const string OLD_SEPARATOR = ":";

    private void LoadTasks(List<MaaInterface.MaaInterfaceTask> tasks, IList<DragItemViewModel>? oldDrags = null)
    {
        _taskLoader ??= new TaskLoader(Interface, ViewModel!);
        _taskLoader.LoadTasks(tasks, TasksSource, ref FirstTask, oldDrags);
    }

    private void ResetActionFailedCount()
    {
        _screencapFailedCount = 0;
    }

    public void ResetScreencapFailureLogFlags()
    {
        lock (_screencapLogLock)
        {
            _screencapAbortLogPending = false;
            _screencapDisconnectedLogPending = false;
            _screencapFailureLogged = false;
        }
    }

    public bool TryConsumeScreencapFailureLog(out bool shouldAbort, out bool shouldDisconnected)
    {
        lock (_screencapLogLock)
        {
            shouldAbort = _screencapAbortLogPending;
            shouldDisconnected = _screencapDisconnectedLogPending;
            _screencapAbortLogPending = false;
            _screencapDisconnectedLogPending = false;
            return shouldAbort || shouldDisconnected;
        }
    }

    public bool HandleScreencapStatus(MaaJobStatus status)
    {
        if (status == MaaJobStatus.Invalid || status == MaaJobStatus.Failed)
        {
            ++_screencapFailedCount;
        }

        if (status == MaaJobStatus.Succeeded)
        {
            _screencapFailedCount = 0;
        }

        return _screencapFailedCount >= ActionFailedLimit;
    }


    private string? _tempResourceVersion;

    public void AppendVersionLog(string? resourceVersion)
    {
        if (resourceVersion is null || _tempResourceVersion == resourceVersion)
        {
            return;
        }
        _tempResourceVersion = resourceVersion;
        var frameworkVersion = "";
        try
        {
            frameworkVersion = NativeBindingContext.LibraryVersion;
        }
        catch (Exception e)
        {
            frameworkVersion = "Unknown";
            LoggerHelper.Error("Failed to get MaaFramework version", e);
        }

        // Log all version information
        LoggerHelper.Info($"Resource version: {_tempResourceVersion}");
        LoggerHelper.Info($"MaaFramework version: {frameworkVersion}");
    }

    #endregion

    private void EnsureCommandThread()
    {
        if (_commandThread != null)
            return;

        lock (_commandThreadLock)
        {
            if (_commandThread != null)
                return;

            _commandThread = new Thread(CommandLoop)
            {
                IsBackground = true,
                Name = $"MaaProcessor-{InstanceId}-Command"
            };
            _commandThread.Start();
        }
    }

    private void CommandLoop()
    {
        try
        {
            foreach (var command in _commandQueue.GetConsumingEnumerable(_commandThreadCts.Token))
            {
                try
                {
                    command().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnqueueCommand(Func<Task> command)
    {
        if (_commandThreadCts.IsCancellationRequested || _commandQueue.IsAddingCompleted)
        {
            LoggerHelper.Info("Command queue stopped, ignore request.");
            return;
        }

        EnsureCommandThread();
        _commandQueue.Add(command);
    }

    private void StopCommandThread()
    {
        lock (_commandThreadLock)
        {
            if (_commandThread == null)
                return;

            try
            {
                _commandThreadCts.Cancel();
                _commandQueue.CompleteAdding();
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Stop command thread failed: {ex.Message}");
            }
        }
    }

    #region 开始任务

    private void MeasureExecutionTime(Action methodToMeasure)
    {
        var stopwatch = Stopwatch.StartNew();

        methodToMeasure();

        stopwatch.Stop();
        long elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        switch (elapsedMilliseconds)
        {
            case >= 800:
                AddLogByKey(LangKeys.ScreencapErrorTip, BrushHelper.ConvertToBrush("DarkGoldenrod"), false, true, elapsedMilliseconds.ToString(),
                    ScreenshotType());
                break;

            case >= 400:
                AddLogByKey(LangKeys.ScreencapWarningTip, BrushHelper.ConvertToBrush("DarkGoldenrod"), false, true, elapsedMilliseconds.ToString(),
                    ScreenshotType());
                break;

            default:
                AddLogByKey(LangKeys.ScreencapCost, (IBrush?)null, true, false, elapsedMilliseconds.ToString(),
                    ScreenshotType());
                break;
        }
    }

    private async Task MeasureExecutionTimeAsync(Func<Task> methodToMeasure)
    {
        const int sampleCount = 2;
        long totalElapsed = 0;

        long min = 10000;
        long max = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            var sw = Stopwatch.StartNew();
            await methodToMeasure();
            sw.Stop();
            min = Math.Min(min, sw.ElapsedMilliseconds);
            max = Math.Max(max, sw.ElapsedMilliseconds);
            totalElapsed += sw.ElapsedMilliseconds;
        }

        var avgElapsed = totalElapsed / sampleCount;

        switch (avgElapsed)
        {
            case >= 800:
                AddLogByKey(LangKeys.ScreencapErrorTip, BrushHelper.ConvertToBrush("DarkGoldenrod"), false, false, avgElapsed.ToString(),
                    ScreenshotType());
                break;

            case >= 400:
                AddLogByKey(LangKeys.ScreencapWarningTip, BrushHelper.ConvertToBrush("DarkGoldenrod"), false, false, avgElapsed.ToString(),
                    ScreenshotType());
                break;

            default:
                AddLogByKey(LangKeys.ScreencapCost, (IBrush?)null, true, false, avgElapsed.ToString(),
                    ScreenshotType());
                break;
        }
    }

    public async Task RestartAdb()
    {
        await ProcessHelper.RestartAdbAsync(Config.AdbDevice.AdbPath);
    }

    public async Task ReconnectByAdb()
    {
        await ProcessHelper.ReconnectByAdbAsync(Config.AdbDevice.AdbPath, Config.AdbDevice.AdbSerial);
    }

    public async Task HardRestartAdb()
    {
        ProcessHelper.HardRestartAdb(Config.AdbDevice.AdbPath);
    }
    
    public async Task TestConnecting()
    {
        if (Interlocked.CompareExchange(ref _isConnecting, 1, 0) != 0)
            return;
        try
        {
            await GetTaskerAsync();
            var task = MaaTasker?.Controller?.LinkStart();
            task?.Wait();
            ViewModel?.SetConnected(task?.Status == MaaJobStatus.Succeeded);
        }
        finally
        {
            Interlocked.Exchange(ref _isConnecting, 0);
        }
    }


    public async Task ReconnectAsync(CancellationToken token = default, bool showMessage = true)
    {
        await HandleDeviceConnectionAsync(token, showMessage);
    }

    public void Start(bool onlyStart = false, bool checkUpdate = false)
    {
        EnqueueCommand(() => StartInternal(null, onlyStart, checkUpdate));
    }

    public void Start(List<DragItemViewModel> dragItemViewModels, bool onlyStart = false, bool checkUpdate = false)
    {
        EnqueueCommand(() => StartInternal(dragItemViewModels, onlyStart, checkUpdate));
    }

    private Task StartInternal(List<DragItemViewModel>? dragItemViewModels, bool onlyStart, bool checkUpdate)
    {
        // 保存当前的任务列表，以便在重新加载时保留用户调整的顺序和 check 状态
        var currentTasks = new Collection<DragItemViewModel>(ViewModel?.TaskItemViewModels.ToList() ?? new List<DragItemViewModel>());

        if (InitializeData(currentTasks))
        {
            List<DragItemViewModel> tasks;
            if (dragItemViewModels == null)
            {
                // 排除不支持当前资源包/控制器的任务（IsTaskSupported 为 false 的任务）
                // 排除 resource option 项（它们不参与任务执行，只提供参数）
                tasks = ViewModel?.TaskItemViewModels.ToList()
                        .FindAll(task => (task.IsChecked || task.IsCheckedWithNull == null) && task.IsTaskSupported && !task.IsResourceOptionItem)
                    ?? new List<DragItemViewModel>();
            }
            else
            {
                tasks = dragItemViewModels;
            }

            _ = StartTask(tasks, onlyStart, checkUpdate);
        }

        return Task.CompletedTask;
    }

    public CancellationTokenSource? CancellationTokenSource
    {
        get;
        private set;
    } = new();
    private DateTime? _startTime;
    private List<DragItemViewModel> _tempTasks = [];

    public async Task StartTask(List<DragItemViewModel>? tasks, bool onlyStart = false, bool checkUpdate = false)
    {
        ResetActionFailedCount();
        Interlocked.Exchange(ref _stopCompletionMessageHandled, 0);
        Status = MFATask.MFATaskStatus.NOT_STARTED;
        CancellationTokenSource = new CancellationTokenSource();

        _startTime = DateTime.Now;

        var token = CancellationTokenSource.Token;

        if (!onlyStart)
        {
            tasks ??= new List<DragItemViewModel>();
            _tempTasks = tasks;
            var taskAndParams = tasks.Select(CreateNodeAndParam).ToList();
            InitializeConnectionTasksAsync(token);
            AddCoreTasksAsync(taskAndParams, token);
        }

        AddPostTasksAsync(onlyStart, checkUpdate, token);
        _taskQueueTotal = TaskQueue.Count;
        if (_taskQueueTotal > 0)
        {
            SetTaskbarProgress(0, _taskQueueTotal);
        }
        else
        {
            ClearTaskbarProgress();
        }

        await TaskManager.RunTaskAsync(async () =>
        {
            await ExecuteTasks(token);
            Stop(Status, true, onlyStart);
        }, token: token, name: "启动任务");

    }

    async private Task ExecuteTasks(CancellationToken token)
    {
        while (TaskQueue.Count > 0 && !token.IsCancellationRequested)
        {
            // 等待 modal 弹窗确认（display=modal 时任务队列暂停推进）
            while (_isWaitingForModal && !token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
            }
            if (token.IsCancellationRequested) break;

            var task = TaskQueue.Dequeue();
            var status = await task.Run(token);
            if (status != MFATask.MFATaskStatus.SUCCEEDED)
            {
                Status = status;
                return;
            }
        }
        if (Status == MFATask.MFATaskStatus.NOT_STARTED)
            Status = !token.IsCancellationRequested ? MFATask.MFATaskStatus.SUCCEEDED : MFATask.MFATaskStatus.STOPPED;
    }

    public class NodeAndParam
    {
        public string? Name { get; set; }
        public string? Entry { get; set; }
        public int? Count { get; set; }

        // public Dictionary<string, MaaNode>? Tasks
        // {
        //     get;
        //     set;
        // }
        public string? Param { get; set; }
    }

    private void UpdateTaskDictionary(ref MaaToken taskModels,
        List<MaaInterface.MaaInterfaceSelectOption>? options,
        List<MaaInterface.MaaInterfaceSelectAdvanced>? advanceds)
    {
        // Instance.NodeDictionary = Instance.NodeDictionary.MergeMaaNodes(taskModels);
        if (options != null)
        {
            ProcessOptions(ref taskModels, options);
        }

        if (advanceds != null)
        {
            foreach (var selectAdvanced in advanceds)
            {
                if (string.IsNullOrWhiteSpace(selectAdvanced.PipelineOverride) || selectAdvanced.PipelineOverride == "{}")
                {
                    if (Interface?.Advanced?.TryGetValue(selectAdvanced.Name ?? string.Empty, out var interfaceAdvanced) == true)
                    {
                        var inputValues = selectAdvanced.Data
                            .Where(kv => kv.Value != null)
                            .ToDictionary(kv => kv.Key, kv => kv.Value!);
                        selectAdvanced.PipelineOverride = interfaceAdvanced.GenerateProcessedPipeline(inputValues);
                    }
                }

                if (!string.IsNullOrWhiteSpace(selectAdvanced.PipelineOverride) && selectAdvanced.PipelineOverride != "{}")
                {
                    var param = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(selectAdvanced.PipelineOverride);
                    //       Instance.NodeDictionary = Instance.NodeDictionary.MergeMaaNodes(param);
                    taskModels.Merge(param);
                }
            }
        }
    }

    /// <summary>
    /// 处理 option 列表（支持 select/switch/input 类型及子配置项）
    /// </summary>
    /// <param name="taskModels">任务参数</param>
    /// <param name="allOptions">任务的所有 option 列表</param>
    /// <param name="optionNamesToProcess">要处理的 option 名称列表（null 表示处理所有直接引用的 options）</param>
    /// <param name="processedOptions">已处理的 option 名称（避免重复处理）</param>
    private void ProcessOptions(
        ref MaaToken taskModels,
        List<MaaInterface.MaaInterfaceSelectOption> allOptions,
        List<string>? optionNamesToProcess = null,
        HashSet<string>? processedOptions = null)
    {
        processedOptions ??= new HashSet<string>();

        // 确定要处理的 options
        IEnumerable<MaaInterface.MaaInterfaceSelectOption> optionsToProcess;

        if (optionNamesToProcess == null)
        {
            // 根级调用：处理所有 options（按顺序）
            optionsToProcess = allOptions;
        }
        else
        {
            // 递归调用：只处理指定名称的 options
            optionsToProcess = allOptions.Where(o => optionNamesToProcess.Contains(o.Name ?? string.Empty));
        }

        foreach (var selectOption in optionsToProcess)
        {
            var optionName = selectOption.Name ?? string.Empty;

            // 避免重复处理同一个 option
            if (processedOptions.Contains(optionName))
                continue;

            processedOptions.Add(optionName);

            if (Interface?.Option?.TryGetValue(optionName, out var interfaceOption) != true)
                continue;

            // 处理 checkbox 类型（多选，任务 10）
            if (interfaceOption.IsCheckbox && interfaceOption.Cases != null)
            {
                var selectedCases = selectOption.SelectedCases ?? new List<string>();
                // 按 cases 定义顺序合并所有选中 case 的 pipeline_override
                foreach (var caseItem in interfaceOption.Cases)
                {
                    if (caseItem.Name != null && selectedCases.Contains(caseItem.Name))
                    {
                        if (caseItem.PipelineOverride != null)
                        {
                            taskModels.Merge(caseItem.PipelineOverride);
                        }

                        // 递归处理被选中 case 的子配置项
                        if (caseItem.Option != null && caseItem.Option.Count > 0)
                        {
                            var unprocessedSubOptionNames = caseItem.Option
                                .Where(name => !processedOptions.Contains(name))
                                .ToList();

                            if (unprocessedSubOptionNames.Count > 0 && selectOption.SubOptions != null)
                            {
                                var subOptionsToProcess = selectOption.SubOptions
                                    .Where(s => unprocessedSubOptionNames.Contains(s.Name ?? string.Empty))
                                    .ToList();

                                ProcessOptions(ref taskModels, subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
                            }
                            else if (unprocessedSubOptionNames.Count > 0)
                            {
                                ProcessOptions(ref taskModels, allOptions, unprocessedSubOptionNames, processedOptions);
                            }
                        }
                    }
                }
            }
            // 处理 input 类型
            else if (interfaceOption.IsInput)
            {
                // 从 Data 重新生成 PipelineOverride（因为 PipelineOverride 是 JsonIgnore 的）
                string? pipelineOverride = selectOption.PipelineOverride;

                if ((selectOption.Data == null || selectOption.Data.Count == 0) && interfaceOption.Inputs is { Count: > 0 })
                {
                    selectOption.Data = interfaceOption.Inputs
                        .Where(input => !string.IsNullOrEmpty(input.Name))
                        .ToDictionary(input => input.Name!, input => input.Default ?? string.Empty);
                }

                if ((string.IsNullOrWhiteSpace(pipelineOverride) || pipelineOverride == "{}")
                    && selectOption.Data != null
                    && interfaceOption.PipelineOverride != null)
                {
                    // 从 Data 重新生成
                    pipelineOverride = interfaceOption.GenerateProcessedPipeline(
                        selectOption.Data.Where(kv => kv.Value != null)
                            .ToDictionary(kv => kv.Key, kv => kv.Value!));
                }

                if (!string.IsNullOrWhiteSpace(pipelineOverride) && pipelineOverride != "{}")
                {
                    var param = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(pipelineOverride);
                    taskModels.Merge(param);
                }
            }
            // 处理 select/switch 类型
            else if (selectOption.Index is int index
                     && interfaceOption.Cases is { } cases
                     && index >= 0
                     && index < cases.Count)
            {
                var selectedCase = cases[index];

                // 合并当前 case 的 pipeline_override
                if (selectedCase.PipelineOverride != null)
                {
                    taskModels.Merge(selectedCase.PipelineOverride);
                }

                // 只递归处理被选中 case 的子配置项（且未被处理过的）
                if (selectedCase.Option != null && selectedCase.Option.Count > 0)
                {
                    // 过滤掉已处理的
                    var unprocessedSubOptionNames = selectedCase.Option
                        .Where(name => !processedOptions.Contains(name))
                        .ToList();

                    if (unprocessedSubOptionNames.Count > 0 && selectOption.SubOptions != null)
                    {
                        // 从 selectOption.SubOptions 中获取子选项（已保存的用户选择值）
                        var subOptionsToProcess = selectOption.SubOptions
                            .Where(s => unprocessedSubOptionNames.Contains(s.Name ?? string.Empty))
                            .ToList();

                        ProcessOptions(ref taskModels, subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
                    }
                    else if (unprocessedSubOptionNames.Count > 0)
                    {
                        // 如果没有 SubOptions，使用默认值处理
                        ProcessOptions(ref taskModels, allOptions, unprocessedSubOptionNames, processedOptions);
                    }
                }
            }
        }
    }

    private string SerializeTaskParams(MaaToken taskModels)
    {
        // var settings = new JsonSerializerSettings
        // {
        //     Formatting = Formatting.Indented,
        //     NullValueHandling = NullValueHandling.Ignore,
        //     DefaultValueHandling = DefaultValueHandling.Ignore
        // };

        try
        {
            return taskModels.ToString();
            //     return JsonConvert.SerializeObject(taskModels.Tokens, settings);
        }
        catch (Exception)
        {
            return "{}";
        }
    }

    private NodeAndParam CreateNodeAndParam(DragItemViewModel task)
    {
        var taskModels = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(JsonConvert.SerializeObject(task.InterfaceItem?.PipelineOverride ?? new Dictionary<string, JToken>(), new JsonSerializerSettings()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        })).ToMaaToken();

        // PI v2.3.0 合并顺序：global_option < resource.option < controller.option < task.option
        // 1. 合并全局选项（global_option，最低优先级）
        MergeGlobalOptionParams(ref taskModels);

        // 2. 合并当前资源的全局选项参数（resource.option）
        MergeResourceOptionParams(ref taskModels);

        // 3. 合并当前控制器的选项参数（controller.option）
        MergeControllerOptionParams(ref taskModels);

        // 4. 合并任务自身的 option（task.option，最高优先级）
        UpdateTaskDictionary(ref taskModels, task.InterfaceItem?.Option, task.InterfaceItem?.Advanced);

        var taskParams = SerializeTaskParams(taskModels);
        // var settings = new JsonSerializerSettings
        // {
        //     Formatting = Formatting.Indented,
        //     NullValueHandling = NullValueHandling.Ignore,
        //     DefaultValueHandling = DefaultValueHandling.Ignore
        // };
        // var json = JsonConvert.SerializeObject(Instance.BaseNodes, settings);
        //
        // var tasks = JsonConvert.DeserializeObject<Dictionary<string, MaaNode>>(json, settings);
        // tasks = tasks.MergeMaaNodes(taskModels);
        LoggerHelper.Info(taskParams);
        return new NodeAndParam
        {
            Name = task.Name,
            Entry = task.InterfaceItem?.Entry,
            Count = task.InterfaceItem?.Repeatable == true ? (task.InterfaceItem?.RepeatCount ?? 1) : 1,
            // Tasks = tasks,
            Param = taskParams
        };
    }

    /// <summary>
    /// 合并全局选项参数（global_option，最低优先级）
    /// </summary>
    private void MergeGlobalOptionParams(ref MaaToken taskModels)
    {
        var globalSelectOptions = Interface?.GlobalSelectOptions;
        if (globalSelectOptions == null || globalSelectOptions.Count == 0)
            return;

        ProcessOptions(ref taskModels, globalSelectOptions);
    }

    /// <summary>
    /// 合并当前资源的全局选项参数到任务参数中（resource.option）
    /// </summary>
    private void MergeResourceOptionParams(ref MaaToken taskModels)
    {
        // 获取当前资源
        var currentResourceName = ViewModel?.CurrentResource;
        var currentResource = ViewModel?.CurrentResources
            .FirstOrDefault(r => r.Name == currentResourceName);

        if (currentResource?.SelectOptions == null || currentResource.SelectOptions.Count == 0)
            return;

        // 查找任务列表中的资源设置项，获取用户选择的值
        var resourceOptionItem = ViewModel?.TaskItemViewModels
            .FirstOrDefault(t => t.IsResourceOptionItem && t.ResourceItem?.Name == currentResourceName);

        var selectOptions = resourceOptionItem?.ResourceItem?.SelectOptions ?? currentResource.SelectOptions;

        // 处理资源的全局选项
        ProcessOptions(ref taskModels, selectOptions);
    }

    /// <summary>
    /// 合并当前控制器的选项参数（controller.option）
    /// </summary>
    private void MergeControllerOptionParams(ref MaaToken taskModels)
    {
        var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
        var controllerName = controllerType.ToJsonKey();
        var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
            c.Type != null && c.Type.Equals(controllerName, StringComparison.OrdinalIgnoreCase));

        var selectOptions = controllerConfig?.SelectOptions;
        if (selectOptions == null || selectOptions.Count == 0)
            return;

        ProcessOptions(ref taskModels, selectOptions);
    }

    private void InitializeConnectionTasksAsync(CancellationToken token)
    {
        TaskQueue.Enqueue(CreateMFATask("启动脚本", async () =>
        {
            await TaskManager.RunTaskAsync(async () => await RunScript(), token: token, name: "启动附加开始脚本");
        }));

        TaskQueue.Enqueue(CreateMFATask("连接设备", async () =>
        {
            await HandleDeviceConnectionAsync(token);
        }));

        TaskQueue.Enqueue(CreateMFATask("性能基准", async () =>
        {
            await MeasureScreencapPerformanceAsync(token);
        }));
    }

    public async Task MeasureScreencapPerformanceAsync(CancellationToken token)
    {
        await TaskManager.RunTaskAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            await MeasureExecutionTimeAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                MaaTasker?.Controller.Screencap().Wait();
            });
        }, token: token, name: "截图测试");
    }

    async private Task HandleDeviceConnectionAsync(CancellationToken token, bool showMessage = true)
    {
        if (Interlocked.CompareExchange(ref _isConnecting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (ViewModel?.IsConnected == true)
            {
                return;
            }

            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
            var isAdb = controllerType == MaaControllerTypes.Adb;
            var isPlayCover = controllerType == MaaControllerTypes.PlayCover;
            var targetKey = controllerType switch
            {
                MaaControllerTypes.Adb => LangKeys.Emulator,
                MaaControllerTypes.Win32 => LangKeys.Window,
                MaaControllerTypes.PlayCover => "TabPlayCover",
                _ => LangKeys.Window
            };
            var beforeTask = InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
            var delayFingerprintMatching = beforeTask.Contains("StartupSoftware", StringComparison.OrdinalIgnoreCase);

            if (showMessage)
                AddLogByKey(LangKeys.ConnectingTo, (IBrush?)null, true, true, targetKey);
            else
                ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.ConnectingTo.ToLocalizationFormatted(true, targetKey));

            if (!isPlayCover && ViewModel?.CurrentDevice == null && InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true) && !delayFingerprintMatching)
                ViewModel?.TryReadAdbDeviceFromConfig(false, true);

            var tuple = await TryConnectAsync(token);
            var connected = tuple.Item1;
            var shouldRetry = tuple.Item3;

            if (!connected && isAdb && !tuple.Item2 && shouldRetry)
            {
                connected = await HandleAdbConnectionAsync(token, showMessage);
            }
            else if (!connected && controllerType == MaaControllerTypes.Win32 && !tuple.Item2 && shouldRetry)
            {
                await RetryConnectionAsync(CancellationToken.None, showMessage, StartSoftware, LangKeys.TryToStartGame,
                    InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnectedWin32, false),
                    () =>
                    {
                        if (InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true))
                            ViewModel?.TryReadAdbDeviceFromConfig(false, true);
                    });
            }

            if (!connected)
            {
                if (!tuple.Item2 && shouldRetry)
                    HandleConnectionFailureAsync(controllerType, token);
                throw new Exception("Connection failed after all retries");
            }

            ViewModel?.SetConnected(true);
        }
        finally
        {
            Interlocked.Exchange(ref _isConnecting, 0);
        }
    }

    async private Task<bool> HandleAdbConnectionAsync(CancellationToken token, bool showMessage = true)
    {
        bool connected = false;
        var retrySteps = new List<Func<CancellationToken, Task<bool>>>
        {
            async t => await RetryConnectionAsync(t, showMessage, StartSoftware, LangKeys.TryToStartEmulator, InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnected, false),
                () =>
                {
                    if (InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true)) ViewModel?.TryReadAdbDeviceFromConfig(false, true);
                }),
            async t => await RetryConnectionAsync(t, showMessage, ReconnectByAdb, LangKeys.TryToReconnect),
            async t => await RetryConnectionAsync(t, showMessage, RestartAdb, LangKeys.RestartAdb, InstanceConfiguration.GetValue(ConfigurationKeys.AllowAdbRestart, true)),
            async t => await RetryConnectionAsync(t, showMessage, HardRestartAdb, LangKeys.HardRestartAdb, InstanceConfiguration.GetValue(ConfigurationKeys.AllowAdbHardRestart, true))
        };

        foreach (var step in retrySteps)
        {
            if (token.IsCancellationRequested) break;
            connected = await step(token);
            if (connected) break;
        }

        return connected;
    }

    async private Task<bool> RetryConnectionAsync(CancellationToken token, bool showMessage, Func<Task> action, string logKey, bool enable = true, Action? other = null)
    {
        if (!enable) return false;
        token.ThrowIfCancellationRequested();
        if (showMessage)
            AddLog(LangKeys.ConnectFailed.ToLocalization() + "\n" + logKey.ToLocalization(), (IBrush?)null);
        else
            ToastHelper.Info(LangKeys.ConnectFailed.ToLocalization(), logKey.ToLocalization());
        await action();
        if (token.IsCancellationRequested)
        {
            Stop(MFATask.MFATaskStatus.STOPPED);
            return false;
        }
        other?.Invoke();
        var tuple = await TryConnectAsync(token);
        // 如果不应该重试（Agent启动失败或资源加载失败），直接返回 false
        if (!tuple.Item3)
        {
            return false;
        }
        return tuple.Item1;
    }

    async private Task<(bool, bool, bool)> TryConnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tuple = await GetTaskerAndBoolAsync(token);
        return (tuple.Item1 is { IsInitialized: true }, tuple.Item2, tuple.Item3);
    }

    private void HandleConnectionFailureAsync(MaaControllerTypes controllerType, CancellationToken token)
    {
        // 如果 token 已取消，不需要再调用 Stop，因为已经在其他地方处理了
        if (token.IsCancellationRequested)
        {
            LoggerHelper.Info("HandleConnectionFailureAsync: token is already canceled, skipping Stop call");
            return;
        }
        AddLogByKey(LangKeys.ConnectFailed, (IBrush?)null);
        ViewModel?.SetConnected(false);
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => "TabPlayCover",
            _ => LangKeys.Window
        };
        ToastHelper.Warn(LangKeys.Warning_CannotConnect.ToLocalizationFormatted(true, targetKey));
        Stop(MFATask.MFATaskStatus.STOPPED);
    }


    private void AddCoreTasksAsync(List<NodeAndParam> taskAndParams, CancellationToken token)
    {
        foreach (var task in taskAndParams)
        {
            TaskQueue.Enqueue(CreateMaaFWTask(task.Name,
                async () =>
                {
                    token.ThrowIfCancellationRequested();
                    // if (task.Tasks != null)
                    //     NodeDictionary = task.Tasks;
                    await TryRunTasksAsync(MaaTasker, task.Entry, task.Param, token);
                }, task.Count ?? 1
            ));
        }
    }

    async private Task TryRunTasksAsync(MaaTasker? maa, string? task, string? param, CancellationToken token)
    {
        if (maa == null || task == null) return;

        var job = maa.AppendTask(task, param ?? "{}");
        await TaskManager.RunTaskAsync((Action)(() =>
        {
            if (InstanceConfiguration.GetValue(ConfigurationKeys.ContinueRunningWhenError, true))
                job.Wait();
            else
                job.Wait().ThrowIfNot(MaaJobStatus.Succeeded);
        }), token, (ex) => throw ex, name: "队列任务", catchException: true, shouldLog: false);
    }

    async private Task RunScript(string str = "Prescript")
    {
        await ScriptRunner.RunScriptAsync(str);
    }

    private void AddPostTasksAsync(bool onlyStart, bool checkUpdate, CancellationToken token)
    {
        if (!onlyStart)
        {
            TaskQueue.Enqueue(CreateMFATask("结束脚本", async () =>
            {
                await TaskManager.RunTaskAsync(async () => await RunScript("Post-script"), token: token, name: "启动附加结束脚本");
            }));
        }
        if (checkUpdate)
        {
            TaskQueue.Enqueue(CreateMFATask("检查更新", async () =>
            {
                VersionChecker.Check();
            }, isUpdateRelated: true));
        }
    }

    private MFATask CreateMaaFWTask(string? name, Func<Task> action, int count = 1)
    {
        return new MFATask
        {
            Name = name,
            Count = count,
            Type = MFATask.MFATaskType.MAAFW,
            Action = action,
            OwnerViewModel = ViewModel
        };
    }

    private MFATask CreateMFATask(string? name, Func<Task> action, bool isUpdateRelated = false)
    {
        return new MFATask
        {
            IsUpdateRelated = isUpdateRelated,
            Name = name,
            Type = MFATask.MFATaskType.MFA,
            Action = action,
            OwnerViewModel = ViewModel
        };
    }

    private static void SetTaskbarProgress(int completed, int total)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            TaskbarManager.Instance.SetProgressState(TaskbarProgressBarState.Normal);
            TaskbarManager.Instance.SetProgressValue(completed, total);
        }
        catch
        {
        }
    }

    private static void ClearTaskbarProgress()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            TaskbarManager.Instance.SetProgressState(TaskbarProgressBarState.NoProgress);
        }
        catch
        {
        }
    }

    #endregion

    #region 停止任务

    private readonly Lock _stopLock = new();

    public void Stop(MFATask.MFATaskStatus status, bool finished = false, bool onlyStart = false, Action? action = null)
    {
        EnqueueCommand(() => StopInternal(status, finished, onlyStart, action));
    }

    private Task StopInternal(MFATask.MFATaskStatus status, bool finished, bool onlyStart, Action? action)
    {
        ResetActionFailedCount();
        ClearTaskbarProgress();
        _taskQueueTotal = 0;

        lock (_stopLock)
        {
            LoggerHelper.Info("Stop Status: " + Status);
            if (Status == MFATask.MFATaskStatus.STOPPING)
                return Task.CompletedTask;
            Status = MFATask.MFATaskStatus.STOPPING;
            DispatcherHelper.PostOnMainThread(() =>
            {
                if (ViewModel != null) ViewModel.ToggleEnable = false;
            });
            try
            {
                var isUpdateRelated = TaskQueue.Any(task => task.IsUpdateRelated);
                ViewModel?.SetCurrentTaskName(string.Empty);
                if (!ShouldProcessStop(finished))
                {
                    ToastHelper.Warn(LangKeys.NoTaskToStop.ToLocalization());

                    TaskQueue.Clear();
                    return Task.CompletedTask;
                }

                CancelOperations(status == MFATask.MFATaskStatus.STOPPED && !_agentStarted && _agentContexts.Count > 0);

                TaskQueue.Clear();

                ExecuteStopCore(finished, async () =>
                {
                    var stopResult = MaaJobStatus.Succeeded;

                    if (MaaTasker is { IsRunning: true, IsStopping: false } && status != MFATask.MFATaskStatus.FAILED && status != MFATask.MFATaskStatus.SUCCEEDED)
                    {

                        // 持续尝试停止直到返回 Succeeded
                        const int maxRetries = 10;
                        const int retryDelayMs = 500;

                        for (int i = 0; i < maxRetries; i++)
                        {
                            LoggerHelper.Info($"Stopping tasker attempt {i + 1}");
                            stopResult = AbortCurrentTasker();
                            LoggerHelper.Info($"Stopping tasker attempt {i + 1} returned {stopResult}, retrying...");

                            if (stopResult == MaaJobStatus.Succeeded)
                                break;

                            await Task.Delay(retryDelayMs);
                        }

                    }
                    HandleStopResult(status, stopResult, onlyStart, action, isUpdateRelated);
                    DispatcherHelper.PostOnMainThread(() =>
                    {
                        if (ViewModel != null) ViewModel.ToggleEnable = true;
                    });
                });
            }
            catch (Exception ex)
            {
                DispatcherHelper.PostOnMainThread(() =>
                {
                    if (ViewModel != null) ViewModel.ToggleEnable = true;
                });
                HandleStopException(ex);
            }
        }

        return Task.CompletedTask;
    }


    private void CancelOperations(bool killAgent = false)
    {
        SetWaitingForModal(false);
        _emulatorCancellationTokenSource?.SafeCancel();
        CancellationTokenSource.SafeCancel();
        if (killAgent)
        {
            AgentHelper.KillAllAgents(_agentContexts);
            _agentContexts = [];
        }
    }

    private bool ShouldProcessStop(bool finished)
    {
        return CancellationTokenSource?.IsCancellationRequested == false
            || finished;
    }

    private void ExecuteStopCore(bool finished, Action stopAction)
    {
        TaskManager.RunTaskAsync(() =>
        {
            if (!finished) DispatcherHelper.PostOnMainThread(() => AddLogByKey(LangKeys.Stopping, (IBrush?)null));

            stopAction.Invoke();

            DispatcherHelper.PostOnMainThread(() => Instances.RootViewModel.Idle = true);
        }, null, "停止maafw任务");
    }

    private MaaJobStatus AbortCurrentTasker()
    {
        if (MaaTasker == null)
            return MaaJobStatus.Succeeded;
        var status = MaaTasker.Stop().Wait();

        return status;
    }

    private void HandleStopResult(MFATask.MFATaskStatus status, MaaJobStatus success, bool onlyStart, Action? action = null, bool isUpdateRelated = false)
    {
        if (success == MaaJobStatus.Succeeded)
        {
            DisplayTaskCompletionMessage(status, onlyStart, action);
        }
        else if (success == MaaJobStatus.Invalid)
        {
            AddLog(LangKeys.StoppingInternalTask.ToLocalization(), (IBrush?)null);
        }
        else
        {
            ToastHelper.Error(LangKeys.StoppingFailed.ToLocalization());
        }
        if (isUpdateRelated)
        {
            VersionChecker.Check();
        }
        _tempTasks = [];
    }

    private void DisplayTaskCompletionMessage(MFATask.MFATaskStatus status, bool onlyStart = false, Action? action = null)
    {
        if (Interlocked.Exchange(ref _stopCompletionMessageHandled, 1) == 1)
        {
            action?.Invoke();
            _startTime = null;
            return;
        }

        if (status == MFATask.MFATaskStatus.FAILED)
        {
            ToastHelper.Info(LangKeys.TaskFailed.ToLocalization());
            AddLogByKey(LangKeys.TaskFailed, (IBrush?)null);
            ExternalNotificationHelper.ExternalNotificationAsync(Instances.ExternalNotificationSettingsUserControlModel.EnabledCustom
                ? Instances.ExternalNotificationSettingsUserControlModel.CustomFailureText
                : LangKeys.TaskFailed.ToLocalization());
        }
        else if (status == MFATask.MFATaskStatus.STOPPED)
        {
            TaskManager.RunTask(() =>
            {
                Task.Delay(400).ContinueWith(_ =>
                {

                    ToastHelper.Info(LangKeys.TaskStopped.ToLocalization());
                    AddLogByKey(LangKeys.TaskAbandoned, (IBrush?)null);
                });
            });
        }
        else
        {
            if (!onlyStart)
            {
                var list = _tempTasks.Count > 0 ? _tempTasks : ViewModel?.TaskItemViewModels.ToList() ?? new List<DragItemViewModel>();
                list.Where(t => t.IsCheckedWithNull == null && !t.IsTaskSupported).ToList().ForEach(d => d.IsCheckedWithNull = false);

                if (_startTime != null)
                {
                    var elapsedTime = DateTime.Now - (DateTime)_startTime;
                    ToastNotification.Show(LangKeys.TaskCompleted.ToLocalization(), LangKeys.TaskAllCompletedWithTime.ToLocalizationFormatted(false, ((int)elapsedTime.TotalHours).ToString(),
                        ((int)elapsedTime.TotalMinutes % 60).ToString(), ((int)elapsedTime.TotalSeconds % 60).ToString()));
                }
                else
                {
                    ToastNotification.Show(LangKeys.TaskCompleted.ToLocalization());
                }
            }

            if (_startTime != null)
            {
                var elapsedTime = DateTime.Now - (DateTime)_startTime;
                AddLogByKey(LangKeys.TaskAllCompletedWithTime, (IBrush?)null, true, true, ((int)elapsedTime.TotalHours).ToString(),
                    ((int)elapsedTime.TotalMinutes % 60).ToString(), ((int)elapsedTime.TotalSeconds % 60).ToString());
            }
            else
            {
                AddLogByKey(LangKeys.TaskAllCompleted, (IBrush?)null);
            }
            if (!onlyStart)
            {
                ExternalNotificationHelper.ExternalNotificationAsync(Instances.ExternalNotificationSettingsUserControlModel.EnabledCustom
                    ? Instances.ExternalNotificationSettingsUserControlModel.CustomSuccessText
                    : LangKeys.TaskAllCompleted.ToLocalization());
                HandleAfterTaskOperation();
            }
        }
        action?.Invoke();
        _startTime = null;
    }

    public void HandleAfterTaskOperation()
    {
        var afterTask = InstanceConfiguration.GetValue(ConfigurationKeys.AfterTask, "None");
        switch (afterTask)
        {
            case "CloseMFA":
                Instances.ShutdownApplication();
                break;
            case "CloseEmulator":
                CloseSoftware(this);
                break;
            case "CloseEmulatorAndMFA":
                CloseSoftwareAndMFA(this);
                break;
            case "ShutDown":
                Instances.ShutdownSystem();
                break;
            case "CloseEmulatorAndRestartMFA":
                CloseSoftwareAndRestartMFA(this);
                break;
            case "RestartPC":
                Instances.RestartSystem();
                break;
        }
    }

    public static void CloseSoftwareAndRestartMFA(MaaProcessor? processor = null)
    {
        CloseSoftware(processor);
        Instances.RestartApplication();
    }

    public static void CloseSoftware(MaaProcessor? processor)
    {
        CloseSoftwareInternal(processor, null);
    }

    public static void CloseSoftware(Action? action = null)
    {
        CloseSoftwareInternal(null, action);
    }

    public static void CloseSoftware(MaaProcessor? processor, Action? action)
    {
        CloseSoftwareInternal(processor, action);
    }

    private static void CloseSoftwareInternal(MaaProcessor? processor, Action? action)
    {
        processor ??= MaaProcessorManager.Instance.Current;
        if (processor.ViewModel?.CurrentController == MaaControllerTypes.Adb)
        {
            EmulatorHelper.KillEmulatorModeSwitcher(processor);
        }
        else if (processor.ViewModel?.CurrentController == MaaControllerTypes.Win32)
        {
            if (OperatingSystem.IsWindows())
            {
                var hwnd = processor.Config.DesktopWindow.HWnd;
                var closedByHwnd = ProcessHelper.CloseProcessesByHWnd(hwnd);

                if (!closedByHwnd)
                {
                    if (_softwareProcess != null && !_softwareProcess.HasExited)
                    {
                        _softwareProcess.Kill();
                    }
                    else
                    {
                        ProcessHelper.CloseProcessesByName(processor.Config.DesktopWindow.Name, processor.InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty));
                        _softwareProcess = null;
                    }
                }
            }
        }
        processor.Stop(MFATask.MFATaskStatus.STOPPED);
        action?.Invoke();
    }

    public static void CloseSoftwareAndMFA(MaaProcessor? processor = null)
    {
        CloseSoftware(processor, Instances.ShutdownApplication);
    }

    private void HandleStopException(Exception ex)
    {
        LoggerHelper.Error($"Stop operation failed: {ex.Message}");
        ToastHelper.Error(LangKeys.StoppingFailed.ToLocalization());
    }

    #endregion

    #region 启动软件

    public async Task WaitSoftware()
    {
        if (InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None").Contains("Startup", StringComparison.OrdinalIgnoreCase))
        {
            await StartSoftware();
        }

        ViewModel?.TryReadAdbDeviceFromConfig(false);
    }
    private CancellationTokenSource? _emulatorCancellationTokenSource;
    private static Process? _softwareProcess;

    public async Task StartSoftware()
    {
        _emulatorCancellationTokenSource = new CancellationTokenSource();
        await StartRunnableFile(InstanceConfiguration.GetValue(ConfigurationKeys.SoftwarePath, string.Empty),
            InstanceConfiguration.GetValue(ConfigurationKeys.WaitSoftwareTime, 60.0), _emulatorCancellationTokenSource.Token);
    }

    async private Task StartRunnableFile(string exePath, double waitTimeInSeconds, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return;

        if (OperatingSystem.IsWindows() && exePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveShortcut(exePath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                LoggerHelper.Info($"Resolve shortcut: {exePath} -> {resolved}");
                exePath = resolved;
            }
        }

        var processName = Path.GetFileNameWithoutExtension(exePath);

        // 检查当前控制器是否需要管理员权限
        var requiresAdmin = ShouldStartWithAdminPrivileges();

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        // 如果需要管理员权限且当前不是管理员，使用 runas 启动
        if (requiresAdmin && OperatingSystem.IsWindows() && !AdminHelper.IsRunningAsAdministrator())
        {
            startInfo.Verb = "runas";
            LoggerHelper.Info("以管理员权限启动软件");
        }

        if (Process.GetProcessesByName(processName).Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty)))
            {
                startInfo.Arguments = InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
                _softwareProcess =
                    Process.Start(startInfo);
            }
            else
                _softwareProcess = Process.Start(startInfo);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty)))
            {
                startInfo.Arguments = InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
                _softwareProcess = Process.Start(startInfo);
            }
            else
                _softwareProcess = Process.Start(startInfo);
        }

        for (double remainingTime = waitTimeInSeconds + 1; remainingTime > 0; remainingTime -= 1)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (remainingTime % 10 == 0)
            {
                AddLogByKey(LangKeys.WaitSoftwareTime, (IBrush?)null, true, true,
                    ((ViewModel?.CurrentController ?? MaaControllerTypes.Adb) == MaaControllerTypes.Adb)
                        ? LangKeys.Emulator
                        : LangKeys.Window,
                    remainingTime.ToString()
                );
            }
            else if (remainingTime.Equals(waitTimeInSeconds))
            {
                AddLogByKey(LangKeys.WaitSoftwareTime, (IBrush?)null, true, true,
                    ((ViewModel?.CurrentController ?? MaaControllerTypes.Adb) == MaaControllerTypes.Adb)
                        ? LangKeys.Emulator
                        : LangKeys.Window,
                    remainingTime.ToString()
                );
            }


            await Task.Delay(1000, token);
        }

    }

    [SupportedOSPlatform("windows")]
    private static string? ResolveShortcut(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type);
                dynamic shortcut = shell.CreateShortcut(path);
                return shortcut.TargetPath;
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"Resolve shortcut failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 检查当前控制器是否需要以管理员权限启动软件
    /// </summary>
    private bool ShouldStartWithAdminPrivileges()
    {
        var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
        if (controllerType != MaaControllerTypes.Win32 && controllerType != MaaControllerTypes.Gamepad)
            return false;

        var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
            c.Type != null && c.Type.Equals(controllerType.ToJsonKey(), StringComparison.OrdinalIgnoreCase));

        return controllerConfig?.PermissionRequired == true;
    }

    /// <summary>
    /// 检查当前进程是否满足控制器的管理员权限要求
    /// 如果配置了 permission_required，MaaFW 需要以管理员权限运行，
    /// 由于 UI 和 FW 在同一进程，即当前进程必须以管理员身份运行
    /// </summary>
    private bool CheckTargetProcessAdminPermission()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
        if (controllerType != MaaControllerTypes.Win32 && controllerType != MaaControllerTypes.Gamepad)
            return true;

        var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
            c.Type != null && c.Type.Equals(controllerType.ToJsonKey(), StringComparison.OrdinalIgnoreCase));

        // 如果配置了 permission_required，当前进程（承载 MaaFW）必须以管理员身份运行
        if (controllerConfig?.PermissionRequired == true)
        {
            if (!AdminHelper.IsRunningAsAdministrator())
            {
                LoggerHelper.Warning("控制器配置了 permission_required，但当前进程未以管理员身份运行");
                DispatcherHelper.RunOnMainThread(() =>
                {
                    Instances.DialogManager.CreateDialog()
                        .OfType(NotificationType.Error)
                        .WithContent(LangKeys.AdminPermissionRequiredDetail.ToLocalization())
                        .WithActionButton(LangKeys.Restart.ToLocalization(), _ =>
                        {
                            if (AdminHelper.RestartAsAdministrator())
                                Instances.ShutdownApplication();
                        }, true)
                        .WithActionButton(LangKeys.ButtonCancel.ToLocalization(), _ => { }, true, "Outline")
                        .TryShow();
                });
                return false;
            }
        }

        return true;
    }

    #endregion

    #region 自定义识别和动作注册

    /// <summary>
    /// 注册自定义识别器和动作
    /// </summary>
    /// <param name="tasker">MaaTasker 实例</param>
    private void RegisterCustomRecognitionsAndActions(MaaTasker tasker)
    {
        if (Interface == null) return;

        try
        {
            // 注册内置特殊任务 Action
            tasker.Resource.Register(new Custom.CountdownAction());
            tasker.Resource.Register(new Custom.TimedWaitAction());
            tasker.Resource.Register(new Custom.SystemNotificationAction());
            tasker.Resource.Register(new Custom.CustomProgramAction());
            tasker.Resource.Register(new Custom.KillProcessAction());
            tasker.Resource.Register(new Custom.ComputerOperationAction());
            tasker.Resource.Register(new Custom.WebhookAction());
            LoggerHelper.Info("Registered built-in special task actions");

            // 获取当前资源的自定义目录
            var currentResource = ViewModel?.CurrentResources
                .FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            var originalPaths = currentResource?.ResolvedPath ?? currentResource?.Path;

            if (originalPaths == null || originalPaths.Count == 0)
            {
                LoggerHelper.Info("No resource paths found, skipping custom class loading");
                return;
            }

            // 创建副本，避免修改原始列表
            var resourcePaths = new List<string>(originalPaths);
            // LoggerHelper.Info(LangKeys.RegisteringCustomRecognizer.ToLocalization());
            // LoggerHelper.Info(LangKeys.RegisteringCustomAction.ToLocalization());
            resourcePaths.Add(Path.Combine(AppContext.BaseDirectory, "resource"));
            // 遍历所有资源路径，查找 custom 目录
            foreach (var resourcePath in resourcePaths)
            {
                var customDir = Path.Combine(resourcePath, "custom");
                if (!Directory.Exists(customDir))
                {
                    LoggerHelper.Info($"Custom directory not found: {customDir}");
                    continue;
                }

                var customClasses = CustomClassLoader.GetCustomClasses(customDir, new[]
                {
                    nameof(IMaaCustomRecognition),
                    nameof(IMaaCustomAction)
                });

                foreach (var customClass in customClasses)
                {
                    try
                    {
                        if (customClass.Value is IMaaCustomRecognition recognition)
                        {
                            tasker.Resource.Register(recognition);
                            LoggerHelper.Info($"Registered IMaaCustomRecognition: {customClass.Name}");
                        }
                        else if (customClass.Value is IMaaCustomAction action)
                        {
                            tasker.Resource.Register(action);
                            LoggerHelper.Info($"Registered IMaaCustomAction: {customClass.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"Failed to register custom class {customClass.Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"Error during custom recognition/action registration: {ex.Message}");
        }
    }

    #endregion
}
