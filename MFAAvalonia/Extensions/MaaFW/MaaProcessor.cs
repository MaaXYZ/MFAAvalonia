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

    public const string ConnectionFailedAfterAllRetriesMessage = "Connection failed after all retries";

    private static readonly Random Random = new();
    private int _taskQueueTotal;
    private int _isTaskRunActive;
    private readonly BlockingCollection<Func<Task>> _commandQueue = new();
    private readonly object _commandThreadLock = new();
    private readonly CancellationTokenSource _commandThreadCts = new();
    private Thread? _commandThread;
    public static string Resource => AppPaths.ResourceDirectory;
    public static string ResourceBase => Path.Combine(Resource, "base");
    public static ObservableCollection<MaaProcessor> Processors { get; } = new();
    // Android processes start with "/" as Environment.CurrentDirectory. Passing the
    // application data root explicitly prevents MaaToolkit from probing /config/maa_option.json,
    // which throws an uncaught native std::filesystem exception and aborts the process.
    public static MaaToolkit Toolkit { get; } = new(true, AppPaths.DataRoot);
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
        PlatformRunProgress.ClearLogs?.Invoke(InstanceId);
    }

    private void PublishPlatformLog(LogItemViewModel log)
    {
        static uint ToArgb(IBrush? brush, uint fallback)
        {
            if (brush is not ISolidColorBrush solid)
                return fallback;
            var color = solid.Color;
            return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        }

        PlatformRunProgress.Log?.Invoke(new RunLogEntry(
            InstanceId,
            log.Time,
            log.Content,
            log.UseMarkdown,
            log.ShowTime,
            ToArgb(log.Color, 0xffffffff),
            ToArgb(log.BackgroundColor, 0x00000000)));
    }

    private IDisposable BeginInstanceLogScope(string operation, string source = "Runtime")
    {
        return LoggerHelper.PushContext(
            source: source,
            operation: operation,
            instanceId: InstanceId,
            instanceName: MaaProcessorManager.Instance.GetInstanceName(InstanceId));
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
        bool showTime = true,
        bool useMarkdown = false)
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
            var log = new LogItemViewModel(content, brush, weight, "HH':'mm':'ss",
                showTime: showTime, changeColor: changeColor)
            {
                BackgroundColor = backGroundBrush,
                UseMarkdown = useMarkdown
            };
            LogItemViewModels.Add(log);
            PublishPlatformLog(log);
            using var logScope = BeginInstanceLogScope("MonitorLog", "Monitor");
            LoggerHelper.Info($"[Record] {content}");

            TrimExcessLogs();
        });
    }

    public void AddLog(string content,
        string color = "",
        string weight = "Regular",
        bool changeColor = true,
        bool showTime = true,
        bool useMarkdown = false)
    {
        var brush = BrushHelper.ConvertToBrush(color, Brushes.Black);
        AddLog(content, brush, weight, changeColor, showTime, useMarkdown);
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
                PublishPlatformLog(log);
                using var logScope = BeginInstanceLogScope("MonitorLog", "Monitor");
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
                PublishPlatformLog(log);
                using var logScope = BeginInstanceLogScope("MonitorMarkdown", "Monitor");
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

    private static Dictionary<string, JToken> DeserializePipelineOverrideJsonc(string json)
    {
        var token = JToken.Parse(json, JsoncLoadSettings);
        return token.ToObject<Dictionary<string, JToken>>() ?? new Dictionary<string, JToken>();
    }

    /// <summary>
    /// 获取 interface 文件路径，优先返回 .jsonc，其次 .json
    /// </summary>
    public static string? GetInterfaceFilePath()
    {
        var jsoncPath = AppPaths.InterfaceJsoncPath;
        if (File.Exists(jsoncPath))
            return jsoncPath;

        var jsonPath = AppPaths.InterfaceJsonPath;
        if (File.Exists(jsonPath))
            return jsonPath;

        return null;
    }

    private static readonly object InterfacePreloadLock = new();
    private static Task<MaaInterface>? _interfacePreloadTask;

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

        // 这里只读取预加载数据的协议版本，不应用 Interface。
        // MaaProcessorManager 的 Lazy 构造完成后，LoadInstanceConfig 才会在主线程应用完整数据。
        IsV3 = GetPreloadedInterfaceVersion() >= 3;
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
        DetachScreenshotTasker(requireStopped: true);
        _detachedScreenshotCleanupCancellationTokenSource?.Cancel();
        _detachedScreenshotCleanupCancellationTokenSource?.Dispose();
        lock (_detachedScreenshotTaskersLock)
        {
            _detachedScreenshotTaskers.Clear();
        }
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
                var paths = MaaInterface.ReplacePlaceholder(customResource.Path ?? new(), AppPaths.DataRoot);
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
                if (Instances.IsResolved<SettingsViewModel>())
                {
                    Instances.SettingsViewModel.ApplyInterfaceMetadata(value);
                }

                // 加载多语言配置
                if (value.Languages is { Count: > 0 })
                {
                    LanguageHelper.LoadLanguagesFromInterface(value.Languages, AppPaths.DataRoot);
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
        var projectDir = AppPaths.DataRoot;

        if (!Instances.IsResolved<SettingsViewModel>())
        {
            return;
        }

        var settingsViewModel = Instances.SettingsViewModel;

        // 加载 Description
        if (!string.IsNullOrWhiteSpace(maaInterface.Description))
        {
            var description = await maaInterface.Description.ResolveContentAsync(projectDir);
            settingsViewModel.ResourceDescription = description;
            settingsViewModel.HasResourceDescription = !string.IsNullOrWhiteSpace(description);
        }
        else
        {
            settingsViewModel.ResourceDescription = string.Empty;
            settingsViewModel.HasResourceDescription = false;
        }

        // 加载 Contact
        if (!string.IsNullOrWhiteSpace(maaInterface.Contact))
        {
            var contact = await maaInterface.Contact.ResolveContentAsync(projectDir);
            settingsViewModel.ResourceContact = contact;
            settingsViewModel.HasResourceContact = !string.IsNullOrWhiteSpace(contact);
        }
        else
        {
            settingsViewModel.ResourceContact = string.Empty;
            settingsViewModel.HasResourceContact = false;
        }

        // 加载 License
        if (!string.IsNullOrWhiteSpace(maaInterface.License))
        {
            var license = await maaInterface.License.ResolveContentAsync(projectDir);
            settingsViewModel.ResourceLicense = license;
            settingsViewModel.HasResourceLicense = !string.IsNullOrWhiteSpace(license);
        }
        else
        {
            settingsViewModel.ResourceLicense = string.Empty;
            settingsViewModel.HasResourceLicense = false;
        }
    }

    public MaaTasker? MaaTasker { get; set; }
    private MaaTasker? _screenshotTasker;
    private Task<MaaTasker?>? _screenshotTaskerInitTask;
    private long _screenshotTaskerGeneration;
    private long _screenshotTaskerInitGeneration;
    private readonly Lock _screenshotTaskerInitLock = new();

    private sealed class DetachedScreenshotTaskerRecord
    {
        public MaaTasker Tasker { get; init; } = null!;
        public DateTime DetachedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime NextCleanupAttemptUtc { get; set; } = DateTime.UtcNow.AddSeconds(15);
        public int Attempts { get; set; }
    }

    private readonly List<DetachedScreenshotTaskerRecord> _detachedScreenshotTaskers = [];
    private readonly Lock _detachedScreenshotTaskersLock = new();
    private CancellationTokenSource? _detachedScreenshotCleanupCancellationTokenSource;
    private Task? _detachedScreenshotCleanupTask;
    public MaaTasker? ScreenshotTasker => _screenshotTasker;
    public void SetTasker(MaaTasker? maaTasker = null, bool requireAllStopped = false)
    {
        ResetActionFailedCount();
        if (maaTasker == null)
        {
            var oldTasker = MaaTasker;
            MaaTasker = null; // 先设置为 null，防止重复释放
            var taskerStopped = true;

            if (oldTasker != null)
            {
                try
                {
                    // 使用超时机制避免无限等待，最多等待 5 秒
                    var stopTask = Task.Run(() => oldTasker.Stop().Wait());
                    if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
                    {
                        taskerStopped = false;
                        LoggerHelper.Warning("停止 MaaTasker 超时：已等待 5 秒。");
                    }
                    else if (stopTask.IsFaulted)
                    {
                        taskerStopped = false;
                        LoggerHelper.Warning($"停止 MaaTasker 内部任务失败：{stopTask.Exception?.GetBaseException().Message}");
                    }
                }
                catch (Exception e)
                {
                    taskerStopped = false;
                    LoggerHelper.Warning($"停止 MaaTasker 失败：{e.Message}");
                }
            }

            _agentStarted = false;
            // Agent contexts can outlive MaaTasker after an earlier partial cleanup. Always
            // terminate them, even when MaaTasker is already null.
            var agentsStopped = AgentHelper.KillAllAgents(_agentContexts, oldTasker);
            ViewModel?.SetConnected(false);
            var screenshotTaskerStopped = DetachScreenshotTasker(requireAllStopped);

            if (requireAllStopped && (!taskerStopped || !agentsStopped || !screenshotTaskerStopped))
                throw new InvalidOperationException("更新前未能停止所有 MaaTasker、截图 Tasker 或 Agent 进程。");
        }
        else if (maaTasker != null)
        {
            MaaTasker = maaTasker;
            DetachScreenshotTasker();
            ResetScreencapFailureLogFlags();
        }
    }

    private bool DetachScreenshotTasker(bool requireStopped = false)
    {
        MaaTasker? screenshotTasker;
        Task<MaaTasker?>? initTask;
        lock (_screenshotTaskerInitLock)
        {
            _screenshotTaskerGeneration++;
            screenshotTasker = _screenshotTasker;
            _screenshotTasker = null;
            initTask = _screenshotTaskerInitTask;
            _screenshotTaskerInitTask = null;

            // Publish the new generation and drop the old job under the same job
            // lock used by PostScreencapPipelined to prevent republishing a stale job.
            lock (_liveViewScreencapJobLock)
            {
                _liveViewScreencapJob = null;
            }
        }

        if (!requireStopped)
        {
            QueueDetachedScreenshotTasker(screenshotTasker);
            return true;
        }

        var taskersToStop = new List<MaaTasker>();
        AddUniqueScreenshotTasker(taskersToStop, screenshotTasker);

        List<DetachedScreenshotTaskerRecord> detachedSnapshot;
        lock (_detachedScreenshotTaskersLock)
        {
            detachedSnapshot = _detachedScreenshotTaskers.ToList();
            _detachedScreenshotTaskers.Clear();
        }
        foreach (var record in detachedSnapshot)
            AddUniqueScreenshotTasker(taskersToStop, record.Tasker);

        var allStopped = true;
        if (initTask != null)
        {
            try
            {
                if (!initTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    allStopped = false;
                    LoggerHelper.Warning("等待截图 Tasker 初始化结束超时：已等待 5 秒。");
                }
                else if (initTask.Status == TaskStatus.RanToCompletion)
                {
                    AddUniqueScreenshotTasker(taskersToStop, initTask.Result);
                }
                else if (initTask.IsFaulted)
                {
                    LoggerHelper.Warning($"截图 Tasker 初始化失败：{initTask.Exception?.GetBaseException().Message}");
                }
            }
            catch (Exception ex)
            {
                allStopped = false;
                LoggerHelper.Warning($"等待截图 Tasker 初始化结束失败：{ex.Message}");
            }
        }

        foreach (var tasker in taskersToStop)
        {
            if (StopAndDisposeScreenshotTasker(tasker)) continue;
            allStopped = false;
            QueueDetachedScreenshotTasker(tasker);
        }

        return allStopped;
    }

    private static void AddUniqueScreenshotTasker(List<MaaTasker> taskers, MaaTasker? tasker)
    {
        if (tasker != null && taskers.All(existing => !ReferenceEquals(existing, tasker)))
            taskers.Add(tasker);
    }

    private void QueueDetachedScreenshotTasker(MaaTasker? screenshotTasker)
    {
        if (screenshotTasker == null)
            return;

        lock (_detachedScreenshotTaskersLock)
        {
            if (_detachedScreenshotTaskers.Any(record => ReferenceEquals(record.Tasker, screenshotTasker)))
                return;
            _detachedScreenshotTaskers.Add(new DetachedScreenshotTaskerRecord
            {
                Tasker = screenshotTasker
            });
        }
        EnsureDetachedScreenshotTaskerCleanupLoop();
        LoggerHelper.Warning("主连接上下文已变化，旧截图任务执行器已脱钩，等待按新连接重建。");
    }

    private bool StopAndDisposeScreenshotTasker(MaaTasker screenshotTasker)
    {
        try
        {
            var stopTask = Task.Run(() =>
            {
                if (screenshotTasker.IsStopping)
                    throw new InvalidOperationException("截图 Tasker 仍处于停止过程中。");
                if (screenshotTasker.IsRunning && !screenshotTasker.IsStopping)
                    screenshotTasker.Stop().Wait();
            });
            if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
            {
                LoggerHelper.Warning("停止截图 Tasker 超时：已等待 5 秒。");
                return false;
            }
            if (stopTask.IsFaulted)
            {
                LoggerHelper.Warning($"停止截图 Tasker 失败：{stopTask.Exception?.GetBaseException().Message}");
                return false;
            }

            screenshotTasker.Dispose();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"停止或释放截图 Tasker 失败：{ex.Message}");
            return false;
        }
    }

    private void EnsureDetachedScreenshotTaskerCleanupLoop()
    {
        lock (_detachedScreenshotTaskersLock)
        {
            if (_detachedScreenshotCleanupTask is { IsCompleted: false })
                return;

            _detachedScreenshotCleanupCancellationTokenSource?.Cancel();
            _detachedScreenshotCleanupCancellationTokenSource?.Dispose();
            _detachedScreenshotCleanupCancellationTokenSource = new CancellationTokenSource();
            var token = _detachedScreenshotCleanupCancellationTokenSource.Token;
            _detachedScreenshotCleanupTask = Task.Run(() => CleanupDetachedScreenshotTaskersLoopAsync(token), token);
        }
    }

    private async Task CleanupDetachedScreenshotTaskersLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            List<DetachedScreenshotTaskerRecord> snapshot;
            lock (_detachedScreenshotTaskersLock)
            {
                snapshot = _detachedScreenshotTaskers.ToList();
            }

            if (snapshot.Count == 0)
                return;

            var nowUtc = DateTime.UtcNow;
            foreach (var record in snapshot)
            {
                token.ThrowIfCancellationRequested();
                if (record.NextCleanupAttemptUtc > nowUtc)
                    continue;

                if (TryDisposeDetachedScreenshotTasker(record))
                {
                    lock (_detachedScreenshotTaskersLock)
                    {
                        _detachedScreenshotTaskers.Remove(record);
                    }
                }
                else
                {
                    record.Attempts++;
                    var delaySeconds = Math.Min(60, 15 + record.Attempts * 10);
                    record.NextCleanupAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
    }

    private bool TryDisposeDetachedScreenshotTasker(DetachedScreenshotTaskerRecord record)
    {
        var screenshotTasker = record.Tasker;

        try
        {
            if (screenshotTasker.IsStopping)
                return false;

            if (screenshotTasker.IsRunning)
            {
                LoggerHelper.Info("延迟回收旧截图任务执行器：检测到仍在运行，稍后重试。");
                return false;
            }
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查旧截图任务执行器状态失败：{ex.Message}");
            return false;
        }

        try
        {
            screenshotTasker.Dispose();
            LoggerHelper.Info("延迟回收旧截图任务执行器成功。");
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"延迟回收旧截图任务执行器失败：{ex.Message}");
            return false;
        }
    }

    public MaaTasker? GetTasker(CancellationToken token = default)
    {
        var task = GetTaskerAsync(token);
        task.Wait(token);
        return task.Result;
    }

    private bool HasReusableMainTasker()
    {
        var tasker = MaaTasker;
        if (tasker == null)
            return false;

        try
        {
            var controller = tasker.Controller;
            if (controller == null)
                return false;

            if (tasker.IsRunning || tasker.IsStopping)
                return true;

            return controller.IsConnected;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查主任务执行器状态失败：{ex.Message}");
            return false;
        }
    }

    public async Task<MaaTasker?> GetTaskerAsync(CancellationToken token = default)
    {
        if (MaaTasker != null && !HasReusableMainTasker())
        {
            LoggerHelper.Warning("检测到主任务执行器已断连或不可用，准备重新初始化。");
            SetTasker();
        }

        if (MaaTasker == null)
        {
            var initializedTasker = (await InitializeMaaTasker(token)).Item1;
            if (initializedTasker != null)
            {
                MaaTasker = initializedTasker;
                // Ensure screenshot tasker is recreated from this processor's latest connection context.
                DetachScreenshotTasker();
                ResetScreencapFailureLogFlags();
                await PrewarmScreenshotTaskerAsync(token);
            }
        }
        return MaaTasker;
    }

    public async Task<(MaaTasker?, bool, bool)> GetTaskerAndBoolAsync(CancellationToken token = default)
    {
        if (MaaTasker != null && !HasReusableMainTasker())
        {
            LoggerHelper.Warning("检测到主任务执行器已断连或不可用，连接前将重新创建。");
            SetTasker();
        }

        var tuple = MaaTasker != null ? (MaaTasker, false, true) : await InitializeMaaTasker(token);
        if (MaaTasker == null && tuple.Item1 != null)
        {
            MaaTasker = tuple.Item1;
            // Ensure screenshot tasker is recreated from this processor's latest connection context.
            DetachScreenshotTasker();
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
        long initGeneration;
        lock (_screenshotTaskerInitLock)
        {
            if (_screenshotTaskerInitTask == null)
            {
                _screenshotTaskerInitGeneration = _screenshotTaskerGeneration;
                _screenshotTaskerInitTask = InitializeScreenshotTaskerAsync(token);
            }
            initTask = _screenshotTaskerInitTask;
            initGeneration = _screenshotTaskerInitGeneration;
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
            LoggerHelper.Warning($"截图任务执行器预热失败：{ex.Message}");
        }

        PublishInitializedScreenshotTasker(initTask, tasker, initGeneration);
    }

    private bool UseSeparateScreenshotTasker =>
        !PlatformControllerFactory.CanInitializeWithoutDevice
        && InstanceConfiguration.GetValue(ConfigurationKeys.UseSeparateScreenshotTasker, true);

    private MaaTasker? GetScreenshotTasker(CancellationToken token = default)
    {
        if (!UseSeparateScreenshotTasker)
        {
            // Detaching increments the generation and clears the in-flight job.
            // Doing it on every shared-controller lookup makes
            // PostScreencapPipelined discard every frame as stale.
            if (_screenshotTasker != null || _screenshotTaskerInitTask != null)
                DisposeScreenshotTasker();
            return MaaTasker;
        }

        if (ShouldRecreateScreenshotTasker())
        {
            DisposeScreenshotTasker();
        }

        if (_screenshotTasker == null && !_isClosed)
        {
            Task<MaaTasker?> initTask;
            long initGeneration;
            lock (_screenshotTaskerInitLock)
            {
                if (_screenshotTaskerInitTask == null)
                {
                    _screenshotTaskerInitGeneration = _screenshotTaskerGeneration;
                    _screenshotTaskerInitTask = InitializeScreenshotTaskerAsync(token);
                }
                initTask = _screenshotTaskerInitTask;
                initGeneration = _screenshotTaskerInitGeneration;
            }

            initTask.Wait(token);
            var tasker = initTask.Result;

            PublishInitializedScreenshotTasker(initTask, tasker, initGeneration);
        }

        return _screenshotTasker;
    }

    private void PublishInitializedScreenshotTasker(
        Task<MaaTasker?> initTask,
        MaaTasker? tasker,
        long initGeneration)
    {
        var accepted = false;
        lock (_screenshotTaskerInitLock)
        {
            if (ReferenceEquals(_screenshotTaskerInitTask, initTask))
                _screenshotTaskerInitTask = null;

            if (tasker != null
                && initGeneration == _screenshotTaskerGeneration
                && MaaTasker != null
                && !_isClosed)
            {
                _screenshotTasker ??= tasker;
                accepted = ReferenceEquals(_screenshotTasker, tasker);
            }
        }

        if (!accepted)
            QueueDetachedScreenshotTasker(tasker);
    }

    private bool ShouldRecreateScreenshotTasker()
    {
        var screenshotTasker = _screenshotTasker;
        if (screenshotTasker == null)
            return false;

        try
        {
            var controller = screenshotTasker.Controller;
            if (controller == null)
                return true;

            // 主任务仍然在线时，独立截图 tasker 断连说明实时视图链路已经单独失效，需要重建。
            if (MaaTasker?.Controller?.IsConnected == true && !controller.IsConnected)
                return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查截图任务执行器状态失败：{ex.Message}");
            return true;
        }

        return false;
    }

    private void DisposeScreenshotTasker()
    {
        DetachScreenshotTasker(requireStopped: true);
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
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private bool _suppressConnectionAttemptErrorToast;
    public bool IsConnecting => _isConnecting != 0;
    public bool IsTaskRunActive => Volatile.Read(ref _isTaskRunActive) != 0;

    private MaaController? GetScreenshotController(bool test)
    {
        if (test && !_isClosed)
            _ = TryConnectForLiveViewAsync();

        return GetScreenshotTasker(CancellationToken.None)?.Controller;
    }

    private async Task TryConnectForLiveViewAsync()
    {
        try
        {
            await TryConnectAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Live view connection probes are best-effort and may be cancelled on shutdown.
        }
        catch (Exception ex)
        {
            // A live-view refresh must never create an unobserved background exception.
            LoggerHelper.Debug($"实时画面后台连接探测失败：{ex.Message}");
        }
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

        if (controller == null)
            return MaaJobStatus.Invalid;

        if (!controller.IsConnected)
        {
            if (IsAnyScreenshotRelatedWorkRunning(controller))
            {
                return MaaJobStatus.Succeeded;
            }

            return MaaJobStatus.Invalid;
        }

        try
        {
            return controller.Screencap().Wait();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"提交截图任务失败：{ex.Message}");
            return MaaJobStatus.Invalid;
        }
    }

    /// <summary>
    /// 实时视图在途截图任务（流水线复用，非阻塞）。
    /// </summary>
    private MaaJob? _liveViewScreencapJob;
    private readonly Lock _liveViewScreencapJobLock = new();

    /// <summary>
    /// 流水线式提交实时视图截图：非阻塞，提交后立即返回，截图由控制器在后台执行。
    /// 若上一帧截图仍在执行则复用而不重复提交，避免任务堆积。
    /// 相比原 PostScreencap 的同步 Screencap().Wait()，定时器不再被截图耗时阻塞，
    /// 实时视图刷新率可真正由 LiveViewRefreshRate 决定。
    /// </summary>
    /// <returns>null 表示控制器不可用或截图任务器已切换；Succeeded 表示已提交或仍有正常在途截图；
    /// Failed/Invalid 表示上一帧截图失败（与 PostScreencap 的失败语义一致）。</returns>
    public MaaJobStatus? PostScreencapPipelined()
    {
        var screenshotTaskerGeneration = Volatile.Read(ref _screenshotTaskerGeneration);
        var controller = GetScreenshotController(false);
        if (controller == null)
        {
            return null;
        }

        if (!controller.IsConnected)
        {
            if (screenshotTaskerGeneration != Volatile.Read(ref _screenshotTaskerGeneration))
            {
                // The tasker was detached while resolving its controller.
                return null;
            }

            return IsAnyScreenshotRelatedWorkRunning(controller)
                ? MaaJobStatus.Succeeded
                : MaaJobStatus.Invalid;
        }

        lock (_liveViewScreencapJobLock)
        {
            if (screenshotTaskerGeneration != Volatile.Read(ref _screenshotTaskerGeneration))
            {
                // The tasker was detached while resolving its controller.
                return null;
            }

            var job = _liveViewScreencapJob;
            if (job != null)
            {
                MaaJobStatus status;
                try
                {
                    status = job.Status;
                }
                catch (ObjectDisposedException)
                {
                    // 任务器已重建，放弃旧的在途任务
                    _liveViewScreencapJob = null;
                    return MaaJobStatus.Invalid;
                }
                catch
                {
                    _liveViewScreencapJob = null;
                    return MaaJobStatus.Invalid;
                }

                if (status.IsRunning() || status.IsPending())
                {
                    // 上一帧仍在执行，保持流水线复用，不重复提交
                    return MaaJobStatus.Succeeded;
                }

                // 上一帧已结束：若失败，先向调用方上报（仅一次），随后替换为新的在途任务
                _liveViewScreencapJob = null;
                if (status is MaaJobStatus.Failed or MaaJobStatus.Invalid)
                {
                    return status;
                }
            }

            try
            {
                _liveViewScreencapJob = controller.Screencap();
                return MaaJobStatus.Succeeded;
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"提交截图任务失败：{ex.Message}");
                _liveViewScreencapJob = null;
                return MaaJobStatus.Invalid;
            }
        }
    }

    /// <summary>
    /// 实时视图流水线清理：放弃在途截图任务。截图任务器分离时也会在代际变更下清空在途任务。
    /// </summary>
    public void ResetLiveViewScreencapJob()
    {
        lock (_liveViewScreencapJobLock)
        {
            _liveViewScreencapJob = null;
        }
    }

    public bool IsMainControllerConnected()
    {
        try
        {
            return MaaTasker?.Controller?.IsConnected == true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"检查主控制器连接状态失败：{ex.Message}");
            return false;
        }
    }

    public void ResetLiveViewTasker()
    {
        ResetActionFailedCount();
        DisposeScreenshotTasker();
    }

    private bool IsAnyScreenshotRelatedWorkRunning(MaaController? controller)
    {
        return controller?.IsConnected == true || _screenshotTasker?.IsRunning == true || MaaTasker?.IsRunning == true;
    }

    private static bool IsControllerRunning(IMaaController? controller)
    {
        if (controller == null)
            return false;

        try
        {
            var property = controller.GetType().GetProperty("IsRunning");
            return property?.PropertyType == typeof(bool) && property.GetValue(controller) is true;
        }
        catch
        {
            return false;
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
            LoggerHelper.Warning($"获取图像失败：{ex.Message}");
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
            LoggerHelper.Warning($"截图控制器初始化失败：{ex.Message}");
            try
            {
                controller?.Dispose();
            }
            catch (Exception disposeEx)
            {
                LoggerHelper.Warning($"释放截图控制器失败：{disposeEx.Message}");
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
            LoggerHelper.Warning($"截图控制器连接失败：{ex.Message}");
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
                var attachedPaths = MaaInterface.ReplacePlaceholder(controllerConfig.AttachResourcePath, AppPaths.DataRoot);
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
            LoggerHelper.Warning($"截图任务资源初始化失败：{ex.Message}");
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
            LoggerHelper.Warning($"截图任务控制器初始化失败：{ex.Message}");
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
            LoggerHelper.Warning($"截图任务执行器初始化失败：{ex.Message}");
            return null;
        }
    }

    private void ConfigureScreenshotTasker(MaaTasker tasker)
    {
        var logDir = Path.Combine(AppPaths.LogsDirectory, "log_screencap");
        // if (!Directory.Exists(logDir))
        //     Directory.CreateDirectory(logDir);
        LoggerHelper.Info("截图日志目录：" + logDir);
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
        try
        {
            var preTaskExecuted = await RunPreTasksAsync(token);
            var shouldRefreshWin32Window = ViewModel is { CurrentController: MaaControllerTypes.Win32, CurrentDevice: not null }
                && !ViewModel.IsCurrentWin32WindowValid();
            if (ViewModel?.CurrentController != MaaControllerTypes.PlayCover
                && ((preTaskExecuted && ViewModel?.CurrentDevice == null) || shouldRefreshWin32Window))
            {
                await Task.Run(() => ViewModel.AutoDetectDevice(token, showToast: false), token);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LoggerHelper.Error("执行 PI pretask 失败", ex);
            AddLog($"pretask failed: {ex.Message}", Brushes.OrangeRed, changeColor: false);
            return (null, true, false);
        }
        MaaResource maaResource = null;
        try
        {
            var currentResource = ViewModel?.CurrentResources
                .FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            // 优先使用 ResolvedPath（运行时路径），如果没有则使用 Path
            var resources = currentResource?.ResolvedPath ?? currentResource?.Path ?? [];
            resources = resources.Select(Path.GetFullPath).ToList();

            // PI v2.6.0 要求 hash 只基于 resource.path，必须在 attach_resource_path 之前校验。
            if (!string.IsNullOrWhiteSpace(currentResource?.Hash))
            {
                using var hashResource = new MaaResource(resources);
                var actualHash = hashResource.Hash;
                if (!string.Equals(actualHash, currentResource.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"资源完整性校验不匹配：expected={currentResource.Hash}, actual={actualHash}";
                    LoggerHelper.Warning(message);
                    AddLog(message, Brushes.OrangeRed, changeColor: false);
                    ToastHelper.Warn(message);
                }
            }

            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
            var controllerName = controllerType.ToJsonKey();
            var controllerConfig = Interface?.Controller?.FirstOrDefault(c =>
                c.Type != null && c.Type.Equals(controllerName, StringComparison.OrdinalIgnoreCase));

            if (controllerConfig?.AttachResourcePath != null)
            {
                var attachedPaths = MaaInterface.ReplacePlaceholder(controllerConfig.AttachResourcePath, AppPaths.DataRoot);
                if (attachedPaths != null)
                {
                    resources.AddRange(attachedPaths.Select(Path.GetFullPath));
                }
            }

            LoggerHelper.Info($"资源路径：{string.Join(",", resources)}");


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
            LoggerHelper.Warning("资源加载已取消。");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaJobStatusException ex)
        {
            ShouldRetry = false;
            TelemetryService.RecordTaskFailure(InstanceId, "resource_initialization_failed", "resource_load", ex);
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            ShouldRetry = false;
            LoggerHelper.Error("资源初始化失败", e);
            TelemetryService.RecordTaskFailure(InstanceId, "resource_initialization_failed", "resource_load", e);
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
                LangKeys.InitControllerFailed.ToLocalization(),
                showToast: !_suppressConnectionAttemptErrorToast));

            if (controller == null)
            {
                LoggerHelper.Warning("控制器初始化结果为空，已中止本次连接尝试。");
                TelemetryService.RecordTaskFailure(InstanceId, "controller_initialization_failed", "controller", "null_result");
                return (null, InvalidResource, ShouldRetry);
            }

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
            LoggerHelper.Warning("控制器初始化已取消。");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException ex)
        {
            TelemetryService.RecordTaskFailure(InstanceId, "controller_initialization_failed", "controller", ex);
            return (null, InvalidResource, ShouldRetry); // 控制器异常可以重试
        }
        catch (Exception e)
        {
            LoggerHelper.Error("控制器初始化失败", e);
            TelemetryService.RecordTaskFailure(InstanceId, "controller_initialization_failed", "controller", e);
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
            MaaJobStatus? linkStatus;
            try
            {
                linkStatus = tasker.Controller?.LinkStart().Wait();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                TelemetryService.RecordTaskFailure(InstanceId, "controller_link_start_failed", "controller", ex);
                throw;
            }
            if (linkStatus != MaaJobStatus.Succeeded)
            {
                LoggerHelper.Warning($"控制器 LinkStart 失败：状态={linkStatus}");
                tasker.Dispose();
                TelemetryService.RecordTaskFailure(InstanceId, "controller_link_start_failed", "controller", linkStatus.ToString());
                return (null, InvalidResource, ShouldRetry);
            }

            tasker.Releasing += (_, _) =>
            {
                tasker.Callback -= HandleCallBack;
            };

            try
            {
                var tempMFADir = AppPaths.TempMfaDirectory;
                if (Directory.Exists(tempMFADir))
                    Directory.Delete(tempMFADir, true);

                var tempMaaDir = AppPaths.TempMaaFwDirectory;
                if (Directory.Exists(tempMaaDir))
                    Directory.Delete(tempMaaDir, true);

                var tempResDir = AppPaths.TempResourceDirectory;
                if (Directory.Exists(tempResDir))
                    Directory.Delete(tempResDir, true);
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"清理临时目录失败：reason={e.Message}", e);
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
                        // A configured Agent that produces no context is a failure even without an exception.
                        ShouldRetry = false;
                        TelemetryService.RecordTaskFailure(InstanceId, "agent_start_failed", "agent", "no_agent_context");
                        return (null, InvalidResource, ShouldRetry);
                    }
                    _agentStarted = true;
                }
                catch (OperationCanceledException)
                {
                    LoggerHelper.Info("用户已取消 Agent 初始化。");
                    AgentHelper.KillAllAgents(_agentContexts);
                    throw;
                }
                catch (Exception ex)
                {
                    AddLogByKey(LangKeys.AgentStartFailed, Brushes.OrangeRed, changeColor: false);
                    LoggerHelper.Error($"启动 Agent 失败：reason={ex.Message}", ex);
                    var isNullReference = ex is NullReferenceException
                        || ex.Message.Contains("Object reference not set to an instance of an object.", StringComparison.OrdinalIgnoreCase);
                    if (isNullReference)
                        ToastHelper.Error(LangKeys.AgentStartFailed.ToLocalization());
                    else
                        ToastHelper.Error(LangKeys.AgentStartFailed.ToLocalization(), ex.Message);
                    AgentHelper.KillAllAgents(_agentContexts);
                    ShouldRetry = false;
                    TelemetryService.RecordTaskFailure(InstanceId, "agent_start_failed", "agent", ex);
                    return (null, InvalidResource, ShouldRetry);
                }
            }
            RegisterCustomRecognitionsAndActions(tasker);
            ViewModel?.SetConnected(true);
            //  tasker.Utility.SetOption_Recording(ConfigurationManager.Maa.GetValue(ConfigurationKeys.Recording, false));
            tasker.Global.SetOption_SaveDraw(ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveDraw, false));
            tasker.Global.SetOption(GlobalOption.SaveOnError, ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveOnError, true));
            tasker.Global.SetOption_DebugMode(ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false));
            LoggerHelper.Info("MaaFW 调试模式：" + ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false));
            // The Android native task runner invokes node callbacks while its action
            // lock is held. Crossing that callback into Mono can suspend the Avalonia
            // process before StartApp/touch reaches the controller, even when the
            // managed handler immediately queues its work. Android gets task state
            // from the queued MaaJob and video from the Shizuku display backend, so it
            // must not subscribe to this synchronous native callback stream.
            if (!OperatingSystem.IsAndroid())
                tasker.Callback += HandleCallBack;
            ResetScreencapFailureLogFlags();
            return (tasker, InvalidResource, ShouldRetry);
        }
        catch (OperationCanceledException)
        {
            LoggerHelper.Warning("任务执行器初始化已取消。");
            return (null, InvalidResource, ShouldRetry);
        }
        catch (MaaException ex)
        {
            TelemetryService.RecordTaskFailure(InstanceId, "tasker_initialization_failed", "tasker", ex);
            return (null, InvalidResource, ShouldRetry);
        }
        catch (Exception e)
        {
            LoggerHelper.Error("任务执行器初始化失败", e);
            TelemetryService.RecordTaskFailure(InstanceId, "tasker_initialization_failed", "tasker", e);
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
        if (OperatingSystem.IsAndroid())
        {
            // MaaFramework invokes callbacks synchronously while some native action
            // locks are still held. Keep the Android native callback boundary free of
            // UI, telemetry, and reverse Tasker calls; otherwise Action.Starting can
            // deadlock the task thread and eventually ANR the Avalonia main thread.
            // MaaCallbackEventArgs owns managed strings, so copying it is sufficient.
            var copiedArgs = new MaaCallbackEventArgs(args.Message, args.Details, args.HandleType);
            var copiedSender = sender is MaaTasker ? sender : MaaTasker;
            _ = Task.Run(() =>
            {
                try
                {
                    HandleCallBackCore(copiedSender, copiedArgs);
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"处理 Android MaaFramework 回调时发生错误：{ex.Message}");
                }
            });
            return;
        }

        HandleCallBackCore(sender, args);
    }

    private void HandleCallBackCore(object? sender, MaaCallbackEventArgs args)
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

        var callbackName = jObject["name"]?.ToString() ?? string.Empty;
        var shouldTraceNodeEvent = ShouldTraceNodeEvent(jObject, args.Message);
        if (CancellationTokenSource?.IsCancellationRequested != true
            && (shouldTraceNodeEvent || args.Message.Equals("Node.PipelineNode.Starting", StringComparison.Ordinal)))
        {
            TelemetryService.RecordNodeEvent(InstanceId, args.Message, args.Details, shouldTraceNodeEvent);
        }

        var showHitDraw = Instances.GameSettingsUserControlModel.ShowHitDraw;
        var hasFocus = jObject["focus"] is
            { Type: not JTokenType.Null and not JTokenType.Undefined };
        MaaTasker? tasker = sender as MaaTasker;
        if ((showHitDraw || hasFocus) && tasker == null)
        {
            // MaaContext.Tasker is a native reverse lookup. During Action.Starting,
            // MaaFramework still owns the action lock, so performing that lookup from
            // its callback re-enters the tasker and deadlocks before the controller
            // receives StartApp/touch calls. The processor already owns the exact
            // tasker for this run; use that managed reference instead.
            tasker = MaaTasker;
        }

        if (tasker != null && showHitDraw)
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
                        global::Avalonia.Rect? drawRect = null;
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
                                    drawRect = new global::Avalonia.Rect(rect.X, rect.Y, rect.Width, rect.Height);
                                }
                                bitmapToSet = bitmap;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Warning($"处理回调识别信息时发生错误：{ex.Message}");
                            bitmapToSet?.Dispose();
                            bitmapToSet = null;
                        }


                        if (bitmapToSet != null)
                        {
                            var finalBitmap = bitmapToSet;
                            var finalDrawRect = drawRect;
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                if (MaaProcessorManager.Instance.Current == this)
                                {
                                    var imageToSet = finalBitmap;
                                    if (finalDrawRect.HasValue)
                                    {
                                        var newBitmap = finalBitmap.DrawRectangle(finalDrawRect.Value, Brushes.LightGreen, 1.5f);
                                        // 如果 DrawRectangle 返回了新的 Bitmap，释放原始的
                                        if (!ReferenceEquals(newBitmap, finalBitmap))
                                        {
                                            finalBitmap.Dispose();
                                        }
                                        imageToSet = newBitmap;
                                    }

                                    // 释放旧的截图
                                    var oldImage = Instances.ScreenshotViewModel.ScreenshotImage;
                                    Instances.ScreenshotViewModel.ScreenshotImage = imageToSet;
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
                        global::Avalonia.Rect? drawRect = null;
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
                                    drawRect = new global::Avalonia.Rect(rect.X, rect.Y, rect.Width, rect.Height);
                                }
                                bitmapToSet = bitmap;
                            }
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Warning($"处理回调动作信息时发生错误：{ex.Message}");
                            bitmapToSet?.Dispose();
                            bitmapToSet = null;
                        }


                        if (bitmapToSet != null)
                        {
                            var finalBitmap = bitmapToSet;
                            var finalDrawRect = drawRect;
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                if (MaaProcessorManager.Instance.Current == this)
                                {
                                    var imageToSet = finalBitmap;
                                    if (finalDrawRect.HasValue)
                                    {
                                        var newBitmap = finalBitmap.DrawRectangle(finalDrawRect.Value, Brushes.LightGreen, 1.5f);
                                        // 如果 DrawRectangle 返回了新的 Bitmap，释放原始的
                                        if (!ReferenceEquals(newBitmap, finalBitmap))
                                        {
                                            finalBitmap.Dispose();
                                        }
                                        imageToSet = newBitmap;
                                    }

                                    // 释放旧的截图
                                    var oldImage = Instances.ScreenshotViewModel.ScreenshotImage;
                                    Instances.ScreenshotViewModel.ScreenshotImage = imageToSet;
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

        // MaaFramework includes `focus: null` in ordinary node callbacks. Treating
        // the mere presence of that field as an active focus makes Action.Starting
        // call back into GetCachedImage while the native action lock is held. The
        // Android tasker then deadlocks before StartApp reaches the controller.
        if (hasFocus)
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
                                LoggerHelper.Warning($"处理回调焦点识别详情时发生错误：{ex.Message}");
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
                    LoggerHelper.Warning($"处理回调焦点图像时发生错误：{ex.Message}");
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
        string waringMessage = "",
        bool showToast = true)
    {
        if (showToast)
            ToastHelper.Error(message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error($"初始化控制器失败：message={message}, reason={e.Message}", e);
    }

    private void HandleInitializationError(Exception e,
        string title,
        string message,
        bool hasWarning = false,
        string waringMessage = "",
        bool showToast = true)
    {
        if (showToast)
            ToastHelper.Error(title, message);
        if (hasWarning)
            LoggerHelper.Warning(waringMessage);
        LoggerHelper.Error($"初始化控制器失败：title={title}, message={message}, reason={e.Message}", e);
    }

    private MaaController InitializeController(MaaControllerTypes controllerType, bool logConfig)
    {
        ConnectToMAA(logConfig);

        var platformController = PlatformControllerFactory.TryCreate(controllerType);
        if (platformController != null)
        {
            if (logConfig)
                LoggerHelper.Info($"当前平台已接管控制器：{platformController.GetType().Name}");
            return platformController;
        }

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
                    LoggerHelper.Info($"设备名称：{Config.AdbDevice.Name}");
                    LoggerHelper.Info($"ADB 路径：{Config.AdbDevice.AdbPath}");
                    LoggerHelper.Info($"ADB 序列号：{Config.AdbDevice.AdbSerial}");
                    LoggerHelper.Info($"截图模式：{Config.AdbDevice.ScreenCap}");
                    LoggerHelper.Info($"输入模式：{Config.AdbDevice.Input}");
                    LoggerHelper.Info($"控制器配置：{Config.AdbDevice.Config}");
                }

                return new MaaAdbController(
                    Config.AdbDevice.AdbPath,
                    Config.AdbDevice.AdbSerial,
                    Config.AdbDevice.ScreenCap, Config.AdbDevice.Input,
                    !string.IsNullOrWhiteSpace(Config.AdbDevice.Config) ? Config.AdbDevice.Config : "{}",
                    Path.Combine(AppPaths.InstallRoot, "libs", "MaaAgentBinary")
                );

            case MaaControllerTypes.PlayCover:
                if (logConfig)
                {
                    LoggerHelper.Info($"PlayCover 地址：{Config.PlayCover.PlayCoverAddress}");
                    LoggerHelper.Info($"PlayCover BundleId：{Config.PlayCover.UUID}");
                }

                return new MaaPlayCoverController(Config.PlayCover.PlayCoverAddress, Config.PlayCover.UUID);

            case MaaControllerTypes.MacOS:
                if (logConfig)
                {
                    LoggerHelper.Info("当前控制器类型：MacOS");
                    LoggerHelper.Info($"窗口名称：{Config.MacOSWindow.Name}");
                    LoggerHelper.Info($"窗口 ID：{Config.MacOSWindow.WindowId}");
                    LoggerHelper.Info($"截图模式：{Config.MacOSWindow.ScreenCap}");
                    LoggerHelper.Info($"输入模式：{Config.MacOSWindow.Input}");
                }

                return new MaaMacOSController(
                    Config.MacOSWindow.WindowId,
                    Config.MacOSWindow.ScreenCap,
                    Config.MacOSWindow.Input,
                    Config.MacOSWindow.Link,
                    Config.MacOSWindow.Check);

            case MaaControllerTypes.WlRoots:
                if (logConfig)
                {
                    LoggerHelper.Info("当前控制器类型：WlRoots");
                    LoggerHelper.Info($"Wayland Socket：{Config.WlRoots.SocketPath}");
                    LoggerHelper.Info($"使用 Win32 VK 键码：{Config.WlRoots.UseWin32VirtualKeyCodes}");
                }

                return new MaaWlRootsController(
                    Config.WlRoots.SocketPath,
                    Config.WlRoots.UseWin32VirtualKeyCodes,
                    Config.WlRoots.Link,
                    Config.WlRoots.Check);

            case MaaControllerTypes.Gamepad:
                if (logConfig)
                {
                    LoggerHelper.Info("当前控制器类型：Gamepad");
                    LoggerHelper.Info($"窗口名称：{Config.DesktopWindow.Name}");
                    LoggerHelper.Info($"窗口句柄：{Config.DesktopWindow.HWnd}");
                    LoggerHelper.Info($"截图模式：{Config.Gamepad.ScreenCap}");
                    LoggerHelper.Info($"手柄类型：{Config.Gamepad.Type}");
                }

                return new MaaGamepadController(Config.DesktopWindow.HWnd, Config.Gamepad.Type,
                    Config.Gamepad.ScreenCap, Config.DesktopWindow.Link, Config.DesktopWindow.Check);

            case MaaControllerTypes.Win32:
            default:
                if (logConfig)
                {
                    LoggerHelper.Info($"窗口名称：{Config.DesktopWindow.Name}");
                    LoggerHelper.Info($"窗口句柄：{Config.DesktopWindow.HWnd}");
                    LoggerHelper.Info($"截图模式：{Config.DesktopWindow.ScreenCap}");
                    LoggerHelper.Info($"鼠标输入：{Config.DesktopWindow.Mouse}");
                    LoggerHelper.Info($"键盘输入：{Config.DesktopWindow.KeyBoard}");
                    LoggerHelper.Info($"连接方式：{Config.DesktopWindow.Link}");
                    LoggerHelper.Info($"检查方式：{Config.DesktopWindow.Check}");
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
            LoggerHelper.Info("未找到界面资源定义文件，准备生成默认 interface.json");
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
            string resourceDir = Path.Combine(AppPaths.ResourceDirectory, "base");
            if (!Directory.Exists(resourceDir))
                Directory.CreateDirectory(resourceDir);
            JsonHelper.SaveJson(AppPaths.InterfaceJsonPath,
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

    /// <summary>
    /// 在 UI 和实例初始化期间并行预加载 interface。此阶段只读取和解析数据，
    /// 不设置 Interface，避免从后台线程触发语言及 UI 刷新。
    /// </summary>
    public static void StartInterfacePreload()
    {
        lock (InterfacePreloadLock)
        {
            if (Interface != null || _interfacePreloadTask != null)
            {
                return;
            }

            var interfacePath = GetInterfaceFilePath();
            if (interfacePath == null)
            {
                return;
            }

            _interfacePreloadTask = Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var result = LoadMaaInterfaceRecursive(interfacePath);
                LoggerHelper.Info($"[Interface] 预加载完成，耗时 {stopwatch.ElapsedMilliseconds} ms");
                return result;
            });
        }
    }

    private static int? GetPreloadedInterfaceVersion()
    {
        if (Interface != null)
        {
            return Interface.InterfaceVersion;
        }

        StartInterfacePreload();

        Task<MaaInterface>? preloadTask;
        lock (InterfacePreloadLock)
        {
            preloadTask = _interfacePreloadTask;
        }

        if (preloadTask == null)
        {
            return null;
        }

        try
        {
            return preloadTask.GetAwaiter().GetResult().InterfaceVersion;
        }
        catch
        {
            // ReadInterface 会在管理器构造完成后统一记录并展示解析错误。
            // 此处等待任务也确保预加载异常已被观察，不会落到终结器线程。
            return null;
        }
    }

    public static (string Key, string Fallback, string Version, string CustomTitle, string CustomFallback) ReadInterface()
    {
        if (Interface != null)
        {
            return (Interface.Label ?? string.Empty, Interface.Name ?? string.Empty, Interface.Version ?? string.Empty,
                Interface.Title ?? string.Empty, Interface.CustomTitle ?? string.Empty);
        }

        if (CheckInterface(out string name, out string back, out string version, out string customTitle, out var fallBack))
        {
            return (name, back, version, customTitle, fallBack);
        }

        var interfacePath = GetInterfaceFilePath() ?? AppPaths.InterfaceJsonPath;
        var interfaceFileName = Path.GetFileName(interfacePath);
        var defaultValue = new MaaInterface();

        try
        {
            Task<MaaInterface>? preloadTask;
            lock (InterfacePreloadLock)
            {
                preloadTask = _interfacePreloadTask;
            }

            // 优先复用启动阶段的预加载结果；非标准启动流程仍同步加载。
            Interface = preloadTask?.GetAwaiter().GetResult() ?? LoadMaaInterfaceRecursive(interfacePath);
        }
        catch (Exception ex)
        {
            Interface = defaultValue;
            var error = "";
            LoggerHelper.Error($"加载界面资源定义失败：file={interfaceFileName}, reason={ex.Message}", ex);

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
                LoggerHelper.Error($"加载界面资源定义失败：file={interfaceFileName}, reason={e.Message}", e);
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
            LoggerHelper.Warning($"检测到循环依赖：{fullPath}");
            return new MaaInterface();
        }
        loadedPaths.Add(fullPath);

        // 使用 JsonHelper 加载，如果失败会抛出异常
        var loaded = JsonHelper.LoadJson<MaaInterface>(fullPath, null, new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false));

        if (loaded == null)
            throw new Exception($"Failed to load interface file: {fullPath}");

        // PI import 的顺序为主文件在前、随后按 import 数组顺序追加；同名 option 以后导入者为准。
        var result = loaded;

        if (loaded.Import != null)
        {
            foreach (var importPath in loaded.Import)
            {
                var resolvedPath = MaaInterface.ReplacePlaceholder(importPath, Path.GetDirectoryName(fullPath));
                if (string.IsNullOrWhiteSpace(resolvedPath)) continue;

                try
                {
                    var imported = LoadMaaInterfaceRecursive(resolvedPath, loadedPaths);

                    // 仅支持导入与任务配置直接相关的字段
                    var filteredImport = new MaaInterface
                    {
                        Group = imported.Group,
                        Option = imported.Option,
                        PreTask = imported.PreTask,
                        GlobalOption = imported.GlobalOption,
                        Setting = imported.Setting
                    };

                    result.Merge(filteredImport);
                    // 协议规定 task / preset 按 import 顺序追加，不按名称覆盖或去重。
                    if (imported.Task is { Count: > 0 })
                        result.Task = [.. result.Task ?? [], .. imported.Task];
                    if (imported.Preset is { Count: > 0 })
                        result.Preset = [.. result.Preset ?? [], .. imported.Preset];
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"加载导入的 interface 文件失败：{resolvedPath}，原因：{ex.Message}");
                }
            }
        }

        return result;
    }

    private async Task<bool> RunPreTasksAsync(CancellationToken token)
    {
        if (Interface?.PreTask is not { Count: > 0 }) return false;
        var controllerName = ViewModel?.GetCurrentControllerName()
            ?? MaaInterfaceActivationHelper.ResolveControllerName(
                Interface, ViewModel?.CurrentController ?? MaaControllerTypes.None)
            ?? ViewModel?.CurrentController.ToJsonKey();
        var resourceName = ViewModel?.CurrentResource;
        var executed = false;

        foreach (var preTask in Interface.PreTask)
        {
            if (string.IsNullOrWhiteSpace(preTask.Exec))
                throw new InvalidDataException("pretask.exec is required");
            if (preTask.Controller is { Count: > 0 }
                && !preTask.Controller.Any(name => string.Equals(name, controllerName, StringComparison.OrdinalIgnoreCase))) continue;
            if (preTask.Resource is { Count: > 0 }
                && !preTask.Resource.Any(name => string.Equals(name, resourceName, StringComparison.OrdinalIgnoreCase))) continue;

            var exec = MaaInterface.ReplacePlaceholder(preTask.Exec, ResourceBase) ?? preTask.Exec;
            var info = new ProcessStartInfo
            {
                FileName = exec,
                WorkingDirectory = ResourceBase,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in preTask.Args ?? []) info.ArgumentList.Add(arg);
            if (preTask.Option is { Count: > 0 })
                info.ArgumentList.Add(BuildPreTaskOptionJson(preTask.Option, controllerName, resourceName));

            LoggerHelper.Info($"执行 pretask：{preTask.Name ?? preTask.Exec}");
            executed = true;
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 pretask: {preTask.Exec}");
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"pretask '{preTask.Name ?? preTask.Exec}' exited with code {process.ExitCode}");
        }

        return executed;
    }

    private static bool ShouldTraceNodeEvent(JObject details, string message)
    {
        var defaultValue = message.Equals("Node.PipelineNode.Failed", StringComparison.Ordinal);
        if (details["focus"] is not JObject focus
            || !focus.TryGetValue(message, StringComparison.Ordinal, out var template)
            || template is not JObject templateObject)
            return defaultValue;

        return templateObject["trace"]?.Value<bool?>() ?? defaultValue;
    }

    private string BuildPreTaskOptionJson(IEnumerable<string> optionNames, string? controllerName, string? resourceName)
    {
        var result = new JObject();
        var selected = ViewModel?.TaskItemViewModels
            .SelectMany(item => item.IsResourceOptionItem
                ? item.ResourceItem?.SelectOptions ?? []
                : item.InterfaceItem?.Option ?? [])
            .Where(option => !string.IsNullOrWhiteSpace(option.Name))
            .GroupBy(option => option.Name!)
            .ToDictionary(group => group.Key, group => group.First())
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        foreach (var name in optionNames)
        {
            if (Interface?.Option?.TryGetValue(name, out var definition) != true) continue;
            if (!MaaInterfaceActivationHelper.IsOptionApplicable(Interface, definition, controllerName, resourceName)) continue;
            selected.TryGetValue(name, out var value);
            if (definition.IsCheckbox)
                result[name] = JToken.FromObject(value?.SelectedCases ?? definition.DefaultCases ?? []);
            else if (definition.IsInput || definition.IsHotkey)
            {
                var data = value?.Data ?? new Dictionary<string, string?>();
                var defaults = definition.IsHotkey
                    ? definition.Hotkeys?.ToDictionary(x => x.Name ?? string.Empty, x => x.Default ?? string.Empty)
                    : definition.Inputs?.ToDictionary(x => x.Name ?? string.Empty, x => x.Default ?? string.Empty);
                result[name] = JObject.FromObject(defaults == null
                    ? data
                    : defaults.Concat(data).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value));
            }
            else
            {
                var index = value?.Index ?? Math.Max(0, definition.Cases?.FindIndex(x => x.Name == definition.DefaultCase) ?? 0);
                result[name] = definition.Cases?.ElementAtOrDefault(index)?.Name ?? definition.DefaultCase ?? string.Empty;
            }
        }
        return result.ToString(Formatting.None);
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
                        LoggerHelper.Warning($"创建资源目录失败：{rp}，原因：{ex.Message}");
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
                        LoggerHelper.Error($"创建任务管线目录失败：path={pipeline}, reason={ex.Message}", ex);
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
                        LoggerHelper.Error($"写入默认任务管线示例失败：path={Path.Combine(pipeline, "sample.json")}, reason={ex.Message}", ex);
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
            LoggerHelper.Error($"加载任务管线失败：reason={ex.Message}", ex);
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
            LoggerHelper.Info("正在加载 MAA 控制器配置...");
        }
        ConfigureMaaProcessorForADB(logConfig);
        ConfigureMaaProcessorForWin32(logConfig);
        ConfigureMaaProcessorForMacOS(logConfig);
        ConfigureMaaProcessorForGamepad(logConfig);
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

    private void ConfigureMaaProcessorForMacOS(bool logConfig)
    {
        if (ViewModel?.CurrentController != MaaControllerTypes.MacOS)
            return;

        Config.MacOSWindow.ScreenCap = InstanceConfiguration.GetValue(ConfigurationKeys.MacOSControlScreenCapType,
            MacOSScreencapMethod.ScreenCaptureKit, default(MacOSScreencapMethod), new UniversalEnumConverter<MacOSScreencapMethod>());
        Config.MacOSWindow.Input = InstanceConfiguration.GetValue(ConfigurationKeys.MacOSControlInputType,
            MacOSInputMethod.GlobalEvent, default(MacOSInputMethod), new UniversalEnumConverter<MacOSInputMethod>());

        if (logConfig)
            LoggerHelper.Info($"{LangKeys.CaptureModeOption.ToLocalization()}:{Config.MacOSWindow.ScreenCap},{LangKeys.InputModeOption.ToLocalization()}:{Config.MacOSWindow.Input}");
    }

    private void ConfigureMaaProcessorForGamepad(bool logConfig)
    {
        if (ViewModel?.CurrentController != MaaControllerTypes.Gamepad)
            return;

        Config.Gamepad.ScreenCap = InstanceConfiguration.GetValue(ConfigurationKeys.GamepadControlScreenCapType,
            Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None, new UniversalEnumConverter<Win32ScreencapMethods>());
        Config.Gamepad.Type = InstanceConfiguration.GetValue(ConfigurationKeys.GamepadType,
            GamepadType.Xbox360, default(GamepadType), new UniversalEnumConverter<GamepadType>());

        if (logConfig)
            LoggerHelper.Info($"{LangKeys.CaptureModeOption.ToLocalization()}:{Config.Gamepad.ScreenCap},{LangKeys.VirtualGamepadType.ToLocalization()}:{Config.Gamepad.Type}");
    }

    private Win32ScreencapMethods ConfigureWin32ScreenCapTypes()
    {
        return InstanceConfiguration.GetValue(ConfigurationKeys.Win32ControlScreenCapType,
            Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None,
            new UniversalEnumConverter<Win32ScreencapMethods>());
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
            LoggerHelper.Error("获取 MaaFramework 版本失败", e);
        }

        // Log all version information
        LoggerHelper.Info($"资源版本：{_tempResourceVersion}");
        LoggerHelper.Info($"MaaFramework 版本：{frameworkVersion}");
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
                    using var logScope = BeginInstanceLogScope("CommandLoop", "Worker");
                    command().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LoggerHelper.Error($"停止窗口焦点监听失败：reason={ex.Message}", ex);
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
            LoggerHelper.Info("命令队列已停止，已忽略本次请求。");
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
                LoggerHelper.Warning($"停止命令线程失败：{ex.Message}");
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
        await _connectionGate.WaitAsync();
        Interlocked.Exchange(ref _isConnecting, 1);
        try
        {
            if (ViewModel is { CurrentController: MaaControllerTypes.Win32, CurrentDevice: not null }
                && !ViewModel.IsCurrentWin32WindowValid())
            {
                LoggerHelper.Info("检测到 Win32 目标窗口已失效，释放旧控制器并重新发现窗口。");
                ViewModel.InvalidateCurrentWin32Window();
                SetTasker();
                await Task.Run(() => ViewModel.AutoDetectDevice(showToast: false));
            }

            await GetTaskerAsync();
            var task = MaaTasker?.Controller?.LinkStart();
            task?.Wait();
            ViewModel?.SetConnected(task?.Status == MaaJobStatus.Succeeded);
        }
        finally
        {
            Interlocked.Exchange(ref _isConnecting, 0);
            _connectionGate.Release();
        }
    }


    public async Task ReconnectAsync(CancellationToken token = default, bool showMessage = true)
    {
        await HandleDeviceConnectionAsync(token, showMessage);
    }

    public void Start(bool onlyStart = false, bool checkUpdate = false)
    {
        if (!TryBeginTaskRun())
        {
            LoggerHelper.Info("任务启动请求已忽略：当前已有任务链正在启动或运行。");
            return;
        }
        EnqueueCommand(() => StartInternal(null, onlyStart, checkUpdate));
    }

    public void Start(List<DragItemViewModel> dragItemViewModels, bool onlyStart = false, bool checkUpdate = false, bool ignoreCheckedState = false)
    {
        if (!TryBeginTaskRun())
        {
            LoggerHelper.Info("任务启动请求已忽略：当前已有任务链正在启动或运行。");
            return;
        }
        EnqueueCommand(() => StartInternal(dragItemViewModels, onlyStart, checkUpdate, ignoreCheckedState));
    }

    private Task StartInternal(List<DragItemViewModel>? dragItemViewModels, bool onlyStart, bool checkUpdate, bool ignoreCheckedState = false)
    {
        using var logScope = BeginInstanceLogScope("StartTask", "Worker");
        // 保存当前的任务列表，以便在重新加载时保留用户调整的顺序和 check 状态
        var currentTasks = new Collection<DragItemViewModel>(ViewModel?.TaskItemViewModels.ToList() ?? new List<DragItemViewModel>());

        if (InitializeData(currentTasks))
        {
            List<DragItemViewModel> tasks;
            if (dragItemViewModels == null)
            {
                tasks = FilterExecutableTasks(ViewModel?.TaskItemViewModels);
            }
            else
            {
                tasks = FilterExecutableTasks(dragItemViewModels, ignoreCheckedState);
            }

            _ = RunTaskChainAsync(tasks, onlyStart, checkUpdate);
            return Task.CompletedTask;
        }

        EndTaskRun();
        return Task.CompletedTask;
    }

    private bool TryBeginTaskRun()
    {
        if (Interlocked.CompareExchange(ref _isTaskRunActive, 1, 0) != 0)
            return false;

        DispatcherHelper.PostOnMainThread(() =>
        {
            if (ViewModel != null)
                ViewModel.IsRunning = true;
        });
        return true;
    }

    private void EndTaskRun()
    {
        Interlocked.Exchange(ref _isTaskRunActive, 0);
        DispatcherHelper.PostOnMainThread(() =>
        {
            if (ViewModel != null)
                ViewModel.IsRunning = TaskQueue.Count > 0;
        });
    }

    private async Task RunTaskChainAsync(List<DragItemViewModel> tasks, bool onlyStart, bool checkUpdate)
    {
        try
        {
            await StartTask(tasks, onlyStart, checkUpdate);
        }
        catch (OperationCanceledException)
        {
            Status = MFATask.MFATaskStatus.STOPPED;
        }
        catch (Exception ex)
        {
            Status = MFATask.MFATaskStatus.FAILED;
            LoggerHelper.Error($"任务链执行异常：reason={ex.Message}", ex);
            Stop(Status, true, onlyStart);
        }
        finally
        {
            EndTaskRun();
        }
    }

    private List<DragItemViewModel> FilterExecutableTasks(IEnumerable<DragItemViewModel>? source, bool ignoreCheckedState = false)
    {
        var currentResourceName = ViewModel?.CurrentResource;
        var currentControllerName = ViewModel?.GetCurrentControllerName();

        return source?
                .Where(task =>
                {
                    if (task.IsResourceOptionItem)
                    {
                        return false;
                    }

                    var isSupported = task.SupportsResource(currentResourceName)
                        && task.SupportsController(currentControllerName);

                    task.IsResourceSupported = task.SupportsResource(currentResourceName);
                    task.IsControllerSupported = task.SupportsController(currentControllerName);
                    task.IsTaskSupported = isSupported;

                    return (ignoreCheckedState || task.IsChecked || task.IsCheckedWithNull == null) && isSupported;
                })
                .ToList()
            ?? new List<DragItemViewModel>();
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
        using var logScope = BeginInstanceLogScope("ExecuteTaskQueue", "Worker");
        ResetActionFailedCount();
        Interlocked.Exchange(ref _stopCompletionMessageHandled, 0);
        Status = MFATask.MFATaskStatus.NOT_STARTED;
        CancellationTokenSource = new CancellationTokenSource();

        _startTime = DateTime.Now;

        var token = CancellationTokenSource.Token;

        // A python-for-android Agent runs in a separate process. Android may reclaim
        // that service after the previous run moved MFA behind the target game while
        // the controller itself remains connected. Recreate the complete tasker/Agent
        // session for each user-started queue so a stale ZeroMQ client is never reused.
        if (!onlyStart
            && OperatingSystem.IsAndroid()
            && PlatformAgentFactory.Start != null
            && AgentHelper.HasAgentConfigs(Interface?.Agent)
            && MaaTasker != null)
        {
            LoggerHelper.Info("Android Agent session will be recreated before this task queue.");
            SetTasker();
        }

        var runId = 0L;
        if (!onlyStart)
        {
            tasks ??= new List<DragItemViewModel>();
            _tempTasks = tasks;
            runId = ViewModel?.BeginTaskRun(tasks) ?? 0;
            LoggerHelper.Info($"准备执行任务队列：任务数量={tasks.Count}");
            var taskAndParams = tasks.Select((task, index) => CreateNodeAndParam(task, index + 1, runId)).ToList();
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

        if (!onlyStart)
        {
            var telemetryTaskNames = (tasks ?? [])
                .Select(task => task.InterfaceItem?.Name ?? task.Name ?? "unknown")
                .ToList();
            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.None;
            TelemetryService.StartRun(
                InstanceId,
                telemetryTaskNames,
                ViewModel?.GetCurrentControllerName(),
                controllerType.ToJsonKey());
        }

        try
        {
            await TaskManager.RunTaskAsync(async () =>
            {
                var queueResult = await ExecuteTasks(token);
                Stop(Status, true, onlyStart, queueCompleted: queueResult.QueueCompleted);
            }, token: token, name: "启动任务");
        }
        finally
        {
            if (!onlyStart)
            {
                TelemetryService.FinishRun(InstanceId, Status);
                ViewModel?.FinalizeTaskRun(runId);
            }
        }

    }

    private readonly record struct TaskQueueResult(bool QueueCompleted);

    async private Task<TaskQueueResult> ExecuteTasks(CancellationToken token)
    {
        var completedWithFailures = false;
        while (!token.IsCancellationRequested && TaskQueue.TryDequeue(out var task))
        {
            // 等待 modal 弹窗确认（display=modal 时任务队列暂停推进）
            while (_isWaitingForModal && !token.IsCancellationRequested)
            {
                await Task.Delay(100, token);
            }
            if (token.IsCancellationRequested) break;

            var result = await task.Run(token);
            if (result.Status == MFATask.MFATaskStatus.FAILED)
            {
                completedWithFailures = true;
                if (result.ContinueQueue)
                    continue;
            }

            if (result.Status != MFATask.MFATaskStatus.SUCCEEDED)
            {
                Status = result.Status;
                return new TaskQueueResult(false);
            }
        }
        if (Status == MFATask.MFATaskStatus.NOT_STARTED)
            Status = token.IsCancellationRequested
                ? MFATask.MFATaskStatus.STOPPED
                : completedWithFailures
                    ? MFATask.MFATaskStatus.FAILED
                    : MFATask.MFATaskStatus.SUCCEEDED;

        return new TaskQueueResult(!token.IsCancellationRequested);
    }

    public class NodeAndParam
    {
        public int Index { get; set; }
        public string? Name { get; set; }
        public string? Entry { get; set; }
        public int? Count { get; set; }

        // public Dictionary<string, MaaNode>? Tasks
        // {
        //     get;
        //     set;
        // }
        public string? Param { get; set; }
        public DragItemViewModel? SourceItem { get; set; }
        public long RunId { get; set; }
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
                if (Interface?.Advanced?.TryGetValue(selectAdvanced.Name ?? string.Empty, out var interfaceAdvanced) == true)
                {
                    var inputValues = selectAdvanced.Data
                        .Where(kv => kv.Value != null)
                        .ToDictionary(kv => kv.Key, kv => kv.Value!);
                    selectAdvanced.PipelineOverride = interfaceAdvanced.GenerateProcessedPipeline(inputValues);
                }

                if (!string.IsNullOrWhiteSpace(selectAdvanced.PipelineOverride) && selectAdvanced.PipelineOverride != "{}")
                {
                    var param = DeserializePipelineOverrideJsonc(selectAdvanced.PipelineOverride);
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
        var currentControllerName = GetCurrentControllerNameForOptionMerge();
        var currentResourceName = ViewModel?.CurrentResource;

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

            // v2.3.1: 不适用于当前 controller/resource 的 option 视为未激活，
            // 它自身及其子 option 都不参与 pipeline_override 合并。
            if (!MaaInterfaceActivationHelper.IsOptionApplicable(
                    Interface,
                    interfaceOption,
                    currentControllerName,
                    currentResourceName))
            {
                continue;
            }

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

                                if (subOptionsToProcess.Count > 0)
                                {
                                    ProcessOptions(ref taskModels, subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
                                }
                            }

                            var missingSubOptionNames = unprocessedSubOptionNames
                                .Where(name => !processedOptions.Contains(name))
                                .ToList();

                            if (missingSubOptionNames.Count > 0)
                            {
                                var defaultSubOptions = CreateDefaultSelectOptions(missingSubOptionNames);
                                if (defaultSubOptions.Count > 0)
                                {
                                    ProcessOptions(ref taskModels, defaultSubOptions, missingSubOptionNames, processedOptions);
                                }
                            }
                        }
                    }
                }
            }
            // 处理 input 类型
            else if (interfaceOption.IsInput)
            {
                if ((selectOption.Data == null || selectOption.Data.Count == 0) && interfaceOption.Inputs is { Count: > 0 })
                {
                    selectOption.Data = interfaceOption.Inputs
                        .Where(input => !string.IsNullOrEmpty(input.Name))
                        .ToDictionary(input => input.Name!, input => input.Default ?? string.Empty);
                }

                // 运行前始终按最新 Data 重建，避免 UI 某些路径未刷新缓存时继续使用旧值。
                string? pipelineOverride = selectOption.PipelineOverride;
                if (selectOption.Data != null && interfaceOption.PipelineOverride != null)
                {
                    pipelineOverride = interfaceOption.GenerateProcessedPipeline(
                        selectOption.Data.Where(kv => kv.Value != null)
                            .ToDictionary(kv => kv.Key, kv => kv.Value!));
                    selectOption.PipelineOverride = pipelineOverride;
                }

                if (!string.IsNullOrWhiteSpace(pipelineOverride) && pipelineOverride != "{}")
                {
                    var param = DeserializePipelineOverrideJsonc(pipelineOverride);
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

                        if (subOptionsToProcess.Count > 0)
                        {
                            ProcessOptions(ref taskModels, subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
                        }
                    }

                    var missingSubOptionNames = unprocessedSubOptionNames
                        .Where(name => !processedOptions.Contains(name))
                        .ToList();

                    if (missingSubOptionNames.Count > 0)
                    {
                        var defaultSubOptions = CreateDefaultSelectOptions(missingSubOptionNames);
                        if (defaultSubOptions.Count > 0)
                        {
                            ProcessOptions(ref taskModels, defaultSubOptions, missingSubOptionNames, processedOptions);
                        }
                    }
                }
            }
        }
    }

    private List<MaaInterface.MaaInterfaceSelectOption> CreateDefaultSelectOptions(IEnumerable<string> optionNames)
    {
        var result = new List<MaaInterface.MaaInterfaceSelectOption>();
        foreach (var optionName in optionNames.Distinct())
        {
            if (string.IsNullOrWhiteSpace(optionName) || Interface?.Option?.ContainsKey(optionName) != true)
                continue;

            var option = new MaaInterface.MaaInterfaceSelectOption
            {
                Name = optionName
            };
            TaskLoader.SetDefaultOptionValue(Interface, option);
            result.Add(option);
        }

        return result;
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

    private NodeAndParam CreateNodeAndParam(DragItemViewModel task, int index, long runId)
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
        LoggerHelper.Info($"[任务管线合并] 任务#{index} 名称=[{task.Name ?? task.InterfaceItem?.Name ?? "<未命名>"}] 入口=[{task.InterfaceItem?.Entry ?? "<空>"}] 参数={taskParams}");
        return new NodeAndParam
        {
            Index = index,
            Name = task.Name,
            Entry = task.InterfaceItem?.Entry,
            Count = task.InterfaceItem?.Repeatable == true ? (task.InterfaceItem?.RepeatCount ?? 1) : 1,
            // Tasks = tasks,
            Param = taskParams,
            SourceItem = task,
            RunId = runId
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

    private string? GetCurrentControllerNameForOptionMerge()
    {
        var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.None;
        return MaaInterfaceActivationHelper.ResolveControllerName(Interface, controllerType)
            ?? controllerType.ToJsonKey();
    }

    private void InitializeConnectionTasksAsync(CancellationToken token)
    {
        TaskQueue.Enqueue(CreateMFATask(LangKeys.Prescript.ToLocalization(), async () =>
        {
            await TaskManager.RunTaskAsync(async () => await RunScript(), token: token, name: "启动附加开始脚本");
        }));

        TaskQueue.Enqueue(CreateMFATask(LangKeys.Connection.ToLocalization(), async () =>
        {
            await HandleDeviceConnectionAsync(token);
        }));

        TaskQueue.Enqueue(CreateMFATask(LangKeys.PerformanceBenchmark.ToLocalization(), async () =>
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
        await _connectionGate.WaitAsync(token);
        Interlocked.Exchange(ref _isConnecting, 1);

        var previousSuppressConnectionAttemptErrorToast = _suppressConnectionAttemptErrorToast;
        _suppressConnectionAttemptErrorToast = true;

        try
        {
            var controllerType = ViewModel?.CurrentController ?? MaaControllerTypes.Adb;
            if (controllerType == MaaControllerTypes.Win32
                && ViewModel is { CurrentDevice: not null }
                && !ViewModel.IsCurrentWin32WindowValid())
            {
                LoggerHelper.Info("检测到 Win32 目标窗口已失效，释放旧控制器并重新发现窗口。");
                ViewModel.InvalidateCurrentWin32Window();
                SetTasker();
            }

            if (ViewModel?.IsConnected == true && MaaTasker?.Controller?.IsConnected == true)
            {
                return;
            }

            if (ViewModel?.IsConnected == true && MaaTasker?.Controller?.IsConnected != true)
            {
                LoggerHelper.Warning("检测到 UI 连接状态与底层控制器状态不一致，准备重新建立连接。");
                ViewModel.SetConnected(false);
            }

            // Android interfaces commonly declare an ADB controller for compatibility,
            // but the registered platform factory owns the complete connection lifecycle.
            var isPlatformController = PlatformControllerFactory.CanInitializeWithoutDevice;
            var isAdb = controllerType == MaaControllerTypes.Adb && !isPlatformController;
            var isPlayCover = controllerType == MaaControllerTypes.PlayCover;
            var strictRecoveryTarget = isAdb && ShouldStrictMatchSavedAdbTarget();
            ViewModel?.SetAdbRecoverySelectionLock(strictRecoveryTarget);
            var targetKey = controllerType switch
            {
                MaaControllerTypes.Adb when !isPlatformController => LangKeys.Emulator,
                MaaControllerTypes.Win32 => LangKeys.Window,
                MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
                _ => LangKeys.Window
            };
            var beforeTask = InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
            var delayFingerprintMatching = beforeTask.Contains("StartupSoftware", StringComparison.OrdinalIgnoreCase);

            if (showMessage)
                AddLogByKey(LangKeys.ConnectingTo, (IBrush?)null, true, true, targetKey);
            else
                ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.ConnectingTo.ToLocalizationFormatted(true, targetKey));

            if (isAdb)
            {
                await EnsureAdbTargetReadyAsync(token, showMessage, delayFingerprintMatching);
            }

            // A Win32 HWND is only valid for the lifetime of its window. If the
            // selected window was closed and recreated, rediscover it before
            // creating a controller instead of passing the stale handle on.
            if (controllerType == MaaControllerTypes.Win32
                && ViewModel is { CurrentDevice: not null }
                && !ViewModel.IsCurrentWin32WindowValid())
            {
                ViewModel.InvalidateCurrentWin32Window();
                await Task.Run(() => ViewModel.AutoDetectDevice(token, showToast: false), token);
            }

            if (!isPlatformController && !isPlayCover && ViewModel?.CurrentDevice == null && InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true) && !delayFingerprintMatching)
                ViewModel?.TryReadAdbDeviceFromConfig(false, true, true, false, strictRecoveryTarget);

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
                            ViewModel?.TryReadAdbDeviceFromConfig(false, true, true, false);
                    });
            }

            if (!connected)
            {
                _suppressConnectionAttemptErrorToast = previousSuppressConnectionAttemptErrorToast;
                if (!tuple.Item2 && shouldRetry)
                    HandleConnectionFailureAsync(controllerType, token);
                var connectionException = new InvalidOperationException(ConnectionFailedAfterAllRetriesMessage);
                connectionException.Data["controller.type"] = controllerType.ToString();
                // Exhausted connection retries are a user/device state, not an application
                // exception. Keep the task failure detail without creating a Sentry issue.
                TelemetryService.RecordTaskFailure(InstanceId, "connection_failed", "connection", "retries_exhausted");
                throw connectionException;
            }

            if (isAdb)
            {
                ViewModel?.SetAdbRecoverySelectionLock(false);
                ViewModel?.SyncCurrentAdbSelectionToActiveConfig();
            }

            ViewModel?.SetConnected(true);
        }
        finally
        {
            ViewModel?.SetAdbRecoverySelectionLock(false);
            _suppressConnectionAttemptErrorToast = previousSuppressConnectionAttemptErrorToast;
            Interlocked.Exchange(ref _isConnecting, 0);
            _connectionGate.Release();
        }
    }

    private async Task EnsureAdbTargetReadyAsync(CancellationToken token, bool showMessage, bool delayFingerprintMatching)
    {
        if (ViewModel == null)
            return;

        var strictRecoveryTarget = ShouldStrictMatchSavedAdbTarget();

        if (InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true) && !delayFingerprintMatching)
        {
            ViewModel.TryReadAdbDeviceFromConfig(false, true, true, false, strictRecoveryTarget);
            if (!string.IsNullOrWhiteSpace(Config.AdbDevice.AdbSerial))
                return;
        }

        var hasValidAdbSerial = !string.IsNullOrWhiteSpace(Config.AdbDevice.AdbSerial);
        if (hasValidAdbSerial)
            return;

        if (InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true) && !delayFingerprintMatching)
        {
            ViewModel.TryReadAdbDeviceFromConfig(false, true, true, false, strictRecoveryTarget);
            hasValidAdbSerial = !string.IsNullOrWhiteSpace(Config.AdbDevice.AdbSerial);
            if (hasValidAdbSerial)
                return;
        }

        if (!InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnected, false))
            return;

        if (!CanStartSoftware(out var reason))
        {
            LoggerHelper.Warning($"连接前跳过自动启动模拟器：{reason}");
            return;
        }

        LoggerHelper.Info("ADB 连接目标为空，首次连接前尝试先启动模拟器。");
        await RetryConnectionAsync(token, showMessage, StartSoftware, LangKeys.TryToStartEmulator, true, () =>
        {
            if (InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true))
                ViewModel.TryReadAdbDeviceFromConfig(false, true, true, false, true);
        });
    }

    async private Task<bool> HandleAdbConnectionAsync(CancellationToken token, bool showMessage = true)
    {
        bool connected = false;
        var strictRecoveryTarget = ShouldStrictMatchSavedAdbTarget();
        var retrySteps = new List<Func<CancellationToken, Task<bool>>>
        {
            async t =>
            {
                if (!InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnected, false))
                    return false;

                if (!CanStartSoftware(out var reason))
                {
                    LoggerHelper.Warning($"ADB 连接失败后跳过自动启动模拟器：{reason}");
                    return false;
                }

                LoggerHelper.Warning("ADB 连接失败，正在尝试启动模拟器并刷新设备目标。");
                return await RetryConnectionAsync(t, showMessage, StartSoftware, LangKeys.TryToStartEmulator, true,
                    () => ViewModel?.TryReadAdbDeviceFromConfig(false, true,
                        InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true), false, strictRecoveryTarget));
            },
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

    private bool ShouldStrictMatchSavedAdbTarget()
    {
        return (ViewModel?.CurrentController ?? MaaControllerTypes.Adb) == MaaControllerTypes.Adb
            && InstanceConfiguration.GetValue(ConfigurationKeys.RememberAdb, true)
            && InstanceConfiguration.TryGetValue(ConfigurationKeys.AdbDevice, out AdbDeviceInfo _,
                new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>())
            && InstanceConfiguration.GetValue(ConfigurationKeys.RetryOnDisconnected, false)
            && CanStartSoftware(out _);
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
            LoggerHelper.Info("处理连接失败时发现令牌已取消，跳过 Stop 调用。");
            return;
        }
        AddLogByKey(LangKeys.ConnectFailed, (IBrush?)null);
        ViewModel?.SetConnected(false);
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
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
                    return await TryRunTasksAsync(MaaTasker, task.Entry, task.Param, token);
                }, task.Count ?? 1, task.SourceItem, task.RunId
            ));
        }
    }

    async private Task<MaaJobStatus> TryRunTasksAsync(MaaTasker? maa, string? task, string? param, CancellationToken token)
    {
        if (maa == null || task == null) return MaaJobStatus.Invalid;

        var job = maa.AppendTask(task, param ?? "{}");
        TelemetryService.SetActiveTaskId(InstanceId, job.Id);
        return await TaskManager.RunTaskAsync(() => job.Wait(), token, (ex) => throw ex,
            name: "队列任务", catchException: true, shouldLog: false);
    }

    async private Task RunScript(string str = "Prescript")
    {
        await ScriptRunner.RunScriptAsync(str, InstanceConfiguration);
    }

    private void AddPostTasksAsync(bool onlyStart, bool checkUpdate, CancellationToken token)
    {
        if (!onlyStart)
        {
            TaskQueue.Enqueue(CreateMFATask(LangKeys.Postscript.ToLocalization(), async () =>
            {
                await TaskManager.RunTaskAsync(async () => await RunScript("Post-script"), token: token, name: "启动附加结束脚本");
            }));
        }
        if (checkUpdate)
        {
            TaskQueue.Enqueue(CreateMFATask(LangKeys.CheckUpdate.ToLocalization(), async () =>
            {
                await VersionChecker.CheckAsync();
            }, isUpdateRelated: true));
        }
    }

    private MFATask CreateMaaFWTask(string? name, Func<Task<MaaJobStatus>> action, int count = 1,
        DragItemViewModel? sourceItem = null, long runId = 0)
    {
        return new MFATask
        {
            Name = name,
            Count = count,
            Type = MFATask.MFATaskType.MAAFW,
            MaaAction = action,
            OwnerViewModel = ViewModel,
            SourceItem = sourceItem,
            RunId = runId,
            ContinueOnError = InstanceConfiguration.GetValue(ConfigurationKeys.ContinueRunningWhenError, true)
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

    public void Stop(MFATask.MFATaskStatus status, bool finished = false, bool onlyStart = false,
        Action? action = null, bool queueCompleted = false)
    {
        EnqueueCommand(() => StopInternal(status, finished, onlyStart, queueCompleted, action));
    }

    private Task StopInternal(MFATask.MFATaskStatus status, bool finished, bool onlyStart,
        bool queueCompleted, Action? action)
    {
        using var logScope = BeginInstanceLogScope("StopTask", "Worker");
        ResetActionFailedCount();
        ClearTaskbarProgress();
        _taskQueueTotal = 0;

        lock (_stopLock)
        {
            LoggerHelper.Info("停止前状态：" + Status);
            if (Status == MFATask.MFATaskStatus.STOPPING)
                return Task.CompletedTask;
            Status = MFATask.MFATaskStatus.STOPPING;
            ViewModel?.SetAdbRecoverySelectionLock(false);
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
                            LoggerHelper.Info($"正在尝试停止任务执行器，第 {i + 1} 次。");
                            stopResult = AbortCurrentTasker();
                            LoggerHelper.Info($"第 {i + 1} 次停止任务执行器返回：{stopResult}，准备重试。");

                            if (stopResult == MaaJobStatus.Succeeded)
                                break;

                            await Task.Delay(retryDelayMs);
                        }

                    }
                    HandleStopResult(status, stopResult, onlyStart, queueCompleted, action, isUpdateRelated);
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

    private void HandleStopResult(MFATask.MFATaskStatus status, MaaJobStatus success, bool onlyStart,
        bool queueCompleted = false, Action? action = null, bool isUpdateRelated = false)
    {
        if (success == MaaJobStatus.Succeeded)
        {
            DisplayTaskCompletionMessage(status, onlyStart, queueCompleted, action);
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

    private void DisplayTaskCompletionMessage(MFATask.MFATaskStatus status, bool onlyStart = false,
        bool queueCompleted = false, Action? action = null)
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

            if (!onlyStart)
                HandleAfterTaskOperation();
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
            case "ShutDownOnce":
                InstanceConfiguration.SetValue(ConfigurationKeys.AfterTask, "None");
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
        LoggerHelper.Error($"停止操作失败：{ex.Message}");
        ToastHelper.Error(LangKeys.StoppingFailed.ToLocalization());
    }

    #endregion

    #region 启动软件

    public async Task WaitSoftware()
    {
        var beforeTask = InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
        var isStartupMode = beforeTask.Contains("Startup", StringComparison.OrdinalIgnoreCase);

        if (isStartupMode)
        {
            await StartSoftware();
        }

        if (ViewModel?.IsConnected != true)
        {
            var allowAutoDetect = InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true);
            ViewModel?.TryReadAdbDeviceFromConfig(false, false, allowAutoDetect, false, isStartupMode);
        }
    }
    private CancellationTokenSource? _emulatorCancellationTokenSource;
    private static Process? _softwareProcess;

    public async Task StartSoftware()
    {
        _emulatorCancellationTokenSource = new CancellationTokenSource();
        await StartRunnableFile(InstanceConfiguration.GetValue(ConfigurationKeys.SoftwarePath, string.Empty),
            InstanceConfiguration.GetValue(ConfigurationKeys.WaitSoftwareTime, 60.0), _emulatorCancellationTokenSource.Token);
    }

    private bool CanStartSoftware(out string reason)
    {
        var exePath = InstanceConfiguration.GetValue(ConfigurationKeys.SoftwarePath, string.Empty);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            reason = "SoftwarePath is empty";
            return false;
        }

        if (!File.Exists(exePath))
        {
            reason = $"SoftwarePath does not exist: {exePath}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    async private Task StartRunnableFile(string exePath, double waitTimeInSeconds, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            LoggerHelper.Warning("已跳过启动程序，因为 SoftwarePath 为空。");
            return;
        }

        if (!File.Exists(exePath))
        {
            LoggerHelper.Warning($"已跳过启动程序，因为文件不存在：{exePath}");
            return;
        }

        if (OperatingSystem.IsWindows() && exePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveShortcut(exePath);
            if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
            {
                LoggerHelper.Info($"快捷方式解析结果：{exePath} -> {resolved}");
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

        await WaitForStartupConnectionWindowAsync(waitTimeInSeconds, token);
    }

    private async Task WaitForStartupConnectionWindowAsync(double waitTimeInSeconds, CancellationToken token)
    {
        var connectionWindow = TimeSpan.FromSeconds(Math.Max(0, waitTimeInSeconds));
        if (connectionWindow <= TimeSpan.Zero)
            return;

        var targetKey = (ViewModel?.CurrentController ?? MaaControllerTypes.Adb) switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
            _ => LangKeys.Window
        };
        var deadline = DateTime.UtcNow + connectionWindow;
        var attempt = 0;
        var previousSuppressConnectionAttemptErrorToast = _suppressConnectionAttemptErrorToast;

        AddLogByKey(LangKeys.WaitSoftwareTime, (IBrush?)null, true, true, targetKey, Math.Ceiling(connectionWindow.TotalSeconds).ToString("0"));

        _suppressConnectionAttemptErrorToast = true;

        try
        {
            while (DateTime.UtcNow <= deadline)
            {
                token.ThrowIfCancellationRequested();
                attempt++;

                if (ViewModel?.CurrentController != MaaControllerTypes.PlayCover)
                {
                    ViewModel?.TryReadAdbDeviceFromConfig(false, true,
                        InstanceConfiguration.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true), false, true);
                }

                var tuple = await TryConnectAsync(token);
                if (tuple.Item1)
                {
                    ViewModel?.SetConnected(true);
                    AddLogByKey(LangKeys.StartupConnectSucceededDelay, (IBrush?)null, true, true, targetKey);
                    LoggerHelper.Info($"启动后连接在第 {attempt} 次尝试时成功，继续执行前等待 5 秒。");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    return;
                }

                if (!tuple.Item3)
                {
                    LoggerHelper.Warning($"启动后连接在第 {attempt} 次尝试后停止重试，因为当前错误不可重试。");
                    return;
                }

                var remainingSeconds = Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
                if (attempt == 1 || remainingSeconds <= 3 || remainingSeconds % 5 == 0)
                {
                    AddLogByKey(LangKeys.StartupConnectRetrying, (IBrush?)null, true, true, targetKey, remainingSeconds.ToString());
                }
                LoggerHelper.Info($"启动后连接第 {attempt} 次尝试失败，剩余重试窗口：{remainingSeconds} 秒。");

                if (DateTime.UtcNow >= deadline)
                    break;

                await Task.Delay(1000, token);
            }
        }
        finally
        {
            _suppressConnectionAttemptErrorToast = previousSuppressConnectionAttemptErrorToast;
        }

        LoggerHelper.Warning($"启动后连接等待窗口已超时：{Math.Max(0, waitTimeInSeconds):0.#} 秒。");
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
            LoggerHelper.Warning($"解析快捷方式失败：{ex.Message}");
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
                LoggerHelper.Warning($"控制器需要管理员权限，但当前进程未提权：controller={controllerType}, permissionRequired=true");
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
            tasker.Resource.Register(new Custom.HeiLiuAction((content, color) => AddLog(content, color)));
            tasker.Resource.Register(new Custom.SwitchInstanceAction(this));
            LoggerHelper.Info("已注册内置特殊任务动作。");

            // 获取当前资源的自定义目录
            var currentResource = ViewModel?.CurrentResources
                .FirstOrDefault(c => c.Name == ViewModel?.CurrentResource);
            var originalPaths = currentResource?.ResolvedPath ?? currentResource?.Path;

            if (originalPaths == null || originalPaths.Count == 0)
            {
                LoggerHelper.Info("未找到资源路径，跳过自定义类加载。");
                return;
            }

            // 创建副本，避免修改原始列表
            var resourcePaths = new List<string>(originalPaths);
            // LoggerHelper.Info(LangKeys.RegisteringCustomRecognizer.ToLocalization());
            // LoggerHelper.Info(LangKeys.RegisteringCustomAction.ToLocalization());
            resourcePaths.Add(AppPaths.ResourceDirectory);
            // 遍历所有资源路径，查找 custom 目录
            foreach (var resourcePath in resourcePaths)
            {
                var customDir = Path.Combine(resourcePath, "custom");
                if (!Directory.Exists(customDir))
                {
                    LoggerHelper.Info($"未找到自定义目录：{customDir}");
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
                            LoggerHelper.Info($"已注册自定义识别器：{customClass.Name}");
                        }
                        else if (customClass.Value is IMaaCustomAction action)
                        {
                            tasker.Resource.Register(action);
                            LoggerHelper.Info($"已注册自定义动作：{customClass.Name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"注册自定义类失败：{customClass.Name}，原因：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"注册自定义识别器或动作时发生错误：{ex.Message}");
        }
    }

    #endregion
}
