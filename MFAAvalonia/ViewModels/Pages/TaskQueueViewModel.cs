using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.UsersControls;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.Views.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SukiUI.Dialogs;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.Pages;

public partial class TaskQueueViewModel : ViewModelBase, IDisposable
{
    private readonly MaaProcessor _processorField;
    private readonly DispatcherTimer _taskRunElapsedTimer;
    private long _taskRunId;
    private string? _savedControllerName;
    public MaaProcessor Processor => _processorField;

    public TaskQueueViewModel() : this(MaaProcessorManager.Instance.Current.InstanceId)
    {
    }

    public TaskQueueViewModel(string instanceId)
    {
        _processorField = new MaaProcessor(instanceId);
        _currentController = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.CurrentController, MaaControllerTypes.Adb, MaaControllerTypes.None, new UniversalEnumConverter<MaaControllerTypes>());
        _savedControllerName = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.CurrentControllerName, string.Empty);
        // 初始化为当前控制器类型，避免首次 AutoDetectDevice 时用 interface.json 覆盖用户已保存的配置
        var hasSavedControllerSettings = new[]
        {
            ConfigurationKeys.AdbControlInputType,
            ConfigurationKeys.AdbControlScreenCapType,
            ConfigurationKeys.Win32ControlMouseType,
            ConfigurationKeys.Win32ControlKeyboardType,
            ConfigurationKeys.Win32ControlScreenCapType,
            ConfigurationKeys.MacOSControlInputType,
            ConfigurationKeys.MacOSControlScreenCapType,
            ConfigurationKeys.GamepadControlScreenCapType,
            ConfigurationKeys.GamepadType
        }.Any(_processorField.InstanceConfiguration.ContainsKey);
        // 已有用户配置时，启动阶段不使用 interface.json 覆盖；无配置时允许首次初始化。
        _lastAppliedControllerSettingsKey = hasSavedControllerSettings
            ? (_currentController, NormalizeControllerName(_savedControllerName))
            : null;
        // 提前从配置读取资源，避免 Initialize() 中 UpdateResourcesForController 以空字符串调用时
        // 走 else 分支将第一个资源写入配置，覆盖用户已保存的资源选择
        _currentResource = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.Resource, string.Empty);
        _enableLiveView = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.EnableLiveView, true);
        _liveViewRefreshRate = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.LiveViewRefreshRate, 30.0);

        // Initialize LiveView Timer
        _liveViewTimer = new System.Timers.Timer();
        _liveViewTimer.Elapsed += OnLiveViewTimerElapsed;
        UpdateLiveViewTimerInterval();
        _liveViewTimer.Start();

        _taskRunElapsedTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _taskRunElapsedTimer.Tick += OnTaskRunElapsedTimerTick;

        IsRunning = _processorField.TaskQueue.Count > 0;
        _processorField.TaskQueue.CountChanged += OnTaskQueueCountChanged;
        LanguageHelper.LanguageChanged += OnLanguageChanged;

        // Re-initialize with the correct processor since base constructor might have used Current
        Initialize();
    }

    private string InstanceName => MaaProcessorManager.Instance.GetInstanceName(Processor.InstanceId);

    private IDisposable BeginUiLogScope(string operation)
    {
        return LoggerHelper.PushContext(
            source: "UI",
            operation: operation,
            instanceId: Processor.InstanceId,
            instanceName: InstanceName);
    }

    private void LogUiStateChange(string operation, string message)
    {
        using var _ = BeginUiLogScope(operation);
        LoggerHelper.Info(message);
    }

    private void LogStartBlocked(string reason)
    {
        LoggerHelper.Warning($"操作被拒绝：{reason} | {DescribeCurrentSelection()}");
    }

    private static string DescribeAdbDeviceInfo(AdbDeviceInfo info)
    {
        var configLength = string.IsNullOrWhiteSpace(info.Config) ? 0 : info.Config.Length;
        return $"name={info.Name}, serial={info.AdbSerial}, configLength={configLength}";
    }

    private string DescribeCurrentSelection()
    {
        var controller = OperatingSystem.IsAndroid()
            ? $"ShizukuNative (interface={CurrentController})"
            : CurrentController.ToString();
        var resource = string.IsNullOrWhiteSpace(CurrentResource) ? "<none>" : CurrentResource;
        var device = CurrentDevice switch
        {
            AdbDeviceInfo adb => $"{adb.Name} ({adb.AdbSerial})",
            DesktopWindowInfo win => $"{win.Name} (0x{win.Handle.ToInt64():X})",
            MacOSWindowInfo macOS => $"{macOS.Name} ({macOS.WindowId})",
            WlRootsSocketInfo wlRoots => wlRoots.SocketPath,
            null => "<none>",
            _ => CurrentDevice.ToString() ?? "<unknown>"
        };

        return $"controller={controller}, resource={resource}, device={device}";
    }

    private string DescribeSelectedTasks()
    {
        var selectedTasks = TaskItemViewModels
            .Where(task => !task.IsResourceOptionItem && (task.IsChecked || task.IsCheckedWithNull == null))
            .Select(task => task.InterfaceItem?.Name ?? task.InterfaceItem?.Entry ?? "<unnamed>")
            .ToList();

        return $"selectedTasks={selectedTasks.Count} [{string.Join(", ", selectedTasks)}]";
    }

    private void OnTaskQueueCountChanged(object? sender, ObservableQueue<MFATask>.CountChangedEventArgs e)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            var stopRequested = Processor.CancellationTokenSource?.IsCancellationRequested == true;
            IsRunning = e.NewValue > 0 || (Processor.IsTaskRunActive && !stopRequested);
        });
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Idle))]
    private bool _isRunning;

    public bool Idle => !IsRunning;

    public long BeginTaskRun(IReadOnlyCollection<DragItemViewModel> tasks)
    {
        return DispatcherHelper.RunOnMainThread(() =>
        {
            var runId = Interlocked.Increment(ref _taskRunId);
            _taskRunElapsedTimer.Stop();

            foreach (var item in TaskItemViewModels)
            {
                item.RunId = runId;
                item.RunState = TaskRunState.None;
                item.RunElapsed = TimeSpan.Zero;
                item.RunStartedAt = null;
                item.CompletedRunCount = 0;
                item.TotalRunCount = 0;
                item.RunErrorMessage = null;
            }

            foreach (var item in tasks.Where(item => !item.IsResourceOptionItem))
            {
                item.RunId = runId;
                item.RunState = TaskRunState.Queued;
                item.TotalRunCount = item.InterfaceItem?.Repeatable == true
                    ? item.InterfaceItem.RepeatCount ?? 1
                    : 1;
            }

            PublishPlatformRunProgress(runId, LangKeys.Running.ToLocalization());
            return runId;
        });
    }

    public void MarkTaskRunning(DragItemViewModel? item, long runId)
    {
        UpdateTaskRunState(item, runId, task =>
        {
            if (task.RunState == TaskRunState.Running)
                return;

            task.RunState = TaskRunState.Running;
            task.RunStartedAt = DateTimeOffset.UtcNow - task.RunElapsed;
            _taskRunElapsedTimer.Start();
            PublishPlatformRunProgress(runId, LangKeys.Running.ToLocalization());
        });
    }

    public void MarkTaskIterationCompleted(DragItemViewModel? item, long runId)
    {
        UpdateTaskRunState(item, runId, task =>
        {
            task.CompletedRunCount++;
            PublishPlatformRunProgress(runId, LangKeys.Running.ToLocalization());
        });
    }

    public void MarkTaskSucceeded(DragItemViewModel? item, long runId)
    {
        CompleteTaskRun(item, runId, TaskRunState.Succeeded, null);
    }

    public void MarkTaskFailed(DragItemViewModel? item, long runId, string? errorMessage = null)
    {
        CompleteTaskRun(item, runId, TaskRunState.Failed, errorMessage);
    }

    public void MarkTaskStopped(DragItemViewModel? item, long runId)
    {
        CompleteTaskRun(item, runId, TaskRunState.Stopped, null);
    }

    public void FinalizeTaskRun(long runId)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            foreach (var item in TaskItemViewModels.Where(item => item.RunId == runId))
            {
                if (item.RunState == TaskRunState.Queued)
                    item.RunState = TaskRunState.Skipped;
                else if (item.RunState == TaskRunState.Running)
                    CompleteTaskRunCore(item, TaskRunState.Stopped, null);
            }

            StopTaskRunElapsedTimerIfIdle();
            PlatformRunProgress.Stop?.Invoke(Processor.InstanceId);
        });
    }

    private void PublishPlatformRunProgress(long runId, string state)
    {
        if (!OperatingSystem.IsAndroid() || runId <= 0)
            return;
        var runTasks = TaskItemViewModels
            .Where(item => item.RunId == runId && !item.IsResourceOptionItem)
            .ToList();
        var completed = runTasks.Count(item => item.RunState is
            TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Stopped or TaskRunState.Skipped);
        var current = runTasks.FirstOrDefault(item => item.RunState == TaskRunState.Running);
        var currentName = current?.InterfaceItem?.Name ?? current?.Name ?? CurrentTaskName ?? string.Empty;
        PlatformRunProgress.Update?.Invoke(new RunProgressSnapshot(
            Processor.InstanceId,
            MaaProcessor.Interface?.Name ?? "MFA",
            state,
            LanguageHelper.GetLocalizedString(currentName),
            completed,
            runTasks.Count,
            runTasks.Count == 0));
    }

    private void CompleteTaskRun(DragItemViewModel? item, long runId, TaskRunState state, string? errorMessage)
    {
        UpdateTaskRunState(item, runId, task =>
        {
            CompleteTaskRunCore(task, state, errorMessage);
            StopTaskRunElapsedTimerIfIdle();
            PublishPlatformRunProgress(runId, LangKeys.Running.ToLocalization());
        });
    }

    private static void CompleteTaskRunCore(DragItemViewModel item, TaskRunState state, string? errorMessage)
    {
        if (item.RunStartedAt is { } startedAt)
            item.RunElapsed = DateTimeOffset.UtcNow - startedAt;

        item.RunStartedAt = null;
        item.RunErrorMessage = errorMessage;
        item.RunState = state;
    }

    private void UpdateTaskRunState(DragItemViewModel? item, long runId, Action<DragItemViewModel> update)
    {
        if (item == null)
            return;

        DispatcherHelper.RunOnMainThread(() =>
        {
            if (item.RunId == runId && runId == Volatile.Read(ref _taskRunId))
                update(item);
        });
    }

    private void OnTaskRunElapsedTimerTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var hasRunningTask = false;
        foreach (var item in TaskItemViewModels)
        {
            if (item.RunState != TaskRunState.Running || item.RunStartedAt is not { } startedAt)
                continue;

            item.RunElapsed = now - startedAt;
            hasRunningTask = true;
        }

        if (!hasRunningTask)
            _taskRunElapsedTimer.Stop();
    }

    private void StopTaskRunElapsedTimerIfIdle()
    {
        if (TaskItemViewModels.All(item => item.RunState != TaskRunState.Running))
            _taskRunElapsedTimer.Stop();
    }

    [ObservableProperty] private bool _isCompactMode = false;

    private bool _isSyncing = false;

    /// <summary>
    /// 标记当前 CurrentDevice 变更是否为程序内部触发（刷新/配置加载等），
    /// 为 false 时表示用户通过 ComboBox 手动选择设备
    /// </summary>
    private bool _suppressAutoConnect = false;
    private bool _lockCurrentAdbSelectionDuringRecovery = false;
    private bool _suppressDeviceSelectionToast = false;

    /// <summary>
    /// 记录已应用过 interface.json 控制器设置的控制器类型和名称，
    /// 避免每次刷新设备时都用 interface.json 的值覆盖用户配置
    /// </summary>
    private (MaaControllerTypes Type, string? Name)? _lastAppliedControllerSettingsKey;

    // 竖屏模式下的设置弹窗状态
    [ObservableProperty] private bool _isSettingsPopupOpen = false;

    partial void OnIsCompactModeChanged(bool value)
    {
        if (!value && IsSettingsPopupOpen)
            IsSettingsPopupOpen = false;
    }

    [RelayCommand]
    private void CloseSettingsPopup()
    {
        IsSettingsPopupOpen = false;
    }
    /// <summary>
    /// 在竖屏模式下打开设置弹窗
    /// </summary>
    public void OpenSettingsPopup()
    {
        if (IsCompactMode)
        {
            IsSettingsPopupOpen = true;
        }
    }
    /// <summary>
    /// 控制器选项列表
    /// </summary>
    [ObservableProperty] private ObservableCollection<MaaInterface.MaaResourceController> _controllerOptions = [];

    /// <summary>
    /// 当前选中的控制器
    /// </summary>
    [ObservableProperty] private MaaInterface.MaaResourceController? _selectedController;

    partial void OnSelectedControllerChanged(MaaInterface.MaaResourceController? value)
    {
        if (value == null) return;

        _savedControllerName = value.Name;
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentControllerName, value.Name ?? string.Empty);

        if (value.ControllerType != CurrentController)
        {
            CurrentController = value.ControllerType;
        }
    }

    /// <summary>
    /// 获取当前控制器的名称
    /// </summary>
    public string? GetCurrentControllerName()
    {
        return SelectedController?.Name ?? _savedControllerName ?? TaskLoader.GetControllerName(CurrentController, MaaProcessor.Interface);
    }

    public void UpdateResourcesForController(string? targetResource = null)
    {
        try
        {
            if (MaaProcessor.Interface == null)
            {
                LoggerHelper.Warning("界面资源接口尚未初始化完成。");
                // Android 上 Interface 可能在页面初始化后短暂尚未就绪。
                // 保留已有列表，避免下拉框仍显示旧值但启动校验认为没有选择资源。
                return;
            }

            var allResources = MaaProcessor.Interface.Resources.Values.ToList();
            if (allResources.Count == 0)
            {
                allResources =
                [
                    new()
                    {
                        Name = "Default",
                        Path = [MaaProcessor.ResourceBase]
                    }
                ];
            }

            var currentControllerName = GetCurrentControllerName();
            var filteredResources = TaskLoader.FilterResourcesByController(allResources, currentControllerName);

            foreach (var resource in filteredResources)
            {
                resource.InitializeDisplayName();
                TaskLoader.InitializeResourceSelectOptions(resource, MaaProcessor.Interface, Processor.InstanceConfiguration);
            }

            var resourceToSelect = targetResource ?? CurrentResource;
            var nextResources = new ObservableCollection<MaaInterface.MaaInterfaceResource>(filteredResources);
            var matchedResource = nextResources.FirstOrDefault(r =>
                string.Equals(r.Name?.Trim(), resourceToSelect?.Trim(), StringComparison.OrdinalIgnoreCase));
            var resolvedResource = matchedResource?.Name ?? nextResources.FirstOrDefault()?.Name ?? "Default";

            DispatcherHelper.RunOnMainThread(() =>
            {
                _isRefreshingResourceSelection = true;
                _pendingResourceSelection = resolvedResource;
                try
                {
                    CurrentResources = nextResources;
                    CurrentResource = resolvedResource;
                }
                finally
                {
                    _isRefreshingResourceSelection = false;
                    _pendingResourceSelection = null;
                }

                if (!string.Equals(CurrentResource, resolvedResource, StringComparison.Ordinal))
                {
                    CurrentResource = resolvedResource;
                }
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"按控制器刷新资源列表失败：原因={ex.Message}", ex);
        }
    }


    /// <summary>
    /// 初始化控制器列表
    /// 从MaaInterface.Controller加载，如果为空则使用默认的Adb和Win32
    /// </summary>
    public void InitializeControllerOptions()
    {
        try
        {
            ObservableCollection<MaaInterface.MaaResourceController> nextControllerOptions;
            var controllers = MaaProcessor.Interface?.Controller;
            if (controllers is { Count: > 0 })
            {
                var filteredControllers = controllers
                    .Where(IsControllerSupportedOnCurrentSystem)
                    .ToList();

                if (filteredControllers.Count > 0)
                {
                    // 从interface配置中加载控制器列表
                    foreach (var controller in filteredControllers)
                    {
                        controller.InitializeDisplayName();
                    }
                    nextControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(filteredControllers);
                }
                else
                {
                    // 过滤后为空则使用默认控制器
                    var defaultControllers = CreateDefaultControllers();
                    nextControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
                }
            }
            else
            {
                // 使用默认的Adb和Win32控制器
                var defaultControllers = CreateDefaultControllers();
                nextControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
            }

            DispatcherHelper.RunOnMainThread(() =>
            {
                ControllerOptions = nextControllerOptions;

                var targetController = ResolveTargetController(CurrentController);
                SelectedController = targetController;
                _isSyncing = false;
            });
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"初始化控制器选项失败：控制器={CurrentController}，原因={e.Message}", e);
            // 出错时使用默认控制器
            var defaultControllers = CreateDefaultControllers();
            DispatcherHelper.RunOnMainThread(() =>
            {
                ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
                SelectedController = ControllerOptions.FirstOrDefault();
                _isSyncing = false;
            });
        }
    }

    /// <summary>
    /// 判断控制器是否支持当前系统
    /// </summary>
    private static bool IsControllerSupportedOnCurrentSystem(MaaInterface.MaaResourceController controller)
    {
        var type = controller.Type ?? string.Empty;

        if (type.Contains("win32", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows();

        if (type.Contains("gamepad", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsWindows();

        if (type.Contains("playcover", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsMacOS();

        if (type.Contains("macos", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsMacOS();

        if (type.Contains("wlroots", StringComparison.OrdinalIgnoreCase))
            return OperatingSystem.IsLinux();

        return true;
    }

    /// <summary>
    /// 创建默认的Adb和Win32控制器
    /// </summary>
    private List<MaaInterface.MaaResourceController> CreateDefaultControllers()
    {
        var adbController = new MaaInterface.MaaResourceController
        {
            Name = "Adb",
            Type = MaaControllerTypes.Adb.ToJsonKey()
        };
        adbController.InitializeDisplayName();
        List<MaaInterface.MaaResourceController> controllers = [adbController];
        if (OperatingSystem.IsWindows())
        {
            var win32Controller = new MaaInterface.MaaResourceController
            {
                Name = "Win32",
                Type = MaaControllerTypes.Win32.ToJsonKey()
            };
            win32Controller.InitializeDisplayName();
            controllers.Add(win32Controller);

            var gamePadController = new MaaInterface.MaaResourceController
            {
                Name = "Gamepad",
                Type = MaaControllerTypes.Gamepad.ToJsonKey()
            };
            gamePadController.InitializeDisplayName();
            controllers.Add(gamePadController);
        }
        if (OperatingSystem.IsMacOS())
        {
            var playCoverController = new MaaInterface.MaaResourceController
            {
                Name = "PlayCover",
                Type = MaaControllerTypes.PlayCover.ToJsonKey()
            };
            playCoverController.InitializeDisplayName();
            controllers.Add(playCoverController);

            var macOSController = new MaaInterface.MaaResourceController
            {
                Name = "MacOS",
                Type = MaaControllerTypes.MacOS.ToJsonKey()
            };
            macOSController.InitializeDisplayName();
            controllers.Add(macOSController);
        }
        if (OperatingSystem.IsLinux())
        {
            var wlRootsController = new MaaInterface.MaaResourceController
            {
                Name = "WlRoots",
                Type = MaaControllerTypes.WlRoots.ToJsonKey()
            };
            wlRootsController.InitializeDisplayName();
            controllers.Add(wlRootsController);
        }
        return controllers;
    }

    protected override void Initialize()
    {
        if (_processorField == null) return;
        try
        {
            _isSyncing = true;
            InitializeControllerOptions();
            UpdateResourcesForController(CurrentResource);
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"同步控制器与资源状态失败：控制器={CurrentController}，资源={CurrentResource}，原因={e.Message}", e);
            _isSyncing = false;
        }
    }


    #region 介绍

    [ObservableProperty] private string _introduction = string.Empty;

    #endregion

    #region 任务

    [ObservableProperty] private bool _isCommon = true;
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _toggleEnable = true;

    [ObservableProperty] private ObservableCollection<DragItemViewModel> _taskItemViewModels = [];

    partial void OnTaskItemViewModelsChanged(ObservableCollection<DragItemViewModel> value)
    {
        if (ConfigurationManager.IsSwitching) return;
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, value.Where(model => !model.IsResourceOptionItem).Select(model => model.InterfaceItem).ToList());
    }

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning)
            StopTask();
        else
            StartTask();
    }

    public void StartTask()
    {
        using var _ = BeginUiLogScope("StartTask");
        LoggerHelper.UserAction("启动任务", $"{DescribeCurrentSelection()}, {DescribeSelectedTasks()}",
            operation: "StartTask", instanceId: Processor.InstanceId, instanceName: InstanceName);

        if (IsRunning)
        {
            ToastHelper.Warn(LangKeys.ConfirmExitTitle.ToLocalization());
            LoggerHelper.Warning(LangKeys.ConfirmExitTitle.ToLocalization());
            return;
        }

        if (CurrentResources.Count == 0 || string.IsNullOrWhiteSpace(CurrentResource)
            || CurrentResources.All(r => !string.Equals(r.Name?.Trim(), CurrentResource.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.ResourceNotSelected.ToLocalization());
            LogStartBlocked("启动任务被拒绝：未选择有效资源");
            return;
        }

        var beforeTask = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
        var skipDeviceCheck = beforeTask.Contains("StartupSoftware", StringComparison.OrdinalIgnoreCase)
            || Instances.ConnectSettingsUserControlModel.AutoDetectOnConnectionFailed
            || HasApplicablePreTask()
            || PlatformControllerFactory.CanInitializeWithoutDevice;

        if (!skipDeviceCheck)
        {
            if (CurrentController != MaaControllerTypes.PlayCover && CurrentDevice == null)
            {
                ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.DeviceNotSelected.ToLocalization());
                LogStartBlocked("启动任务被拒绝：未选择连接目标");
                return;
            }

            if (CurrentController == MaaControllerTypes.Adb
                && CurrentDevice is AdbDeviceInfo adbInfo
                && string.IsNullOrWhiteSpace(adbInfo.AdbSerial))
            {
                ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.AdbAddressEmpty.ToLocalization());
                LogStartBlocked("启动任务被拒绝：ADB 序列号为空");
                return;
            }
        }

        if (CurrentController == MaaControllerTypes.PlayCover
            && string.IsNullOrWhiteSpace(Processor.Config.PlayCover.PlayCoverAddress))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.PlayCoverAddressEmpty.ToLocalization());
            LogStartBlocked("启动任务被拒绝：PlayCover 地址为空");
            return;
        }

        // 验证所有已勾选任务的 input 选项
        var failedTasks = new List<string>();
        foreach (var task in TaskItemViewModels)
        {
            task.HasValidationError = false;
            if (!task.IsChecked) continue;

            var options = task.IsResourceOptionItem
                ? task.ResourceItem?.SelectOptions
                : task.InterfaceItem?.Option;
            if (options == null) continue;

            EnsureDefaultOptionValues(options);
            var error = ValidateOptionsRecursive(options);
            if (error != null)
            {
                task.HasValidationError = true;
                failedTasks.Add($"{task.Name}: {error}");
            }
        }

        if (failedTasks.Count > 0)
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), string.Join("\n", failedTasks));
            return;
        }

        if (PlatformControllerFactory.CanInitializeWithoutDevice && Processor.MaaTasker?.Controller == null)
            LoggerHelper.Info("平台控制器尚未初始化，将在任务启动时自动创建。");

        Processor.Start();
    }

    private bool HasApplicablePreTask()
    {
        var controllerName = GetCurrentControllerName();
        var resourceName = CurrentResource;
        return MaaProcessor.Interface?.PreTask?.Any(preTask =>
            (preTask.Controller is not { Count: > 0 }
             || preTask.Controller.Any(name => string.Equals(name, controllerName, StringComparison.OrdinalIgnoreCase)))
            && (preTask.Resource is not { Count: > 0 }
                || preTask.Resource.Any(name => string.Equals(name, resourceName, StringComparison.OrdinalIgnoreCase)))) == true;
    }

    public void StopTask(Action? action = null)
    {
        // Reflect the user's stop request immediately.  The processor still owns the
        // actual cancellation/cleanup and prevents another run from starting until its
        // task chain has unwound.
        IsRunning = false;
        Processor.Stop(MFATask.MFATaskStatus.STOPPED, action: action);
    }

    private static string? ValidateOptionsRecursive(IEnumerable<MaaInterface.MaaInterfaceSelectOption> options)
    {
        foreach (var selectOption in options)
        {
            if (string.IsNullOrEmpty(selectOption.Name)) continue;
            if (MaaProcessor.Interface?.Option?.TryGetValue(selectOption.Name, out var optDef) != true) continue;

            if (optDef.IsInput)
            {
                var result = optDef.ValidateAllInputs(selectOption.Data);
                if (!result.IsValid) return result.ErrorMessage;
            }

            if (selectOption.SubOptions is { Count: > 0 } && optDef.Cases != null)
            {
                var index = selectOption.Index ?? 0;
                if (index >= 0 && index < optDef.Cases.Count)
                {
                    var selectedCase = optDef.Cases[index];
                    if (selectedCase.Option is { Count: > 0 })
                    {
                        var activeNames = new HashSet<string>(selectedCase.Option);
                        var subError = ValidateOptionsRecursive(
                            selectOption.SubOptions.Where(s => activeNames.Contains(s.Name ?? string.Empty)));
                        if (subError != null) return subError;
                    }
                }
            }
        }
        return null;
    }

    private static void EnsureDefaultOptionValues(IEnumerable<MaaInterface.MaaInterfaceSelectOption>? options)
    {
        if (options == null) return;

        foreach (var option in options)
        {
            TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, option);
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        using var _ = BeginUiLogScope("SelectAllTasks");
        foreach (var task in TaskItemViewModels)
            task.IsChecked = true;
        LoggerHelper.UserAction("全选任务", $"taskCount={TaskItemViewModels.Count}",
            operation: "SelectAllTasks", instanceId: Processor.InstanceId, instanceName: InstanceName);
    }

    [RelayCommand]
    private void SelectNone()
    {
        using var _ = BeginUiLogScope("SelectNoneTasks");
        foreach (var task in TaskItemViewModels)
            task.IsChecked = false;
        LoggerHelper.UserAction("取消全选任务", $"taskCount={TaskItemViewModels.Count}",
            operation: "SelectNoneTasks", instanceId: Processor.InstanceId, instanceName: InstanceName);
    }

    [RelayCommand]
    private void AddTask()
    {
        using var _ = BeginUiLogScope("OpenAddTaskDialog");
        LoggerHelper.UserAction("打开添加任务对话框", DescribeCurrentSelection(),
            operation: "OpenAddTaskDialog", instanceId: Processor.InstanceId, instanceName: InstanceName);
        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.AdbEditor.ToLocalization()).WithViewModel(dialog => new AddTaskDialogViewModel(dialog, Processor.TasksSource, this)).TryShow();
    }

    /// <summary>
    /// 当前 interface 中定义的预设列表（可观察，Interface 变更时通过 RefreshPresets 刷新）
    /// </summary>
    [ObservableProperty] private List<MaaInterface.MaaInterfacePreset>? _presets;

    /// <summary>
    /// 是否有可用的预设（可观察，Interface 变更时通过 RefreshPresets 刷新）
    /// </summary>
    [ObservableProperty] private bool _hasPresets;

    /// <summary>
    /// 刷新预设列表（在 MaaProcessor.Interface 变更后调用）
    /// </summary>
    public void RefreshPresets()
    {
        var presets = MaaProcessor.Interface?.Preset;
        presets?.ForEach(p => p.InitializeDisplayName());
        Presets = presets;
        HasPresets = presets is { Count: > 0 };
    }

    [RelayCommand]
    private void ApplyPreset(MaaInterface.MaaInterfacePreset preset)
    {
        if (preset?.Task == null) return;
        using var _ = BeginUiLogScope("ApplyPreset");
        LoggerHelper.UserAction("应用预设",
            $"preset={preset.DisplayName ?? preset.Name}, taskCount={preset.Task.Count}",
            operation: "ApplyPreset",
            instanceId: Processor.InstanceId,
            instanceName: InstanceName);

        // 1. 根据预设中的任务列表重建任务顺序与数量：
        //    - 只保留预设中出现的任务（例如：默认 ABCD，预设只有 AB，则应用后只显示 AB 两个任务）
        //    - 保留全局资源设置项（IsResourceOptionItem）
        //    - 保留特殊任务（例如倒计时、自定义系统通知等）
        var resourceOptionItems = TaskItemViewModels
            .Where(t => t.IsResourceOptionItem)
            .ToList();

        var specialTasks = TaskItemViewModels
            .Where(t => !string.IsNullOrWhiteSpace(t.InterfaceItem?.Entry)
                && ViewModels.UsersControls.Settings.AddTaskDialogViewModel.SpecialActionNames.Contains(t.InterfaceItem.Entry!))
            .ToList();

        // 使用 TasksSource 作为模板，保证从 interface 原始定义克隆任务。
        // 这里按名称保留模板源，但应用时必须按预设项出现顺序逐个克隆，
        // 不能再按名称回查，否则同名任务（例如多个“自动出征”）会全部落到第一项上。
        var templateDict = Processor.TasksSource
            .Where(t => !string.IsNullOrEmpty(t.InterfaceItem?.Name))
            .GroupBy(t => t.InterfaceItem!.Name!)
            .ToDictionary(g => g.Key, g => g.First());

        var presetTaskBindings = new List<(MaaInterface.MaaInterfacePresetTask PresetTask, DragItemViewModel DragItem)>();

        // 重新构建 TaskItemViewModels
        TaskItemViewModels.Clear();

        // 先恢复资源选项项（保持原有顺序）
        foreach (var item in resourceOptionItems)
            TaskItemViewModels.Add(item);

        // 再按预设顺序添加任务项，并记录“预设项 -> 新克隆任务项”的一一映射
        foreach (var presetTask in preset.Task.Where(t => !string.IsNullOrEmpty(t.Name)))
        {
            if (!templateDict.TryGetValue(presetTask.Name!, out var templateVm)) continue;

            var cloned = templateVm.Clone();
            cloned.OwnerViewModel = this;
            TaskItemViewModels.Add(cloned);
            presetTaskBindings.Add((presetTask, cloned));
        }

        // 最后把特殊任务放到列表末尾
        foreach (var special in specialTasks)
            TaskItemViewModels.Add(special);

        foreach (var (presetTask, dragItem) in presetTaskBindings)
        {
            if (!string.IsNullOrWhiteSpace(presetTask.Label) && dragItem.InterfaceItem != null)
            {
                dragItem.InterfaceItem.Remark = presetTask.Label;
                dragItem.RefreshDisplayName();
            }

            // 设置勾选状态
            // PI v2.3.0: preset.task.enabled 可选且默认 true；应用 preset 时它覆盖 task.default_check。
            dragItem.IsCheckedWithNull = presetTask.Enabled ?? true;

            // 设置选项值
            if (presetTask.Option != null && dragItem.InterfaceItem?.Option != null)
            {
                foreach (var (optionName, optionValue) in presetTask.Option)
                {
                    // 先在顶级 Option 中按名称查找
                    var selectOption = dragItem.InterfaceItem.Option.FirstOrDefault(o => o.Name == optionName);

                    // 如果不是顶级选项，尝试作为子选项处理：
                    // 递归在 option 树里查找/创建目标子选项，支持多级嵌套子选项。
                    if (selectOption == null)
                    {
                        selectOption = FindOrCreateNestedPresetOption(dragItem.InterfaceItem.Option, optionName, presetTask.Name);
                    }

                    if (selectOption == null) continue;

                    if (MaaProcessor.Interface?.Option?.TryGetValue(optionName, out var interfaceOption) != true) continue;

                    if (interfaceOption.IsCheckbox)
                    {
                        // checkbox: string[] → SelectedCases
                        if (optionValue.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                        {
                            selectOption.SelectedCases = optionValue.ToObject<List<string>>() ?? new List<string>();
                        }
                        else if (optionValue.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        {
                            selectOption.SelectedCases = new List<string> { optionValue.Value<string>() ?? string.Empty };
                        }
                    }
                    else if (interfaceOption.IsInput)
                    {
                        // input: Dictionary<string, string> → Data
                        // 单输入项同时支持字符串简写："选项名": "值"
                        if (optionValue is Newtonsoft.Json.Linq.JObject jObj)
                        {
                            selectOption.Data ??= new Dictionary<string, string?>();
                            foreach (var prop in jObj.Properties())
                                selectOption.Data[prop.Name] = prop.Value.Value<string>();

                            RefreshPresetOptionRuntimeState(selectOption);
                        }
                        else if (interfaceOption.Inputs is { Count: 1 } singleInputs)
                        {
                            var inputName = singleInputs[0].Name;
                            if (!string.IsNullOrWhiteSpace(inputName))
                            {
                                selectOption.Data ??= new Dictionary<string, string?>();
                                var inputValue = optionValue.Type == JTokenType.String
                                    ? optionValue.Value<string>()
                                    : optionValue.ToString(Formatting.None);
                                selectOption.Data[inputName] = inputValue;
                                RefreshPresetOptionRuntimeState(selectOption);
                            }
                        }
                    }
                    else if (optionValue.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        // 支持完整的选项对象结构（含子选项），例如：
                        // { "index": 1, "selected_cases": [...], "data": {...}, "sub_options": [...] }
                        var fullOption = optionValue.ToObject<MaaInterface.MaaInterfaceSelectOption>();
                        if (fullOption != null)
                        {
                            selectOption.Index = fullOption.Index;
                            selectOption.SelectedCases = fullOption.SelectedCases;
                            selectOption.Data = fullOption.Data;
                            selectOption.SubOptions = fullOption.SubOptions;
                            RefreshPresetOptionRuntimeState(selectOption);
                        }
                    }
                    else
                    {
                        // select/switch: string (case.name) → Index
                        var caseName = optionValue.Value<string>();
                        if (caseName != null && interfaceOption.Cases != null)
                        {
                            var idx = interfaceOption.Cases.FindIndex(c => c.Name == caseName);
                            if (idx >= 0) selectOption.Index = idx;
                        }
                    }
                }

                // 切换 EnableSetting 强制重建选项控件（TaskOptionGenerator 是命令式创建，非数据绑定）
                if (dragItem.EnableSetting)
                {
                    dragItem.EnableSetting = false;
                    dragItem.EnableSetting = true;
                }
            }
        }

        var savedTaskItems = TaskItemViewModels
            .Where(model => !model.IsResourceOptionItem)
            .Select(model => model.InterfaceItem)
            .ToList();
        var currentTaskKeys = savedTaskItems
            .Where(task => !string.IsNullOrWhiteSpace(task?.Name) && !string.IsNullOrWhiteSpace(task.Entry))
            .Select(task => $"{task!.Name}{TaskLoader.NEW_SEPARATOR}{task.Entry}")
            .Distinct()
            .ToList();

        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, savedTaskItems);
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentTasks, currentTaskKeys);

        if (!string.IsNullOrWhiteSpace(preset.Name))
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.InstancePresetKey, preset.Name);
        else
            Processor.InstanceConfiguration.RemoveValue(ConfigurationKeys.InstancePresetKey);
    }

    private void RefreshPresetOptionRuntimeState(MaaInterface.MaaInterfaceSelectOption option)
    {
        if (string.IsNullOrWhiteSpace(option.Name)) return;
        if (MaaProcessor.Interface?.Option?.TryGetValue(option.Name, out var interfaceOption) != true) return;

        if (interfaceOption.IsInput && interfaceOption.Inputs != null)
        {
            option.Data ??= new Dictionary<string, string?>();
            foreach (var input in interfaceOption.Inputs)
            {
                if (!string.IsNullOrWhiteSpace(input.Name) && !option.Data.ContainsKey(input.Name))
                    option.Data[input.Name] = input.Default ?? string.Empty;
            }

            if (interfaceOption.PipelineOverride != null)
            {
                option.PipelineOverride = interfaceOption.GenerateProcessedPipeline(
                    option.Data
                        .Where(kv => kv.Value != null)
                        .ToDictionary(kv => kv.Key, kv => kv.Value!));
            }
        }

        if (interfaceOption.IsCheckbox)
            option.SelectedCases ??= new List<string>(interfaceOption.DefaultCases ?? new List<string>());

        if (option.SubOptions == null) return;

        foreach (var subOption in option.SubOptions)
            RefreshPresetOptionRuntimeState(subOption);
    }

    private MaaInterface.MaaInterfaceSelectOption? FindOrCreateNestedPresetOption(
        List<MaaInterface.MaaInterfaceSelectOption>? rootOptions,
        string targetOptionName,
        string? taskName)
    {
        if (rootOptions == null || string.IsNullOrWhiteSpace(targetOptionName)) return null;

        foreach (var rootOption in rootOptions)
        {
            var found = FindOrCreateNestedPresetOptionRecursive(
                rootOption,
                targetOptionName,
                taskName,
                new HashSet<string>(StringComparer.Ordinal));
            if (found != null) return found;
        }

        return null;
    }

    private MaaInterface.MaaInterfaceSelectOption? FindOrCreateNestedPresetOptionRecursive(
        MaaInterface.MaaInterfaceSelectOption currentOption,
        string targetOptionName,
        string? taskName,
        HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(currentOption.Name)) return null;
        if (!visited.Add(currentOption.Name)) return null;

        if (string.Equals(currentOption.Name, targetOptionName, StringComparison.Ordinal))
            return currentOption;

        if (MaaProcessor.Interface?.Option?.TryGetValue(currentOption.Name, out var currentOptionDef) != true ||
            currentOptionDef.Cases == null)
            return null;

        foreach (var optionCase in currentOptionDef.Cases)
        {
            if (optionCase.Option == null) continue;

            foreach (var childOptionName in optionCase.Option)
            {
                if (string.IsNullOrWhiteSpace(childOptionName)) continue;

                if (!string.Equals(childOptionName, targetOptionName, StringComparison.Ordinal) &&
                    !InterfaceOptionContainsTarget(childOptionName, targetOptionName, new HashSet<string>(StringComparer.Ordinal)))
                    continue;

                currentOption.SubOptions ??= new List<MaaInterface.MaaInterfaceSelectOption>();
                var childOption = currentOption.SubOptions.FirstOrDefault(o => o.Name == childOptionName);
                if (childOption == null)
                {
                    childOption = new MaaInterface.MaaInterfaceSelectOption { Name = childOptionName };
                    TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, childOption);
                    currentOption.SubOptions.Add(childOption);
                }

                if (string.Equals(childOptionName, targetOptionName, StringComparison.Ordinal))
                    return childOption;

                var found = FindOrCreateNestedPresetOptionRecursive(childOption, targetOptionName, taskName, visited);
                if (found != null) return found;
            }
        }

        return null;
    }

    private bool InterfaceOptionContainsTarget(string currentOptionName, string targetOptionName, HashSet<string> visited)
    {
        if (string.IsNullOrWhiteSpace(currentOptionName)) return false;
        if (string.Equals(currentOptionName, targetOptionName, StringComparison.Ordinal)) return true;
        if (!visited.Add(currentOptionName)) return false;

        if (MaaProcessor.Interface?.Option?.TryGetValue(currentOptionName, out var currentOptionDef) != true ||
            currentOptionDef.Cases == null)
            return false;

        foreach (var optionCase in currentOptionDef.Cases)
        {
            if (optionCase.Option == null) continue;

            foreach (var childOptionName in optionCase.Option)
            {
                if (InterfaceOptionContainsTarget(childOptionName, targetOptionName, visited))
                    return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private void ResetTasks()
    {
        using var _ = BeginUiLogScope("ResetTasks");
        // 保留特殊任务（倒计时、系统通知等用户手动添加的自定义 Action 任务）
        var specialTasks = TaskItemViewModels
            .Where(t => !string.IsNullOrWhiteSpace(t.InterfaceItem?.Entry)
                && ViewModels.UsersControls.Settings.AddTaskDialogViewModel.SpecialActionNames.Contains(t.InterfaceItem.Entry!))
            .ToList();

        // 清空当前任务列表
        TaskItemViewModels.Clear();

        // 从 TasksSource 重新填充任务（TasksSource 包含 interface 中定义的原始任务）
        foreach (var item in Processor.TasksSource)
        {
            // 克隆任务以避免引用问题
            var resetItem = item.Clone();
            resetItem.OwnerViewModel = this;
            EnsureDefaultOptionValues(resetItem.InterfaceItem?.Option);
            TaskItemViewModels.Add(resetItem);
        }

        // 恢复特殊任务到列表末尾
        foreach (var special in specialTasks)
        {
            special.OwnerViewModel = this;
            EnsureDefaultOptionValues(special.InterfaceItem?.Option);
            TaskItemViewModels.Add(special);
        }

        // 更新任务的资源支持状态
        UpdateTasksForResource(CurrentResource);

        // 保存配置
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, TaskItemViewModels.ToList().Select(model => model.InterfaceItem));
        LoggerHelper.UserAction("重置任务列表", $"taskCount={TaskItemViewModels.Count}",
            operation: "ResetTasks", instanceId: Processor.InstanceId, instanceName: InstanceName);
    }

    #endregion

    #region 日志

    /// <summary>
    /// 日志最大数量限制，超出后自动清理旧日志
    /// </summary>
    private const int MaxLogCount = 150;

    /// <summary>
    /// 每次清理时移除的日志数量
    /// </summary>
    private const int LogCleanupBatchSize = 30;

    /// <summary>
    /// 使用 DisposableObservableCollection 自动管理 LogItemViewModel 的生命周期
    /// 当元素被移除或集合被清空时，会自动调用 Dispose() 释放事件订阅
    /// </summary>
    public DisposableObservableCollection<LogItemViewModel> LogItemViewModels => Processor.LogItemViewModels;

    /// <summary>
    /// 清理超出限制的旧日志，防止内存泄漏
    /// DisposableObservableCollection 会自动调用被移除元素的 Dispose()
    /// </summary>
    private void TrimExcessLogs()
    {
        if (LogItemViewModels.Count <= MaxLogCount) return;

        // 计算需要移除的数量
        var removeCount = Math.Min(LogCleanupBatchSize, LogItemViewModels.Count - MaxLogCount + LogCleanupBatchSize);

        // 使用 RemoveRange 批量移除，DisposableObservableCollection 会自动 Dispose
        LogItemViewModels.RemoveRange(0, removeCount);

        // 清理字体缓存，释放未使用的字体资源
        // 这可以防止因渲染特殊Unicode字符而加载的大量字体占用内存
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
        return MaaProcessor.FormatFileSize(size);
    }

    public static string FormatDownloadSpeed(double speed)
    {
        return MaaProcessor.FormatDownloadSpeed(speed);
    }
    public void OutputDownloadProgress(long value = 0, long maximum = 1, int len = 0, double ts = 1)
    {
        Processor.OutputDownloadProgress(value, maximum, len, ts);
    }

    public void ClearDownloadProgress()
    {
        Processor.ClearDownloadProgress();
    }

    public void OutputDownloadProgress(string output, bool downloading = true)
    {
        Processor.OutputDownloadProgress(output, downloading);
    }


    public static readonly string INFO = "info:";
    public static readonly string[] ERROR = ["err:", "error:"];
    public static readonly string[] WARNING = ["warn:", "warning:"];
    public static readonly string TRACE = "trace:";
    public static readonly string DEBUG = "debug:";
    public static readonly string CRITICAL = "critical:";
    public static readonly string SUCCESS = "success:";

    public static bool CheckShouldLog(string content)
    {
        return MaaProcessor.CheckShouldLog(content);
    }

    public void AddLog(string content,
        IBrush? brush,
        string weight = "Regular",
        bool changeColor = true,
        bool showTime = true)
    {
        Processor.AddLog(content, brush, weight, changeColor, showTime);
    }

    public void AddLog(string content,
        string color = "",
        string weight = "Regular",
        bool changeColor = true,
        bool showTime = true)
    {
        Processor.AddLog(content, color, weight, changeColor, showTime);
    }

    public void AddLogByKey(string key, IBrush? brush = null, bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        Processor.AddLogByKey(key, brush, changeColor, transformKey, formatArgsKeys);
    }

    public void AddLogByKey(string key, string color, bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        Processor.AddLogByKey(key, color, changeColor, transformKey, formatArgsKeys);
    }

    public void AddMarkdown(string key, IBrush? brush = null, bool changeColor = true, bool transformKey = true, params string[] formatArgsKeys)
    {
        Processor.AddMarkdown(key, brush, changeColor, transformKey, formatArgsKeys);
    }

    #endregion

    #region 连接

    [ObservableProperty] private int _shouldShow = 0;
    [ObservableProperty] private ObservableCollection<object> _devices = [];
       [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDeviceTooltipText))]
    private object? _currentDevice;

    partial void OnDevicesChanged(ObservableCollection<object> value)
    {
        if (CurrentController != MaaControllerTypes.Adb)
            return;

        if (string.IsNullOrWhiteSpace(Processor.Config.AdbDevice.AdbSerial))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            SyncCurrentAdbSelectionToActiveConfig();
        }, DispatcherPriority.Background);
    }

    private static bool IsSelectableDevice(object? value) =>
        value is AdbDeviceInfo or DesktopWindowInfo or MacOSWindowInfo or WlRootsSocketInfo;

    private static EmptyDevicePlaceholder CreateEmptyDevicePlaceholder(MaaControllerTypes controllerType) =>
        controllerType == MaaControllerTypes.Adb
            ? new EmptyDevicePlaceholder(LangKeys.PleaseSelectEmulator.ToLocalization(), LangKeys.NoEmulatorFoundPlaceholder.ToLocalization())
            : new EmptyDevicePlaceholder(LangKeys.PleaseSelectWindow.ToLocalization(), LangKeys.NoWindowFoundPlaceholder.ToLocalization());

    private void ClearActiveAdbDeviceConfig()
    {
        Processor.Config.AdbDevice.Name = string.Empty;
        Processor.Config.AdbDevice.AdbPath = "adb";
        Processor.Config.AdbDevice.AdbSerial = string.Empty;
        Processor.Config.AdbDevice.Config = "{}";
        Processor.Config.AdbDevice.Info = null;
    }

    public void SetAdbRecoverySelectionLock(bool enabled)
    {
        _lockCurrentAdbSelectionDuringRecovery = enabled;
    }

    public void SyncCurrentAdbSelectionToActiveConfig()
    {
        if (CurrentController != MaaControllerTypes.Adb || string.IsNullOrWhiteSpace(Processor.Config.AdbDevice.AdbSerial))
            return;

        var matchedDevice = Devices.OfType<AdbDeviceInfo>().FirstOrDefault(device =>
            string.Equals(device.AdbPath, Processor.Config.AdbDevice.AdbPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.AdbSerial, Processor.Config.AdbDevice.AdbSerial, StringComparison.OrdinalIgnoreCase));

        if (matchedDevice == null)
        {
            var currentDevices = MaaProcessor.Toolkit.AdbDevice.Find();
            matchedDevice = currentDevices.FirstOrDefault(device =>
                string.Equals(device.AdbPath, Processor.Config.AdbDevice.AdbPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(device.AdbSerial, Processor.Config.AdbDevice.AdbSerial, StringComparison.OrdinalIgnoreCase));

            if (matchedDevice != null)
            {
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        Devices = new ObservableCollection<object>(currentDevices);
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
            }
        }

        if (matchedDevice == null)
            return;

        ApplyCurrentDeviceSelection(matchedDevice);
    }

    private void ApplyCurrentDeviceSelection(object matchedDevice)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            _suppressAutoConnect = true;
            _suppressDeviceSelectionToast = true;
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _suppressAutoConnect = true;
                    _suppressDeviceSelectionToast = true;
                    try
                    {
                        CurrentDevice = matchedDevice;
                        OnPropertyChanged(nameof(CurrentDevice));
                        OnPropertyChanged(nameof(CurrentDeviceTooltipText));
                    }
                    finally
                    {
                        _suppressDeviceSelectionToast = false;
                        _suppressAutoConnect = false;
                    }
                }, DispatcherPriority.Background);
            }
            finally
            {
                _suppressDeviceSelectionToast = false;
                _suppressAutoConnect = false;
            }
        });
    }

    private void SetEmptyDeviceState(MaaControllerTypes? controllerType = null)
    {
        var resolvedControllerType = controllerType ?? CurrentController;
        if (resolvedControllerType == MaaControllerTypes.Adb)
        {
            ClearActiveAdbDeviceConfig();
        }

        var placeholder = CreateEmptyDevicePlaceholder(resolvedControllerType);
        Devices = [placeholder];
        CurrentDevice = null;
    }

    [ObservableProperty] private bool _isConnected;

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
    }

    private DateTime? _lastExecutionTime;

    partial void OnShouldShowChanged(int value)
    {
        // DispatcherHelper.PostOnMainThread(() => Instances.TaskQueueView.UpdateConnectionLayout(true));
    }

    partial void OnCurrentDeviceChanged(object? value)
    {
        if (value is EmptyDevicePlaceholder)
        {
            _suppressAutoConnect = true;
            try
            {
                CurrentDevice = null;
            }
            finally
            {
                _suppressAutoConnect = false;
            }

            SetConnected(false);
            return;
        }

        LogUiStateChange("ChangeDevice", $"当前连接目标变更：{DescribeCurrentSelection()}");

        ChangedDevice(value);

        // 仅 ComboBox 手动选中设备时，根据"刷新后尝试连接"设置自动连接
        if (!_suppressAutoConnect
            && !_isSyncing
            && IsSelectableDevice(value)
            && Instances.IsResolved<ConnectSettingsUserControlModel>()
            && Instances.ConnectSettingsUserControlModel.AutoConnectAfterRefresh)
        {
            _ = TaskManager.RunTaskAsync(() =>
            {
                try
                {
                    Processor.TestConnecting().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LoggerHelper.Warning($"选中设备后自动连接失败：原因={ex.Message}");
                }
            }, name: "选中设备后自动连接", catchException: true, shouldLog: true);
        }
    }

    public void ChangedDevice(object? value)
    {
        if (_isSyncing) return;
        var igoreToast = _suppressDeviceSelectionToast;
        if (value != null)
        {
            var now = DateTime.Now;
            if (_lastExecutionTime == null)
            {
                _lastExecutionTime = now;
            }
            else
            {
                if (now - _lastExecutionTime < TimeSpan.FromSeconds(2))
                    igoreToast = true;
                else
                    _lastExecutionTime = now;
            }
        }
        if (value is EmptyDevicePlaceholder)
        {
            SetConnected(false);
        }
        else if (value is MacOSWindowInfo macOSWindow)
        {
            if (!igoreToast)
                ToastHelper.Info(LangKeys.WindowSelectionMessage.ToLocalizationFormatted(false, ""), macOSWindow.Name);
            var isSameWindow = Processor.Config.MacOSWindow.WindowId == macOSWindow.WindowId
                && macOSWindow.WindowId != 0;
            Processor.Config.MacOSWindow.Name = macOSWindow.Name;
            Processor.Config.MacOSWindow.WindowId = macOSWindow.WindowId;
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.DesktopWindowName, macOSWindow.Name);
            if (!Processor.IsConnecting && !isSameWindow)
                ResetTaskerInBackground();
        }
        else if (value is DesktopWindowInfo window)
        {
            if (!igoreToast) ToastHelper.Info(LangKeys.WindowSelectionMessage.ToLocalizationFormatted(false, ""), window.Name);
            var isSameWindow = Processor.Config.DesktopWindow.HWnd == window.Handle && window.Handle != IntPtr.Zero;
            Processor.Config.DesktopWindow.Name = window.Name;
            Processor.Config.DesktopWindow.HWnd = window.Handle;
            // 记录 ClassName 和 WindowName，下次启动时优先匹配
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.DesktopWindowClassName, window.ClassName);
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.DesktopWindowName, window.Name);
            // 正在连接或设备未变更时跳过 SetTasker，避免打断进行中的连接
            if (!Processor.IsConnecting && !isSameWindow)
                ResetTaskerInBackground();
        }
        else if (value is AdbDeviceInfo device)
        {
            if (!igoreToast) ToastHelper.Info(LangKeys.EmulatorSelectionMessage.ToLocalizationFormatted(false, ""), device.Name);
            // 不依赖 IsConnected（AutoDetectDevice 会提前调用 SetConnected(false)），直接比较设备信息
            var isSameDevice = !string.IsNullOrEmpty(Processor.Config.AdbDevice.AdbSerial)
                && Processor.Config.AdbDevice.AdbSerial == device.AdbSerial
                && Processor.Config.AdbDevice.AdbPath == device.AdbPath;
            Processor.Config.AdbDevice.Name = device.Name;
            Processor.Config.AdbDevice.AdbPath = device.AdbPath;
            Processor.Config.AdbDevice.AdbSerial = device.AdbSerial;
            Processor.Config.AdbDevice.Config = device.Config;
            Processor.Config.AdbDevice.Info = device;
            // 正在连接或设备未变更时跳过 SetTasker，避免打断进行中的连接
            if (!Processor.IsConnecting && !isSameDevice)
                ResetTaskerInBackground();
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.AdbDevice, device);
        }
        else if (value is WlRootsSocketInfo socket && CurrentController == MaaControllerTypes.WlRoots)
        {
            var isSameSocket = string.Equals(
                Processor.Config.WlRoots.SocketPath,
                socket.SocketPath,
                StringComparison.Ordinal);
            Processor.Config.WlRoots.SocketPath = socket.SocketPath;
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.WlRootsSocketPath, socket.SocketPath);
            if (!Processor.IsConnecting && !isSameSocket)
                ResetTaskerInBackground();
        }
    }

    private void ResetTaskerInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                Processor.SetTasker();
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"后台重建任务执行器失败：实例={Processor.InstanceId}，原因={ex.Message}", ex);
            }
        });
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentDevicePlaceholderText))]
    private MaaControllerTypes _currentController = MaaControllerTypes.Adb;

    public string CurrentDevicePlaceholderText => CurrentController == MaaControllerTypes.Adb
        ? LangKeys.PleaseSelectEmulator.ToLocalization()
        : LangKeys.PleaseSelectWindow.ToLocalization();

    public string CurrentDeviceTooltipText
    {
        get
        {
            if (CurrentDevice != null)
            {
                return new DeviceDisplayConverter().Convert(CurrentDevice, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture)?.ToString() ?? string.Empty;
            }

            return CurrentController == MaaControllerTypes.Adb
                ? LangKeys.NoEmulatorFoundPlaceholder.ToLocalization()
                : LangKeys.NoWindowFoundPlaceholder.ToLocalization();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            OnPropertyChanged(nameof(CurrentDevicePlaceholderText));
            OnPropertyChanged(nameof(CurrentDeviceTooltipText));

            if (Devices.Count == 1 && Devices[0] is EmptyDevicePlaceholder)
            {
                SetEmptyDeviceState(CurrentController);
            }
        });
    }

    partial void OnCurrentControllerChanged(MaaControllerTypes value)
    {
        if (_isSyncing) return;
        SyncSelectedControllerToCurrentController(value);
        _savedControllerName = SelectedController?.Name ?? _savedControllerName;
        using var _ = BeginUiLogScope("ChangeController");
        LoggerHelper.UserAction("切换控制器",
            $"controller={value}, resource={CurrentResource}",
            operation: "ChangeController",
            instanceId: Processor.InstanceId,
            instanceName: InstanceName);
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentController, value.ToString());
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentControllerName, _savedControllerName ?? string.Empty);
        if (Instances.IsResolved<ConnectSettingsUserControlModel>())
            Instances.ConnectSettingsUserControlModel.CurrentControllerType = value;
        UpdateResourcesForController(CurrentResource);
        if (value == MaaControllerTypes.PlayCover)
        {
            TryReadPlayCoverConfig();
        }

        // 切换控制器类型时，先取消正在进行的搜索并清空设备列表，
        // 防止旧控制器类型的设备在搜索期间仍然显示
        _refreshCancellationTokenSource?.Cancel();
        _suppressAutoConnect = true;
        try
        {
            SetEmptyDeviceState(value);
        }
        finally
        {
            _suppressAutoConnect = false;
        }
        SetConnected(false);

        if (!ConfigurationManager.IsSwitching)
        {
            Refresh();
        }
    }

    private void SyncSelectedControllerToCurrentController(MaaControllerTypes controllerType)
    {
        if (ControllerOptions.Count == 0) return;

        var targetController = ResolveTargetController(controllerType);
        if (ReferenceEquals(SelectedController, targetController)) return;

        _isSyncing = true;
        try
        {
            DispatcherHelper.RunOnMainThread(() => SelectedController = targetController);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private MaaInterface.MaaResourceController? ResolveTargetController(MaaControllerTypes controllerType)
    {
        if (!string.IsNullOrWhiteSpace(_savedControllerName))
        {
            var exactMatch = ControllerOptions.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.Name)
                && c.Name.Equals(_savedControllerName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                return exactMatch;
            }
        }

        return ControllerOptions.FirstOrDefault(c => c.ControllerType == controllerType)
            ?? ControllerOptions.FirstOrDefault();
    }

    [RelayCommand]
    private void CustomAdb()
    {
        using var _ = BeginUiLogScope("OpenAdbEditor");
        LoggerHelper.UserAction("打开 ADB 编辑器", DescribeCurrentSelection(),
            operation: "OpenAdbEditor", instanceId: Processor.InstanceId, instanceName: InstanceName);
        var deviceInfo = CurrentDevice as AdbDeviceInfo;

        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.AdbEditor.ToLocalization()).WithViewModel(dialog => new AdbEditorDialogViewModel(deviceInfo, dialog)).Dismiss().ByClickingBackground().TryShow();
    }

    [RelayCommand]
    private void EditPlayCover()
    {
        using var _ = BeginUiLogScope("OpenPlayCoverEditor");
        LoggerHelper.UserAction("打开 PlayCover 编辑器", DescribeCurrentSelection(),
            operation: "OpenPlayCoverEditor", instanceId: Processor.InstanceId, instanceName: InstanceName);
        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.PlayCoverEditor.ToLocalization())
            .WithViewModel(dialog => new PlayCoverEditorDialogViewModel(Processor.Config.PlayCover, dialog))
            .Dismiss().ByClickingBackground().TryShow();
    }

    private CancellationTokenSource? _refreshCancellationTokenSource;

    [RelayCommand]
    private async Task Reconnect()
    {
        using var _ = BeginUiLogScope("Reconnect");
        LoggerHelper.UserAction("重新连接", DescribeCurrentSelection(),
            operation: "Reconnect", instanceId: Processor.InstanceId, instanceName: InstanceName);
        if (CurrentResources.Count == 0 || string.IsNullOrWhiteSpace(CurrentResource)
            || CurrentResources.All(r => !string.Equals(r.Name?.Trim(), CurrentResource.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.ResourceNotSelected.ToLocalization());
            LogStartBlocked("重新连接被拒绝：未选择有效资源");
            return;
        }

        if (await RefreshConnectionTargetIfNeededAsync())
        {
            LoggerHelper.Info("重新连接前发现连接目标为空，已先刷新设备列表。");
        }

        if (CurrentController != MaaControllerTypes.PlayCover && CurrentDevice == null)
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.DeviceNotSelected.ToLocalization());
            LogStartBlocked("重新连接被拒绝：未选择连接目标");
            return;
        }

        if (CurrentController == MaaControllerTypes.Adb
            && CurrentDevice is AdbDeviceInfo adbInfo
            && string.IsNullOrWhiteSpace(adbInfo.AdbSerial))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.AdbAddressEmpty.ToLocalization());
            LogStartBlocked("重新连接被拒绝：ADB 序列号为空");
            return;
        }

        if (CurrentController == MaaControllerTypes.PlayCover
            && string.IsNullOrWhiteSpace(Processor.Config.PlayCover.PlayCoverAddress))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.PlayCoverAddressEmpty.ToLocalization());
            LogStartBlocked("重新连接被拒绝：PlayCover 地址为空");
            return;
        }

        try
        {
            using var tokenSource = new CancellationTokenSource();
            await Processor.ReconnectAsync(tokenSource.Token);
            await Processor.TestConnecting();
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"重新连接失败：原因={ex.Message}");
        }
    }

    private bool NeedsRefreshBeforeReconnect()
    {
        if (CurrentController == MaaControllerTypes.None)
            return true;

        return CurrentController switch
        {
            MaaControllerTypes.PlayCover => false,
            MaaControllerTypes.Adb => CurrentDevice is not AdbDeviceInfo adbInfo
                || string.IsNullOrWhiteSpace(adbInfo.AdbSerial),
            MaaControllerTypes.Win32 or MaaControllerTypes.Gamepad => CurrentDevice is not DesktopWindowInfo window
                || window.Handle == IntPtr.Zero,
            MaaControllerTypes.MacOS => CurrentDevice is not MacOSWindowInfo macOSWindow
                || macOSWindow.WindowId == 0,
            MaaControllerTypes.WlRoots => CurrentDevice is not WlRootsSocketInfo socket
                || string.IsNullOrWhiteSpace(socket.SocketPath),
            _ => CurrentDevice == null,
        };
    }

    private async Task<bool> RefreshConnectionTargetIfNeededAsync()
    {
        if (!NeedsRefreshBeforeReconnect())
            return false;

        if (CurrentController == MaaControllerTypes.PlayCover)
            return false;

        try
        {
            await Task.Run(() => AutoDetectDevice());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"重新连接前刷新连接目标失败：{ex.Message}");
        }

        return true;
    }

    [RelayCommand]
    private void Refresh()
    {
        using var _ = BeginUiLogScope("RefreshDevices");
        LoggerHelper.UserAction("刷新连接目标", DescribeCurrentSelection(),
            operation: "RefreshDevices", instanceId: Processor.InstanceId, instanceName: InstanceName);
        if (Processor.IsConnecting)
        {
            ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.ConnectingInProgress.ToLocalization());
            return;
        }

        if (CurrentController == MaaControllerTypes.PlayCover)
        {
            SetConnected(false);
            return;
        }

        _refreshCancellationTokenSource?.Cancel();
        _refreshCancellationTokenSource = new CancellationTokenSource();
        var controllerType = CurrentController;
        TaskManager.RunTask(() =>
            {
                AutoDetectDevice(_refreshCancellationTokenSource.Token);

                // 刷新后自动连接（仅按钮触发的刷新）
                if (CurrentDevice != null
                    && Instances.ConnectSettingsUserControlModel.AutoConnectAfterRefresh)
                {
                    try
                    {
                        _refreshCancellationTokenSource.Token.ThrowIfCancellationRequested();
                        Processor.TestConnecting().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LoggerHelper.Warning($"刷新后自动连接失败：{ex.Message}");
                    }
                }
            }, _refreshCancellationTokenSource.Token, name: "刷新", handleError: (e) => HandleDetectionError(e, controllerType),
            catchException: true, shouldLog: true);
    }

    [RelayCommand]
    private void CloseE()
    {
        MaaProcessor.CloseSoftware();
    }

    [RelayCommand]
    private void Clear()
    {
        using var _ = BeginUiLogScope("ClearLogs");
        // DisposableObservableCollection 会自动调用所有元素的 Dispose()
        Processor.ClearLogs();
        LoggerHelper.UserAction("清空监控日志", null,
            operation: "ClearLogs", instanceId: Processor.InstanceId, instanceName: InstanceName);
    }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    [RelayCommand]
    private Task Export()
    {
        using var _ = BeginUiLogScope("ExportLogs");
        LoggerHelper.UserAction("导出日志", null,
            operation: "ExportLogs", instanceId: Processor.InstanceId, instanceName: InstanceName);

        Instances.DialogManager.CreateDialog()
            .WithTitle(LangKeys.ExportLog.ToLocalization())
            .WithViewModel(dialog => new ExportLogDialogViewModel(dialog))
            .Dismiss().ByClickingBackground()
            .TryShow();

        return Task.CompletedTask;
    }

    public void AutoDetectDevice(CancellationToken token = default, bool showToast = true, bool strictLaunchTarget = false)
    {
        if (CurrentController == MaaControllerTypes.PlayCover)
        {
            DispatcherHelper.RunOnMainThread(() =>
            {
                _suppressAutoConnect = true;
                try
                {
                    Devices = [];
                    CurrentDevice = null;
                }
                finally
                {
                    _suppressAutoConnect = false;
                }
            });
            SetConnected(false);
            return;
        }

        var controllerType = CurrentController;
        var isAdb = controllerType == MaaControllerTypes.Adb;

        if (showToast)
            ToastHelper.Info(GetDetectionMessage(controllerType));
        SetConnected(false);
        token.ThrowIfCancellationRequested();
        var (devices, index) = isAdb
            ? DetectAdbDevices(strictLaunchTarget)
            : controllerType == MaaControllerTypes.MacOS
                ? DetectMacOsWindows(controllerType)
                : controllerType == MaaControllerTypes.WlRoots
                    ? DetectWlRootsSockets()
                : DetectDesktopWindows(controllerType);
        token.ThrowIfCancellationRequested();
        UpdateDeviceList(devices, index);
        token.ThrowIfCancellationRequested();
        HandleControllerSettings(controllerType);
        token.ThrowIfCancellationRequested();
        UpdateConnectionStatus(devices.Count > 0, controllerType, showToast);
    }

    private string GetDetectionMessage(MaaControllerTypes controllerType) =>
        controllerType == MaaControllerTypes.Adb
            ? "EmulatorDetectionStarted".ToLocalization()
            : "WindowDetectionStarted".ToLocalization();

    private (ObservableCollection<object> devices, int index) DetectAdbDevices(bool strictLaunchTarget = false)
    {
        var devices = MaaProcessor.Toolkit.AdbDevice.Find();
        var index = CalculateAdbDeviceIndex(devices, strictLaunchTarget);
        return (new(devices), index);
    }

    private int CalculateAdbDeviceIndex(IList<AdbDeviceInfo> devices, bool strictLaunchTarget = false)
    {
        var preferredDeviceIndex = FindPreferredAdbDeviceIndexByLaunchConfig(devices);
        if (preferredDeviceIndex >= 0)
        {
            return preferredDeviceIndex;
        }

        if (strictLaunchTarget && GetPreferredEmulatorIndexFromLaunchConfig() >= 0)
        {
            LoggerHelper.Info("启动阶段启用了严格多开匹配：当前未找到目标多开号设备，保持未选择状态。");
            return -1;
        }

        if (CurrentDevice is AdbDeviceInfo info)
        {
            LogUiStateChange("MatchAdbDevice", $"当前设备指纹：{DescribeAdbDeviceInfo(info)}");
            var matchedDevice = FindBestFingerprintMatchedAdbDevice(devices, info);
            if (matchedDevice != null)
            {
                return devices.IndexOf(matchedDevice);
            }
        }

        return devices.Count > 0 ? 0 : -1;
    }

    private int FindPreferredAdbDeviceIndexByLaunchConfig(IList<AdbDeviceInfo> devices)
    {
        var targetIndex = GetPreferredEmulatorIndexFromLaunchConfig();
        if (targetIndex < 0)
        {
            return -1;
        }

        for (var i = 0; i < devices.Count; i++)
        {
            if (TryGetIndexFromConfig(devices[i].Config, out var index) && index == targetIndex)
            {
                LoggerHelper.Info($"按启动参数优先匹配 ADB 设备成功：多开号={targetIndex}，设备={DescribeAdbDeviceInfo(devices[i])}");
                return i;
            }
        }

        LoggerHelper.Info($"启动参数已指定多开号={targetIndex}，但当前未找到相同多开号的 ADB 设备，将回退到上次连接设备匹配。");
        return -1;
    }

    private AdbDeviceInfo? FindBestFingerprintMatchedAdbDevice(IEnumerable<AdbDeviceInfo> devices, AdbDeviceInfo savedDevice)
    {
        var matchedDevices = devices
            .Where(device => device.MatchesFingerprint(savedDevice))
            .ToList();

        LoggerHelper.Info($"按指纹匹配到的设备数量：{matchedDevices.Count}");

        if (matchedDevices.Count == 0)
        {
            return null;
        }

        matchedDevices.Sort((a, b) =>
        {
            var aPrefix = a.AdbSerial.Split(':', 2)[0];
            var bPrefix = b.AdbSerial.Split(':', 2)[0];
            var prefixCompare = string.Compare(aPrefix, bPrefix, StringComparison.Ordinal);
            return prefixCompare != 0 ? prefixCompare : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return matchedDevices[0];
    }

    private int GetPreferredEmulatorIndexFromLaunchConfig()
    {
        var config = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
        return ExtractNumberFromEmulatorConfig(config);
    }


    public static int ExtractNumberFromEmulatorConfig(string emulatorConfig)
    {
        return MultiInstanceEditorDialogViewModel.TryExtractIndexFromEmulatorConfig(emulatorConfig);
    }

    private bool TryGetIndexFromConfig(string configJson, out int index)
    {
        index = DeviceDisplayConverter.GetFirstEmulatorIndex(configJson);
        return index != -1;
    }

    private static bool TryExtractPortFromAdbSerial(string adbSerial, out int port)
    {
        port = -1;
        var parts = adbSerial.Split(':', 2); // 分割为IP和端口（最多分割1次）
        LoggerHelper.Info($"解析 ADB 序列号：raw={adbSerial}, segmentCount={parts.Length}");
        return parts.Length == 2 && int.TryParse(parts[1], out port);
    }

    private (ObservableCollection<object> devices, int index) DetectDesktopWindows(MaaControllerTypes controllerType)
    {
        Thread.Sleep(500);
        var windows = MaaProcessor.Toolkit.Desktop.Window.Find().Where(win => !string.IsNullOrWhiteSpace(win.Name)).ToList();
        var (index, filtered) = CalculateWindowIndex(windows, controllerType);
        return (new(filtered), index);
    }

    private (ObservableCollection<object> devices, int index) DetectMacOsWindows(MaaControllerTypes controllerType)
    {
        Thread.Sleep(500);
        // Binding currently exposes macOS window discovery through the cross-platform Desktop.Window API.
        var windows = MaaProcessor.Toolkit.Desktop.Window.Find()
            .Where(window => window.Handle != IntPtr.Zero && !string.IsNullOrWhiteSpace(window.Name))
            .ToList();
        var (index, filtered) = CalculateWindowIndex(windows, controllerType);
        var macOSWindows = filtered
            .Select(window => new MacOSWindowInfo(checked((uint)(nuint)window.Handle), window.Name))
            .Cast<object>();
        return (new ObservableCollection<object>(macOSWindows), index);
    }

    private (ObservableCollection<object> devices, int index) DetectWlRootsSockets()
    {
        Thread.Sleep(500);
        var sockets = MaaProcessor.Toolkit.Desktop.Window.Find()
            .Select(window => window.Name?.Trim())
            .Where(socket => !string.IsNullOrWhiteSpace(socket))
            .Distinct(StringComparer.Ordinal)
            .Select(socket => new WlRootsSocketInfo(socket!))
            .Cast<object>()
            .ToList();
        var savedSocket = Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.WlRootsSocketPath,
            Processor.Config.WlRoots.SocketPath);
        var index = sockets.FindIndex(socket => socket is WlRootsSocketInfo info
            && string.Equals(info.SocketPath, savedSocket, StringComparison.Ordinal));
        return (new ObservableCollection<object>(sockets), index >= 0 ? index : 0);
    }

    private (int index, List<DesktopWindowInfo> afterFiltered) CalculateWindowIndex(List<DesktopWindowInfo> windows, MaaControllerTypes controllerType)
    {
        var controller = MaaProcessor.Interface?.Controller?
            .FirstOrDefault(c => c.Type?.Equals(controllerType.ToJsonKey(), StringComparison.OrdinalIgnoreCase) == true);

        if (controllerType == MaaControllerTypes.MacOS && controller?.MacOS == null
            || controllerType == MaaControllerTypes.Gamepad && controller?.Gamepad == null
            || controllerType == MaaControllerTypes.Win32 && controller?.Win32 == null)
        {
            var idx = MatchPreviousWindow(windows, controllerType);
            return (idx >= 0 ? idx : Math.Max(0, windows.FindIndex(win => !string.IsNullOrWhiteSpace(win.Name))), windows);
        }

        var filtered = windows.Where(win =>
            !string.IsNullOrWhiteSpace(win.Name)).ToList();

        filtered = controllerType switch
        {
            MaaControllerTypes.MacOS => ApplyRegexFilters(filtered, controller!.MacOS!),
            MaaControllerTypes.Gamepad => ApplyRegexFilters(filtered, controller!.Gamepad!),
            _ => ApplyRegexFilters(filtered, controller!.Win32!)
        };

        var matchedIdx = MatchPreviousWindow(filtered, controllerType);
        return (matchedIdx >= 0 ? matchedIdx : (filtered.Count > 0 ? 0 : 0), filtered.ToList());
    }

    /// <summary>
    /// 在窗口列表中匹配上次选中的窗口（优先 ClassName+Name 完全匹配，其次 ClassName 匹配）。
    /// 当 CurrentDevice 为 null（启动初始化时）会从保存的配置中读取上次选中的窗口信息进行匹配，
    /// 后续刷新时 CurrentDevice 已有值，不会走配置回退逻辑。
    /// </summary>
    private int MatchPreviousWindow(List<DesktopWindowInfo> windows, MaaControllerTypes controllerType)
    {
        if (windows.Count == 0)
            return -1;

        if (controllerType == MaaControllerTypes.MacOS)
        {
            if (CurrentDevice is MacOSWindowInfo currentMacOSWindow)
            {
                var currentMatch = windows.FindIndex(window =>
                    checked((uint)(nuint)window.Handle) == currentMacOSWindow.WindowId);
                if (currentMatch >= 0)
                    return currentMatch;
            }

            var savedTitle = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.DesktopWindowName, string.Empty);
            return string.IsNullOrWhiteSpace(savedTitle)
                ? -1
                : windows.FindIndex(window => string.Equals(window.Name, savedTitle, StringComparison.Ordinal));
        }

        // 优先从内存中的当前设备匹配（用于同一会话内的刷新）
        if (CurrentDevice is DesktopWindowInfo prev)
        {
            var exactMatch = windows.FindIndex(w =>
                string.Equals(w.ClassName, prev.ClassName, StringComparison.Ordinal)
                && string.Equals(w.Name, prev.Name, StringComparison.Ordinal));
            if (exactMatch >= 0) return exactMatch;

            var classMatch = windows.FindIndex(w =>
                string.Equals(w.ClassName, prev.ClassName, StringComparison.Ordinal));
            if (classMatch >= 0) return classMatch;

            return -1;
        }

        // 从保存的配置中匹配（用于启动后初始化，CurrentDevice 尚为 null）
        var savedClassName = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.DesktopWindowClassName, string.Empty);
        var savedWindowName = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.DesktopWindowName, string.Empty);

        if (!string.IsNullOrEmpty(savedClassName))
        {
            var exactMatch = windows.FindIndex(w =>
                string.Equals(w.ClassName, savedClassName, StringComparison.Ordinal)
                && string.Equals(w.Name, savedWindowName, StringComparison.Ordinal));
            if (exactMatch >= 0) return exactMatch;

            var classMatch = windows.FindIndex(w =>
                string.Equals(w.ClassName, savedClassName, StringComparison.Ordinal));
            if (classMatch >= 0) return classMatch;
        }

        return -1;
    }


    private List<DesktopWindowInfo> ApplyRegexFilters(List<DesktopWindowInfo> windows, MaaInterface.MaaResourceControllerWin32 win32)
        => ApplyRegexFilters(windows, win32.ClassRegex, win32.WindowRegex);

    private List<DesktopWindowInfo> ApplyRegexFilters(List<DesktopWindowInfo> windows, MaaInterface.MaaResourceControllerMacOS macOS)
    {
        if (string.IsNullOrWhiteSpace(macOS.TitleRegex))
            return windows;

        var titleRegex = new Regex(macOS.TitleRegex);
        return windows.Where(window => titleRegex.IsMatch(window.Name)).ToList();
    }

    private List<DesktopWindowInfo> ApplyRegexFilters(List<DesktopWindowInfo> windows, MaaInterface.MaaResourceControllerGamepad gamepad)
        => ApplyRegexFilters(windows, gamepad.ClassRegex, gamepad.WindowRegex);

    private List<DesktopWindowInfo> ApplyRegexFilters(List<DesktopWindowInfo> windows, string? classRegex, string? windowRegex)
    {
        var filtered = windows;
        if (!string.IsNullOrWhiteSpace(windowRegex))
        {
            var regex = new Regex(windowRegex);
            filtered = filtered.Where(w => regex.IsMatch(w.Name)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(classRegex))
        {
            var regex = new Regex(classRegex);
            filtered = filtered.Where(w => regex.IsMatch(w.ClassName)).ToList();
        }
        return filtered;
    }

    private void UpdateDeviceList(ObservableCollection<object> devices, int index)
    {
        // 使用同步方式更新设备列表，确保在 AutoDetectDevice 返回前设备已更新
        // 这对于 Win32 连接失败后重试时正确检测窗口至关重要
        //DispatcherHelper.RunOnMainThread 已经是同步的（使用 Dispatcher.UIThread.Invoke）
        DispatcherHelper.RunOnMainThread(() =>
        {
            _suppressAutoConnect = true;
            try
            {
                if (devices.Count == 0)
                {
                    SetEmptyDeviceState();
                }
                else
                {
                    Devices = devices;
                    if (CurrentController == MaaControllerTypes.Adb && _lockCurrentAdbSelectionDuringRecovery)
                    {
                        LoggerHelper.Info("恢复已保存模拟器期间仅刷新设备列表，保留当前选中目标不变。");
                    }
                    else
                    {
                        CurrentDevice = index >= 0 && index < devices.Count ? devices[index] : null;
                    }
                }
            }
            finally
            {
                _suppressAutoConnect = false;
            }
        });
    }

    private void HandleControllerSettings(MaaControllerTypes controllerType)
    {
        if (controllerType == MaaControllerTypes.PlayCover)
            return;

        // 同一控制器只应用一次 interface.json 的设置，避免每次刷新都覆盖用户配置
        var controllerName = NormalizeControllerName(GetCurrentControllerName());
        var controller = MaaProcessor.Interface?.Controller?
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(controllerName)
                && c.Name?.Equals(controllerName, StringComparison.OrdinalIgnoreCase) == true)
            ?? MaaProcessor.Interface?.Controller?
                .FirstOrDefault(c => c.Type?.Equals(controllerType.ToJsonKey(), StringComparison.OrdinalIgnoreCase) == true);
        var settingsKey = (controllerType, controllerName);
        if (_lastAppliedControllerSettingsKey == settingsKey)
            return;

        if (controller == null) return;
        _lastAppliedControllerSettingsKey = settingsKey;

        if (controllerType == MaaControllerTypes.WlRoots)
        {
            Processor.Config.WlRoots.UseWin32VirtualKeyCodes = controller.WlRoots?.UseWin32VkCode ?? false;
            return;
        }

        var isAdb = controllerType == MaaControllerTypes.Adb;
        HandleInputSettings(controller, isAdb);
        HandleScreenCapSettings(controller, isAdb);
    }

    private static string? NormalizeControllerName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private void HandleInputSettings(MaaInterface.MaaResourceController controller, bool isAdb)
    {
        if (isAdb)
        {
            var input = controller.Adb?.Input;
            if (input == null) return;
            Instances.ConnectSettingsUserControlModel.AdbControlInputType = input switch
            {
                1 => AdbInputMethods.AdbShell,
                2 => AdbInputMethods.MinitouchAndAdbKey,
                4 => AdbInputMethods.Maatouch,
                8 => AdbInputMethods.EmulatorExtras,
                _ => Instances.ConnectSettingsUserControlModel.AdbControlInputType
            };
        }
        else
        {
            if (controller.ControllerType == MaaControllerTypes.MacOS)
            {
                var macOSInput = ParseMacOSInputMethod(controller.MacOS?.Input);
                if (macOSInput != null)
                    Instances.ConnectSettingsUserControlModel.MacOSControlInputType = macOSInput.Value;
                return;
            }

            if (controller.ControllerType == MaaControllerTypes.Gamepad)
            {
                var gamepadType = ParseGamepadType(controller.Gamepad?.GamepadType);
                if (gamepadType != null)
                    Instances.ConnectSettingsUserControlModel.GamepadType = gamepadType.Value;
                return;
            }

            var mouse = controller.Win32?.Mouse;
            if (mouse != null)
            {
                var parsed = ParseWin32InputMethod(mouse);
                if (parsed != null)
                    Instances.ConnectSettingsUserControlModel.Win32ControlMouseType = parsed.Value;
            }
            var keyboard = controller.Win32?.Keyboard;
            if (keyboard != null)
            {
                var parsed = ParseWin32InputMethod(keyboard);
                if (parsed != null)
                    Instances.ConnectSettingsUserControlModel.Win32ControlKeyboardType = parsed.Value;
            }
            var input = controller.Win32?.Input;
            if (keyboard == null && mouse == null && input != null)
            {
                var parsed = ParseWin32InputMethod(input);
                if (parsed != null)
                {
                    Instances.ConnectSettingsUserControlModel.Win32ControlKeyboardType = parsed.Value;
                    Instances.ConnectSettingsUserControlModel.Win32ControlMouseType = parsed.Value;
                }
            }
        }
    }

    /// <summary>
    /// 解析 Win32InputMethod，支持旧版 long 格式和新版 string 格式
    /// </summary>
    private static Win32InputMethod? ParseWin32InputMethod(object? value)
    {
        if (value == null) return null;

        // 新版 string 格式（枚举名）
        if (value is string strValue)
        {
            if (Enum.TryParse<Win32InputMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        // 旧版 long 格式
        var longValue = Convert.ToInt64(value);
        return longValue switch
        {
            1 => Win32InputMethod.Seize,
            2 => Win32InputMethod.SendMessage,
            4 => Win32InputMethod.PostMessage,
            8 => Win32InputMethod.LegacyEvent,
            16 => Win32InputMethod.PostThreadMessage,
            32 => Win32InputMethod.SendMessageWithCursorPos,
            64 => Win32InputMethod.PostMessageWithCursorPos,
            128 => Win32InputMethod.SendMessageWithWindowPos,
            256 => Win32InputMethod.PostMessageWithWindowPos,
            _ => null
        };
    }

    /// <summary>
    /// 解析 Win32ScreencapMethods，支持旧版 long 格式和新版 string 格式
    /// </summary>
    private static Win32ScreencapMethods? ParseWin32ScreencapMethods(object? value)
    {
        if (value == null) return null;

        // 新版 string 格式（枚举名）
        if (value is string strValue)
        {
            if (Enum.TryParse<Win32ScreencapMethods>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        // 旧版 long 格式
        var longValue = Convert.ToInt64(value);
        return longValue switch
        {
            1 => Win32ScreencapMethods.GDI,
            2 => Win32ScreencapMethods.FramePool,
            4 => Win32ScreencapMethods.DXGI_DesktopDup,
            8 => Win32ScreencapMethods.DXGI_DesktopDup_Window,
            16 => Win32ScreencapMethods.PrintWindow,
            32 => Win32ScreencapMethods.ScreenDC,
            _ => null
        };
    }

    private static MacOSInputMethod? ParseMacOSInputMethod(object? value)
    {
        if (value == null) return null;

        if (value is string strValue)
        {
            if (Enum.TryParse<MacOSInputMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        var longValue = Convert.ToUInt64(value);
        return longValue switch
        {
            1 => MacOSInputMethod.GlobalEvent,
            2 => MacOSInputMethod.PostToPid,
            _ => null
        };
    }

    private static MacOSScreencapMethod? ParseMacOSScreencapMethod(object? value)
    {
        if (value == null) return null;

        if (value is string strValue)
        {
            if (Enum.TryParse<MacOSScreencapMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        return Convert.ToUInt64(value) == 1 ? MacOSScreencapMethod.ScreenCaptureKit : null;
    }

    private static GamepadType? ParseGamepadType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value == "0")
            return GamepadType.Xbox360;
        if (value == "1")
            return GamepadType.DualShock4;
        if (value.Equals("DS4", StringComparison.OrdinalIgnoreCase))
            return GamepadType.DualShock4;
        return Enum.TryParse<GamepadType>(value, true, out var result) && Enum.IsDefined(result) ? result : null;
    }

    private void HandleScreenCapSettings(MaaInterface.MaaResourceController controller, bool isAdb)
    {
        if (isAdb)
        {
            var screenCap = controller.Adb?.ScreenCap;
            if (screenCap == null) return;
            Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType = screenCap switch
            {
                1 => AdbScreencapMethods.EncodeToFileAndPull,
                2 => AdbScreencapMethods.Encode,
                4 => AdbScreencapMethods.RawWithGzip,
                8 => AdbScreencapMethods.RawByNetcat,
                16 => AdbScreencapMethods.MinicapDirect,
                32 => AdbScreencapMethods.MinicapStream,
                64 => AdbScreencapMethods.EmulatorExtras,
                _ => Instances.ConnectSettingsUserControlModel.AdbControlScreenCapType
            };
        }
        else
        {
            if (controller.ControllerType == MaaControllerTypes.MacOS)
            {
                var macOSScreencap = ParseMacOSScreencapMethod(controller.MacOS?.ScreenCap);
                if (macOSScreencap != null)
                    Instances.ConnectSettingsUserControlModel.MacOSControlScreenCapType = macOSScreencap.Value;
                return;
            }


            if (controller.ControllerType == MaaControllerTypes.Gamepad)
            {
                var gamepadScreencap = ParseWin32ScreencapMethods(controller.Gamepad?.ScreenCap);
                if (gamepadScreencap != null)
                    Instances.ConnectSettingsUserControlModel.GamepadControlScreenCapType = gamepadScreencap.Value;
                return;
            }

            var screenCap = controller.Win32?.ScreenCap;
            if (screenCap == null) return;
            var parsed = ParseWin32ScreencapMethods(screenCap);
            if (parsed != null)
                Instances.ConnectSettingsUserControlModel.Win32ControlScreenCapType = parsed.Value;
        }
    }

    private void UpdateConnectionStatus(bool hasDevices, MaaControllerTypes controllerType, bool showToast = true)
    {
        if (!hasDevices && showToast)
        {
            var isAdb = controllerType == MaaControllerTypes.Adb;
            ToastHelper.Info((
                isAdb ? LangKeys.NoEmulatorFound : LangKeys.NoWindowFound).ToLocalization(), (
                isAdb ? LangKeys.NoEmulatorFoundDetail : "").ToLocalization());
        }
    }

    public void TryReadPlayCoverConfig()
    {
        if (Processor.InstanceConfiguration.TryGetValue(ConfigurationKeys.PlayCoverConfig, out PlayCoverCoreConfig savedConfig))
        {
            Processor.Config.PlayCover = savedConfig;
        }
    }

    private void HandleDetectionError(Exception ex, MaaControllerTypes controllerType)
    {
        var targetKey = controllerType switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator,
            MaaControllerTypes.Win32 => LangKeys.Window,
            MaaControllerTypes.MacOS => LangKeys.Window,
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
            _ => LangKeys.Window
        };
        ToastHelper.Warn(string.Format(
            LangKeys.TaskStackError.ToLocalization(),
            targetKey.ToLocalization(),
            ex.Message));

        LoggerHelper.Error($"读取{targetKey.ToLocalization()}配置失败：{ex.Message}", ex);
    }

    public void TryReadAdbDeviceFromConfig(bool inTask = true, bool refresh = false, bool allowAutoDetect = true, bool showToast = true, bool strictLaunchTarget = false)
    {
        if (CurrentController == MaaControllerTypes.PlayCover)
        {
            SetConnected(false);
            return;
        }

        var rememberAdb = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.RememberAdb, true);
        var hasSavedDevice = Processor.InstanceConfiguration.TryGetValue(ConfigurationKeys.AdbDevice, out AdbDeviceInfo savedDevice1,
            new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>());
        var shouldKeepMatchingSavedDeviceStrictly = strictLaunchTarget
            && CurrentController == MaaControllerTypes.Adb
            && rememberAdb
            && Processor.Config.AdbDevice.AdbPath == "adb"
            && hasSavedDevice;

        if (!allowAutoDetect && !shouldKeepMatchingSavedDeviceStrictly)
        {
            if (CurrentController != MaaControllerTypes.Adb
                || !rememberAdb
                || !hasSavedDevice)
            {
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        SetEmptyDeviceState(MaaControllerTypes.Adb);
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
                return;
            }

            var savedDevice = savedDevice1;
            DispatcherHelper.RunOnMainThread(() =>
            {
                _suppressAutoConnect = true;
                try
                {
                    Devices = [savedDevice];
                    CurrentDevice = savedDevice;
                }
                finally
                {
                    _suppressAutoConnect = false;
                }
            });
            return;
        }

        if ((refresh && !shouldKeepMatchingSavedDeviceStrictly)
            || CurrentController != MaaControllerTypes.Adb
            || !rememberAdb
            || Processor.Config.AdbDevice.AdbPath != "adb"
            || !hasSavedDevice)
        {
            _refreshCancellationTokenSource?.Cancel();
            _refreshCancellationTokenSource = new CancellationTokenSource();
            if (inTask)
                TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token, showToast, strictLaunchTarget), name: "刷新设备");
            else
                AutoDetectDevice(_refreshCancellationTokenSource.Token, showToast, strictLaunchTarget);
            return;
        }
        // 检查是否启用指纹匹配功能
        var useFingerprintMatching = shouldKeepMatchingSavedDeviceStrictly
            || Processor.InstanceConfiguration.GetValue(ConfigurationKeys.UseFingerprintMatching, true);

        if (useFingerprintMatching)
        {
            // 使用指纹匹配设备，而不是直接使用保存的设备信息
            // 因为雷电模拟器等的AdbSerial每次启动都会变化
            LoggerHelper.Info(shouldKeepMatchingSavedDeviceStrictly
                ? "严格匹配模式已启用，持续按已保存设备指纹匹配 ADB 设备。"
                : "正在从配置中读取已保存的 ADB 设备，并启用指纹匹配。");
            LoggerHelper.Info($"已保存设备的指纹：{savedDevice1.GenerateDeviceFingerprint()}");

            // 搜索当前可用的设备
            var currentDevices = MaaProcessor.Toolkit.AdbDevice.Find();

            var preferredDeviceIndex = FindPreferredAdbDeviceIndexByLaunchConfig(currentDevices);
            if (preferredDeviceIndex >= 0)
            {
                var preferredDevice = currentDevices[preferredDeviceIndex];
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        SetAdbRecoverySelectionLock(false);
                        Devices = new ObservableCollection<object>(currentDevices);
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
                ApplyCurrentDeviceSelection(preferredDevice);
                return;
            }

            if (strictLaunchTarget && GetPreferredEmulatorIndexFromLaunchConfig() >= 0)
            {
                LoggerHelper.Info("启动阶段启用了严格多开匹配：目标多开号设备尚未出现，本次不回退到历史设备。");
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        ClearActiveAdbDeviceConfig();
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
                return;
            }

            // 尝试通过指纹匹配找到对应的设备（当任一方index为-1时不比较index）
            var matchedDevice = FindBestFingerprintMatchedAdbDevice(currentDevices, savedDevice1);

            if (matchedDevice != null)
            {
                LoggerHelper.Info($"已通过指纹匹配到设备：名称={matchedDevice.Name}，ADB 序列号={matchedDevice.AdbSerial}");
                // 使用新搜索到的设备信息（AdbSerial等可能已更新）
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        SetAdbRecoverySelectionLock(false);
                        Devices = new ObservableCollection<object>(currentDevices);
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
                ApplyCurrentDeviceSelection(matchedDevice);
            }
            else
            {
                if (shouldKeepMatchingSavedDeviceStrictly)
                {
                    LoggerHelper.Info("严格匹配模式下暂未匹配到已保存设备，保留等待并继续按指纹匹配。");
                    DispatcherHelper.RunOnMainThread(() =>
                    {
                        _suppressAutoConnect = true;
                        try
                        {
                            ClearActiveAdbDeviceConfig();
                        }
                        finally
                        {
                            _suppressAutoConnect = false;
                        }
                    });
                    return;
                }

                // 没有找到匹配的设备，执行自动检测
                LoggerHelper.Info("未通过指纹匹配到设备，开始自动检测。");
                _refreshCancellationTokenSource?.Cancel();
                _refreshCancellationTokenSource = new CancellationTokenSource();
                if (inTask)
                    TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token, true, strictLaunchTarget), name: "刷新设备");
                else
                    AutoDetectDevice(_refreshCancellationTokenSource.Token, true, strictLaunchTarget);
            }
        }
        else
        {
            // 不使用指纹匹配，直接使用保存的设备信息
            LoggerHelper.Info("正在从配置中读取已保存的 ADB 设备，当前未启用指纹匹配。");
            DispatcherHelper.RunOnMainThread(() =>
            {
                _suppressAutoConnect = true;
                try
                {
                    Devices = [savedDevice1];
                    CurrentDevice = savedDevice1;
                }
                finally
                {
                    _suppressAutoConnect = false;
                }
            });
        }
    }

    #endregion

    #region 资源

    [ObservableProperty] private ObservableCollection<MaaInterface.MaaInterfaceResource> _currentResources = [];
    private string _currentResource = string.Empty;
    private bool _isRefreshingResourceSelection;
    private string? _pendingResourceSelection;

    public string CurrentResource
    {
        get => _currentResource;
        set
        {
            // 重建资源列表时，ComboBox 可能先把 SelectedValue 瞬时回写为空，
            // 若直接响应会误触发 SetTasker 并释放现有连接。
            if (_isRefreshingResourceSelection)
            {
                if (string.IsNullOrWhiteSpace(value)
                    && !string.IsNullOrWhiteSpace(_currentResource))
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_pendingResourceSelection)
                    && !string.Equals(value, _pendingResourceSelection, StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (string.Equals(_currentResource, value, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                // SetTasker 内部会同步等待旧 Tasker 停止，移到后台线程避免阻塞 UI
                ResetTaskerInBackground();
            }

            SetNewProperty(ref _currentResource, value);
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.Resource, value);
            LogUiStateChange("ChangeResource", $"当前资源变更：resource={value}, controller={CurrentController}");

            if (!string.IsNullOrWhiteSpace(value))
            {
                UpdateTasksForResource(value);
            }
            else
            {
                UpdateTasksForResource(null);
            }
        }
    }

    public void PersistConfigurationState()
    {
        var instanceConfig = Processor.InstanceConfiguration;

        instanceConfig.SetValue(ConfigurationKeys.CurrentController, CurrentController);
        instanceConfig.SetValue(ConfigurationKeys.CurrentControllerName, GetCurrentControllerName() ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(CurrentResource))
        {
            instanceConfig.SetValue(ConfigurationKeys.Resource, CurrentResource);
        }

        instanceConfig.SetValue(
            ConfigurationKeys.TaskItems,
            TaskItemViewModels
                .Where(m => !m.IsResourceOptionItem)
                .Select(m => m.InterfaceItem)
                .ToList());

        PersistResourceOptionConfiguration(instanceConfig);
    }

    private void PersistResourceOptionConfiguration(InstanceConfiguration instanceConfig)
    {
        var allItems = TaskItemViewModels
            .Where(m => m.IsResourceOptionItem && m.ResourceItem?.SelectOptions != null)
            .ToList();

        var globalItem = allItems.FirstOrDefault(m => m.ResourceItem?.Name == "__GlobalOption__");
        if (globalItem?.ResourceItem?.SelectOptions != null)
        {
            instanceConfig.SetValue(
                ConfigurationKeys.GlobalOptionItems,
                globalItem.ResourceItem.SelectOptions);
        }

        const string controllerPrefix = "__ControllerOption__";
        var controllerOptionItems = allItems
            .Where(m => m.ResourceItem?.Name?.StartsWith(controllerPrefix) == true)
            .ToDictionary(
                m => m.ResourceItem!.Name![controllerPrefix.Length..],
                m => m.ResourceItem!.SelectOptions!);
        if (controllerOptionItems.Count > 0)
        {
            instanceConfig.SetValue(
                ConfigurationKeys.ControllerOptionItems,
                controllerOptionItems);
        }

        var resourceOptionItems = allItems
            .Where(m => m.ResourceItem?.Name != "__GlobalOption__" &&
                        m.ResourceItem?.Name?.StartsWith(controllerPrefix) != true)
            .ToDictionary(
                m => m.ResourceItem!.Name ?? string.Empty,
                m => m.ResourceItem!.SelectOptions!);
        instanceConfig.SetValue(ConfigurationKeys.ResourceOptionItems, resourceOptionItems);
    }

    /// <summary>
    /// 判断是否为真正的资源选项项（排除全局选项项和控制器选项项）
    /// </summary>
    private static bool IsRealResourceOptionItem(DragItemViewModel item) =>
        item.IsResourceOptionItem &&
        item.ResourceItem?.Name != "__GlobalOption__" &&
        item.ResourceItem?.Name?.StartsWith("__ControllerOption__") != true &&
        item.ResourceItem?.Name?.StartsWith("__Setting__") != true;

    /// <summary>
    /// 根据当前资源更新任务列表的可见性和资源选项项
    /// </summary>
    /// <param name="resourceName">资源包名称</param>
    public void UpdateTasksForResource(string? resourceName)
    {
        // 查找当前资源
        var currentResource = CurrentResources.FirstOrDefault(r => r.Name == resourceName);
        var hasResourceOption = currentResource?.Option != null && currentResource.Option.Count > 0;

        // 只查找真正的资源选项项（排除全局选项项和控制器选项项）
        var existingResourceOptionItem = TaskItemViewModels.FirstOrDefault(IsRealResourceOptionItem);

        if (hasResourceOption)
        {
            // 初始化资源的 SelectOptions
            InitializeResourceSelectOptions(currentResource!);

            if (existingResourceOptionItem == null)
            {
                // 需要添加资源选项项，插入到全局选项项之后
                var resourceOptionItem = new DragItemViewModel(currentResource!) { OwnerViewModel = this };
                resourceOptionItem.IsVisible = true;

                // 从配置中恢复已保存的选项值
                RestoreResourceOptionValues(currentResource!);

                TaskItemViewModels.Insert(FindResourceOptionInsertIndex(), resourceOptionItem);
            }
            else if (existingResourceOptionItem.ResourceItem?.Name != currentResource!.Name)
            {
                // 资源选项项属于不同的资源，需要替换
                var index = TaskItemViewModels.IndexOf(existingResourceOptionItem);
                var wasShowingSettings = existingResourceOptionItem.EnableSetting;
                if (wasShowingSettings)
                    existingResourceOptionItem.EnableSetting = false;
                TaskItemViewModels.Remove(existingResourceOptionItem);

                var resourceOptionItem = new DragItemViewModel(currentResource) { OwnerViewModel = this };
                resourceOptionItem.IsVisible = true;

                // 从配置中恢复已保存的选项值
                RestoreResourceOptionValues(currentResource);

                TaskItemViewModels.Insert(index >= 0 ? index : FindResourceOptionInsertIndex(), resourceOptionItem);

                // 如果旧项正在显示设置面板，新项也打开
                if (wasShowingSettings)
                    resourceOptionItem.EnableSetting = true;
            }
            else
            {
                // 同一资源，更新 SelectOptions（控制器切换后 option 过滤条件可能变化，强制重建面板）
                existingResourceOptionItem.ResourceItem = currentResource;
                if (existingResourceOptionItem.EnableSetting)
                {
                    existingResourceOptionItem.EnableSetting = false;
                    existingResourceOptionItem.EnableSetting = true;
                }
            }
        }
        else
        {
            // 当前资源没有 option，移除资源选项项
            if (existingResourceOptionItem != null)
            {
                if (existingResourceOptionItem.EnableSetting)
                {
                    existingResourceOptionItem.EnableSetting = false;
                }
                TaskItemViewModels.Remove(existingResourceOptionItem);
            }
        }

        // 更新控制器选项项（切换控制器时移除旧的、添加新的）
        UpdateControllerOptionItemInList();

        // 更新每个任务的资源/控制器支持状态，并刷新已打开的设置面板
        var currentControllerName = GetCurrentControllerName();
        foreach (var task in TaskItemViewModels)
        {
            if (!task.IsResourceOptionItem)
            {
                task.UpdateResourceSupport(resourceName);
                task.UpdateControllerSupport(currentControllerName);

                // 如果设置面板已打开，强制重建以反映新的资源/控制器过滤
                if (task.EnableSetting)
                {
                    task.EnableSetting = false;
                    task.EnableSetting = true;
                }
            }
        }
    }

    public void RefreshTaskSupportForCurrentContext(DragItemViewModel task)
    {
        if (task.IsResourceOptionItem)
        {
            return;
        }

        task.UpdateResourceSupport(CurrentResource);
        task.UpdateControllerSupport(GetCurrentControllerName());
    }

    /// <summary>
    /// 计算资源选项项的插入位置（在全局选项项之后）
    /// </summary>
    private int FindResourceOptionInsertIndex()
    {
        var globalItem = TaskItemViewModels.FirstOrDefault(t =>
            t.IsResourceOptionItem && t.ResourceItem?.Name == "__GlobalOption__");
        return globalItem != null ? TaskItemViewModels.IndexOf(globalItem) + 1 : 0;
    }

    /// <summary>
    /// 更新控制器选项项：移除不匹配当前控制器的旧项，添加当前控制器的新项
    /// </summary>
    private void UpdateControllerOptionItemInList()
    {
        var currentControllerName = GetCurrentControllerName();
        var expectedSyntheticName = string.IsNullOrWhiteSpace(currentControllerName)
            ? null
            : $"__ControllerOption__{currentControllerName}";

        // 移除所有不匹配当前控制器的控制器选项项，记录是否有项正在显示设置面板
        var hadEnabledSetting = false;
        var staleItems = TaskItemViewModels
            .Where(t => t.IsResourceOptionItem &&
                        t.ResourceItem?.Name?.StartsWith("__ControllerOption__") == true &&
                        t.ResourceItem?.Name != expectedSyntheticName)
            .ToList();
        foreach (var item in staleItems)
        {
            if (item.EnableSetting)
            {
                hadEnabledSetting = true;
                item.EnableSetting = false;
            }
            TaskItemViewModels.Remove(item);
        }

        if (expectedSyntheticName == null) return;

        // 已存在则检查是否需要刷新（控制器未变但资源变化时可能需要重建面板）
        var existingControllerItem = TaskItemViewModels.FirstOrDefault(t =>
            t.IsResourceOptionItem && t.ResourceItem?.Name == expectedSyntheticName);
        if (existingControllerItem != null)
        {
            if (existingControllerItem.EnableSetting)
            {
                existingControllerItem.EnableSetting = false;
                existingControllerItem.EnableSetting = true;
            }
            return;
        }

        // 获取当前控制器对象
        var controllerObj = MaaProcessor.Interface?.Controller?.FirstOrDefault(c =>
            c.Name != null && c.Name.Equals(currentControllerName, StringComparison.OrdinalIgnoreCase));
        if (controllerObj == null) return;

        // 确保控制器的 SelectOptions 已初始化
        if ((controllerObj.SelectOptions == null || controllerObj.SelectOptions.Count == 0)
            && controllerObj.Option is { Count: > 0 })
        {
            InitializeControllerSelectOptionsForController(controllerObj);
        }

        if (controllerObj.SelectOptions == null || controllerObj.SelectOptions.Count == 0) return;

        // 创建控制器选项项并插入到资源选项项之后
        var syntheticResource = new MaaInterface.MaaInterfaceResource
        {
            Name = expectedSyntheticName,
            SelectOptions = controllerObj.SelectOptions,
        };
        syntheticResource.InitializeDisplayName();

        var newItem = new DragItemViewModel(syntheticResource) { OwnerViewModel = this };
        newItem.IsVisible = true;
        TaskItemViewModels.Insert(FindControllerOptionInsertIndex(), newItem);

        // 如果旧控制器选项项正在显示设置面板，新项也打开
        if (hadEnabledSetting)
            newItem.EnableSetting = true;
    }

    /// <summary>
    /// 计算控制器选项项的插入位置（在资源选项项之后，普通任务之前）
    /// </summary>
    private int FindControllerOptionInsertIndex()
    {
        // 找到最后一个资源选项项（真正的资源选项项）
        var resourceItem = TaskItemViewModels.LastOrDefault(IsRealResourceOptionItem);
        if (resourceItem != null)
            return TaskItemViewModels.IndexOf(resourceItem) + 1;

        // 没有资源选项项，插入到全局选项项之后
        var globalItem = TaskItemViewModels.FirstOrDefault(t =>
            t.IsResourceOptionItem && t.ResourceItem?.Name == "__GlobalOption__");
        return globalItem != null ? TaskItemViewModels.IndexOf(globalItem) + 1 : 0;
    }

    /// <summary>
    /// 初始化指定控制器的 SelectOptions（从配置中恢复已保存的值）
    /// </summary>
    private void InitializeControllerSelectOptionsForController(MaaInterface.MaaResourceController controller)
    {
        if (controller.Option == null || controller.Option.Count == 0)
        {
            controller.SelectOptions = null;
            return;
        }

        var savedOptions = Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.ControllerOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());

        Dictionary<string, MaaInterface.MaaInterfaceSelectOption>? savedDict = null;
        if (savedOptions.TryGetValue(controller.Name ?? string.Empty, out var savedList) && savedList != null)
            savedDict = savedList.ToDictionary(o => o.Name ?? string.Empty);

        var existingDict = controller.SelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        controller.SelectOptions = controller.Option.Select(optionName =>
        {
            if (existingDict.TryGetValue(optionName, out var existing))
            {
                TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, existing);
                return existing;
            }
            if (savedDict?.TryGetValue(optionName, out var saved) == true)
            {
                var savedOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = saved.Name,
                    Index = saved.Index,
                    Data = saved.Data != null ? new Dictionary<string, string?>(saved.Data) : null,
                    SelectedCases = saved.SelectedCases != null ? new List<string>(saved.SelectedCases) : null,
                };
                TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, savedOption);
                return savedOption;
            }
            var opt = new MaaInterface.MaaInterfaceSelectOption { Name = optionName };
            TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, opt);
            return opt;
        }).ToList();
    }

    /// <summary>
    /// 初始化资源的 SelectOptions
    /// 只初始化顶级选项，子选项会在运行时由 UpdateSubOptions 动态创建
    /// 会保留已有的值并从配置中恢复保存的值
    /// </summary>
    private void InitializeResourceSelectOptions(MaaInterface.MaaInterfaceResource resource)
    {
        if (resource.Option == null || resource.Option.Count == 0)
        {
            resource.SelectOptions = null;
            return;
        }

        // 收集所有子选项名称（这些选项不应该在顶级初始化）
        var subOptionNames = new HashSet<string>();
        foreach (var optionName in resource.Option)
        {
            if (MaaProcessor.Interface?.Option?.TryGetValue(optionName, out var interfaceOption) == true)
            {
                if (interfaceOption.Cases != null)
                {
                    foreach (var caseOption in interfaceOption.Cases)
                    {
                        if (caseOption.Option != null)
                        {
                            foreach (var subOptionName in caseOption.Option)
                            {
                                subOptionNames.Add(subOptionName);
                            }
                        }
                    }
                }
            }
        }

        // 获取已保存的配置
        var savedResourceOptions = Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.ResourceOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());

        Dictionary<string, MaaInterface.MaaInterfaceSelectOption>? savedDict = null;
        if (savedResourceOptions.TryGetValue(resource.Name ?? string.Empty, out var savedOptions) && savedOptions != null)
        {
            savedDict = savedOptions.ToDictionary(o => o.Name ?? string.Empty);
        }

        // 保留已有的 SelectOptions 值
        var existingDict = resource.SelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        // 只初始化顶级选项（不是子选项的选项）
        resource.SelectOptions = resource.Option
            .Where(optionName => !subOptionNames.Contains(optionName))
            .Select(optionName =>
            {
                // 优先使用已有的值（保留运行时的修改）
                if (existingDict.TryGetValue(optionName, out var existingOpt))
                {
                    return existingOpt;
                }

                // 其次使用配置中保存的值
                if (savedDict?.TryGetValue(optionName, out var savedOpt) == true)
                {
                    // 克隆保存的选项，避免引用问题
                    var clonedOpt = new MaaInterface.MaaInterfaceSelectOption
                    {
                        Name = savedOpt.Name,
                        Index = savedOpt.Index,
                        Data = savedOpt.Data != null ? new Dictionary<string, string?>(savedOpt.Data) : null,
                        SubOptions = savedOpt.SubOptions != null ? CloneSubOptions(savedOpt.SubOptions) : null,
                        SelectedCases = savedOpt.SelectedCases != null ? new List<string>(savedOpt.SelectedCases) : null,
                    };
                    return clonedOpt;
                }

                // 最后创建新的并设置默认值
                var selectOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = optionName
                };
                TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, selectOption);
                return selectOption;
            }).ToList();
    }

    /// <summary>
    /// 克隆子选项列表
    /// </summary>
    private List<MaaInterface.MaaInterfaceSelectOption> CloneSubOptions(List<MaaInterface.MaaInterfaceSelectOption> subOptions)
    {
        return subOptions.Select(opt => new MaaInterface.MaaInterfaceSelectOption
        {
            Name = opt.Name,
            Index = opt.Index,
            Data = opt.Data != null ? new Dictionary<string, string?>(opt.Data) : null,
            SubOptions = opt.SubOptions != null ? CloneSubOptions(opt.SubOptions) : null,
            SelectedCases = opt.SelectedCases != null ? new List<string>(opt.SelectedCases) : null
        }).ToList();
    }

    /// <summary>
    /// 从配置中恢复资源选项的已保存值（已整合到 InitializeResourceSelectOptions 中，保留此方法以兼容）
    /// </summary>
    private void RestoreResourceOptionValues(MaaInterface.MaaInterfaceResource resource)
    {
        // 配置恢复逻辑已整合到 InitializeResourceSelectOptions 中
        // 此方法保留以兼容现有调用，但不再需要执行任何操作
    }

    #endregion

    #region 实时画面

    /// <summary>
    /// Live View 刷新率变化事件，参数为计算后的间隔（秒）
    /// </summary>
    public event Action<double>? LiveViewRefreshRateChanged;

    private readonly System.Timers.Timer _liveViewTimer;
    private int _liveViewTickInProgress;
    private int _isDisposed;
    private bool _liveViewNoImageLogged;

    private void UpdateLiveViewTimerInterval()
    {
        var interval = GetLiveViewRefreshInterval();
        _liveViewTimer.Interval = Math.Max(1, interval * 1000);
    }

    private void OnLiveViewTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Android renders the Shizuku virtual display through
        // IMobileVirtualDisplayBackend.FrameReady.  Running the desktop live-view
        // poller as well submits synchronous Screencap jobs to the same native
        // controller while MaaFramework is executing actions.  Besides producing
        // a false "screenshot timed out" message during connection, that can block
        // both the action and UI paths on the controller's native lock.
        if (OperatingSystem.IsAndroid())
            return;

        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        if (Interlocked.Exchange(ref _liveViewTickInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            if (Processor.IsClosed)
                return;

            if (Processor.TryConsumeScreencapFailureLog(out var shouldAbort, out var shouldDisconnected))
            {
                // UI updates must be dispatched
                DispatcherHelper.PostOnMainThread(() =>
                {
                    if (shouldAbort)
                    {
                        AddLogByKey(LangKeys.ScreencapTimeoutAbort, Brushes.OrangeRed, changeColor: false);
                    }
                    if (shouldDisconnected)
                    {
                        AddLogByKey(LangKeys.ScreencapTimeoutDisconnected, Brushes.OrangeRed, changeColor: false);
                    }
                });
            }
            if (!IsLiveViewExpanded)
                return;
            if (EnableLiveView && IsConnected)
            {
                // 流水线：非阻塞提交下一帧截图（若上一帧仍在执行则复用，避免任务堆积），
                // 随后读取最近完成的一帧进行显示。相比原来的同步 Screencap().Wait()，
                // 定时器不再被截图耗时阻塞，实际刷新率可真正由 LiveViewRefreshRate 决定。
                // 返回值为上一帧的失败终态（Failed/Invalid）时走既有恢复逻辑（连续失败达阈值才重建/断开）。
                var status = Processor.PostScreencapPipelined();
                if (status is MaaJobStatus.Failed or MaaJobStatus.Invalid)
                {
                    if (Processor.HandleScreencapStatus(status.Value))
                    {
                        if (Processor.IsMainControllerConnected())
                        {
                            LoggerHelper.Warning("实时画面截图链路失效，但主控制器仍在线，准备重建截图任务执行器。");
                            Processor.ResetLiveViewTasker();
                        }
                        else
                        {
                            SetConnected(false);
                            DispatcherHelper.PostOnMainThread(() =>
                            {
                                AddLogByKey(LangKeys.ScreencapTimeoutDisconnected, Brushes.OrangeRed, changeColor: false);
                            });
                        }

                        return;
                    }
                    return;
                }

                var buffer = Processor.GetLiveViewBuffer(false);
                if (buffer == null)
                {
                    if (!_liveViewNoImageLogged)
                    {
                        _liveViewNoImageLogged = true;
                        var screencapType = Processor.ScreenshotType();
                        var controllerType = CurrentController;
                        var reason = controllerType == MaaControllerTypes.Adb
                            ? LangKeys.LiveViewNoImageReasonAdb.ToLocalization()
                            : LangKeys.LiveViewNoImageReasonWindow.ToLocalization();
                        LoggerHelper.Warning($"实时画面为空：截图方式={screencapType}，控制器={controllerType}，原因={reason}");
                        AddLog($"warn: {LangKeys.LiveViewNoImageWarning.ToLocalizationFormatted(false, screencapType, reason)}", (IBrush?)null);
                    }
                }
                else
                {
                    _liveViewNoImageLogged = false;
                    _ = UpdateLiveViewImageAsync(buffer);
                }
            }
            else
            {
                _ = UpdateLiveViewImageAsync(null);
            }
        }
        catch
        {
            // ignored
        }
        finally
        {
            Interlocked.Exchange(ref _liveViewTickInProgress, 0);
        }
    }

    /// <summary>
    /// Live View 是否启用
    /// </summary>
    [ObservableProperty] private bool _enableLiveView = true;

    /// <summary>
    /// Live View 刷新率（FPS），范围 1-60，默认 10
    /// </summary>
    [ObservableProperty] private double _liveViewRefreshRate = 30.0;

    partial void OnEnableLiveViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.EnableLiveView, value);
    }

    partial void OnLiveViewRefreshRateChanged(double value)
    {
        // 限制范围在 1 到 60 FPS 之间，不允许 0 以防止除零错误
        if (value < 1)
        {
            LiveViewRefreshRate = 1;
            return;
        }
        if (value > 120)
        {
            LiveViewRefreshRate = 120;
            return;
        }

        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.LiveViewRefreshRate, value);
        // 将 FPS 转换为间隔（秒）并触发事件
        UpdateLiveViewTimerInterval();
        var interval = 1.0 / value;
        LiveViewRefreshRateChanged?.Invoke(interval);
    }

    /// <summary>
    /// 获取当前刷新间隔（秒），用于定时器
    /// </summary>
    public double GetLiveViewRefreshInterval() => 1.0 / LiveViewRefreshRate;

    [ObservableProperty] private Bitmap? _liveViewImage;
    [ObservableProperty] private bool _isLiveViewExpanded = true;
    private WriteableBitmap? _liveViewWriteableBitmap;
    [ObservableProperty] private double _liveViewFps;
    private DateTime _liveViewFpsWindowStart = DateTime.UtcNow;
    private int _liveViewFrameCount;
    [ObservableProperty] private string _currentTaskName = "";

    private int _liveViewImageCount;
    private int _liveViewImageNewestCount;

    private const int LiveViewSemaphoreMaxCount = 5;
    private static readonly SemaphoreSlim _liveViewSemaphore = new(2, LiveViewSemaphoreMaxCount);

    private readonly WriteableBitmap?[] _liveViewImageCache = new WriteableBitmap?[LiveViewSemaphoreMaxCount];

    /// <summary>
    /// Live View 是否可见（已连接且有图像）
    /// </summary>
    public bool IsLiveViewVisible => EnableLiveView && IsConnected && LiveViewImage != null;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
        _liveViewNoImageLogged = false;
        LoggerHelper.Info(value ? "连接状态变更：已连接。" : "连接状态变更：未连接。");
    }

    partial void OnLiveViewImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
    }

    [RelayCommand]
    private void ToggleLiveViewExpanded()
    {
        IsLiveViewExpanded = !IsLiveViewExpanded;
    }

    public void PauseLiveView()
    {
        _liveViewTimer.Stop();
    }

    public void ResumeLiveView()
    {
        if (Processor.IsClosed || OperatingSystem.IsAndroid())
            return;

        UpdateLiveViewTimerInterval();
        _liveViewTimer.Start();
    }

    /// <summary>
    /// 更新当前任务名称
    /// </summary>
    public void SetCurrentTaskName(string taskName)
    {
        DispatcherHelper.PostOnMainThread(() => CurrentTaskName = taskName);
    }

    /// <summary>
    /// 更新 Live View 图像（仿 WPF：直接写入缓冲）
    /// </summary>
    public async Task UpdateLiveViewImageAsync(MaaImageBuffer? buffer)
    {
        if (!await _liveViewSemaphore.WaitAsync(0))
        {
            buffer?.Dispose();
            return;
        }

        try
        {
            var count = Interlocked.Increment(ref _liveViewImageCount);
            var index = count % _liveViewImageCache.Length;

            if (buffer == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LiveViewImage = null;
                    DisposeLiveViewBitmaps();
                    _liveViewImageNewestCount = 0;
                    _liveViewImageCount = 0;
                });
                return;
            }

            if (!buffer.TryGetRawData(out var rawData, out var width, out var height, out _))
            {
                return;
            }

            if (rawData == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return;
            }

            if (buffer.Channels is not (3 or 4))
            {
                return;
            }

            if (count <= _liveViewImageNewestCount)
            {
                return;
            }

            // 关键修复：在 UI 线程调用中使用 buffer，确保在使用期间不会被释放
            // 使用 Invoke 而不是 InvokeAsync，确保同步执行完成后再释放 buffer
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                    return;

                try
                {
                    // 再次验证指针有效性（防止在等待期间失效）
                    if (rawData != IntPtr.Zero && width > 0 && height > 0)
                    {
                        _liveViewImageCache[index] = WriteBgrToBitmap(rawData, width, height, buffer.Channels, _liveViewImageCache[index]);
                        LiveViewImage = _liveViewImageCache[index];
                    }
                }
                catch (Exception ex)
                {
            LoggerHelper.Warning($"实时画面写入位图失败：{ex.Message}");
                }
            });

            Interlocked.Exchange(ref _liveViewImageNewestCount, count);

            var now = DateTime.UtcNow;
            Interlocked.Increment(ref _liveViewFrameCount);
            var totalSeconds = (now - _liveViewFpsWindowStart).TotalSeconds;
            if (totalSeconds >= 1)
            {
                var frameCount = Interlocked.Exchange(ref _liveViewFrameCount, 0);
                _liveViewFpsWindowStart = now;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LiveViewFps = frameCount / totalSeconds;
                });
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"实时画面更新失败：{ex.Message}");
        }
        finally
        {
            buffer?.Dispose();
            _liveViewSemaphore.Release();
        }
    }


    private static WriteableBitmap WriteBgrToBitmap(IntPtr bgrData, int width, int height, int channels, WriteableBitmap? targetBitmap)
    {
        const int dstBytesPerPixel = 4;

        if (width <= 0 || height <= 0)
        {
            return targetBitmap
                ?? new WriteableBitmap(
                    new PixelSize(1, 1),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
        }

        if (targetBitmap == null
            || targetBitmap.PixelSize.Width != width
            || targetBitmap.PixelSize.Height != height)
        {
            targetBitmap?.Dispose();
            targetBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        using var framebuffer = targetBitmap.Lock();
        unsafe
        {
            var dstStride = framebuffer.RowBytes;
            if (dstStride <= 0)
            {
                return targetBitmap;
            }

            var dstPtr = (byte*)framebuffer.Address;

            if (channels == 4)
            {
                var srcStride = width * dstBytesPerPixel;
                var rowCopy = Math.Min(srcStride, dstStride);
                var srcPtr = (byte*)bgrData;
                for (var y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(srcPtr + y * srcStride, dstPtr + y * dstStride, dstStride, rowCopy);
                }

                return targetBitmap;
            }

            if (channels == 3)
            {
                var srcStride = width * 3;
                var rowBuffer = ArrayPool<byte>.Shared.Rent(width * dstBytesPerPixel);
                try
                {
                    var srcPtr = (byte*)bgrData;
                    var rowCopy = Math.Min(width * dstBytesPerPixel, dstStride);
                    for (var y = 0; y < height; y++)
                    {
                        var srcRow = srcPtr + y * srcStride;
                        for (var x = 0; x < width; x++)
                        {
                            var srcIndex = x * 3;
                            var dstIndex = x * dstBytesPerPixel;
                            rowBuffer[dstIndex] = srcRow[srcIndex];
                            rowBuffer[dstIndex + 1] = srcRow[srcIndex + 1];
                            rowBuffer[dstIndex + 2] = srcRow[srcIndex + 2];
                            rowBuffer[dstIndex + 3] = 255;
                        }

                        fixed (byte* rowPtr = rowBuffer)
                        {
                            Buffer.MemoryCopy(rowPtr, dstPtr + y * dstStride, dstStride, rowCopy);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rowBuffer);
                }

                return targetBitmap;
            }
        }

        return targetBitmap;
    }

    #endregion

    #region 配置切换

    /// <summary>
    /// 配置列表（直接引用 ConfigurationManager.Configs，与设置页面共享同一数据源）
    /// </summary>
    public IAvaloniaReadOnlyList<MFAConfiguration> ConfigurationList => ConfigurationManager.Configs;

    public event Action<DragItemViewModel, bool>? SetOptionRequested;

    public void RequestSetOption(DragItemViewModel item, bool value)
    {
        SetOptionRequested?.Invoke(item, value);
    }

    private void DisposeLiveViewBitmaps()
    {
        var activeBitmap = _liveViewWriteableBitmap;
        _liveViewWriteableBitmap = null;

        foreach (var bitmap in _liveViewImageCache)
        {
            if (bitmap != null && !ReferenceEquals(bitmap, activeBitmap))
                bitmap.Dispose();
        }

        Array.Fill(_liveViewImageCache, null);
        activeBitmap?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;

        _liveViewTimer.Stop();
        _liveViewTimer.Elapsed -= OnLiveViewTimerElapsed;
        _liveViewTimer.Dispose();

        _taskRunElapsedTimer.Stop();
        _taskRunElapsedTimer.Tick -= OnTaskRunElapsedTimerTick;

        _processorField.TaskQueue.CountChanged -= OnTaskQueueCountChanged;
        LanguageHelper.LanguageChanged -= OnLanguageChanged;

        _refreshCancellationTokenSource?.Cancel();
        _refreshCancellationTokenSource?.Dispose();
        _refreshCancellationTokenSource = null;

        LiveViewImage = null;
        DisposeLiveViewBitmaps();
        LiveViewRefreshRateChanged = null;
        SetOptionRequested = null;

        GC.SuppressFinalize(this);
    }

    [ObservableProperty] private string? _currentConfiguration = ConfigurationManager.GetCurrentConfiguration();

    partial void OnCurrentConfigurationChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !value.Equals(ConfigurationManager.GetCurrentConfiguration(), StringComparison.OrdinalIgnoreCase))
        {
            LoggerHelper.UserAction("切换配置", $"from={ConfigurationManager.GetCurrentConfiguration()} -> to={value}",
                operation: "SwitchConfiguration", instanceId: Processor.InstanceId, instanceName: InstanceName);
            ConfigurationManager.SwitchConfiguration(value);
        }

    }

    #endregion
}
