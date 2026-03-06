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

public partial class TaskQueueViewModel : ViewModelBase
{
    private readonly MaaProcessor _processorField;
    public MaaProcessor Processor => _processorField;

    public TaskQueueViewModel() : this(MaaProcessorManager.Instance.Current.InstanceId)
    {
    }

    public TaskQueueViewModel(string instanceId)
    {
        _processorField = new MaaProcessor(instanceId);
        _currentController = _processorField.InstanceConfiguration.GetValue(ConfigurationKeys.CurrentController, MaaControllerTypes.Adb, MaaControllerTypes.None, new UniversalEnumConverter<MaaControllerTypes>());
        // 初始化为当前控制器类型，避免首次 AutoDetectDevice 时用 interface.json 覆盖用户已保存的配置
        _lastAppliedControllerSettingsType = _currentController;
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

        IsRunning = _processorField.TaskQueue.Count > 0;
        _processorField.TaskQueue.CountChanged += OnTaskQueueCountChanged;

        // Re-initialize with the correct processor since base constructor might have used Current
        Initialize();
    }

    private void OnTaskQueueCountChanged(object? sender, ObservableQueue<MFATask>.CountChangedEventArgs e)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            IsRunning = e.NewValue > 0;
        });
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Idle))]
    private bool _isRunning;

    public bool Idle => !IsRunning;

    [ObservableProperty] private bool _isCompactMode = false;

    private bool _isSyncing = false;

    /// <summary>
    /// 标记当前 CurrentDevice 变更是否为程序内部触发（刷新/配置加载等），
    /// 为 false 时表示用户通过 ComboBox 手动选择设备
    /// </summary>
    private bool _suppressAutoConnect = false;

    /// <summary>
    /// 记录已应用过 interface.json 控制器设置的控制器类型，
    /// 避免每次刷新设备时都用 interface.json 的值覆盖用户配置
    /// </summary>
    private MaaControllerTypes? _lastAppliedControllerSettingsType;

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
        if (value != null && value.ControllerType != CurrentController)
        {
            CurrentController = value.ControllerType;
        }
    }

    /// <summary>
    /// 获取当前控制器的名称
    /// </summary>
    public string? GetCurrentControllerName()
    {
        return TaskLoader.GetControllerName(CurrentController, MaaProcessor.Interface);
    }

    public void UpdateResourcesForController(string? targetResource = null)
    {
        try
        {
            if (MaaProcessor.Interface == null)
            {
                LoggerHelper.Warning("MaaProcessor.Interface is not initialized yet.");
                CurrentResources = [];
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
            CurrentResources = new ObservableCollection<MaaInterface.MaaInterfaceResource>(filteredResources);

            if (!string.IsNullOrWhiteSpace(resourceToSelect) && CurrentResources.Any(r => r.Name == resourceToSelect))
            {
                CurrentResource = resourceToSelect;
            }
            else
            {
                CurrentResource = CurrentResources.FirstOrDefault()?.Name ?? "Default";
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"UpdateResourcesForController failed: {ex}");
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
                    ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(filteredControllers);
                }
                else
                {
                    // 过滤后为空则使用默认控制器
                    var defaultControllers = CreateDefaultControllers();
                    ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
                }
            }
            else
            {
                // 使用默认的Adb和Win32控制器
                var defaultControllers = CreateDefaultControllers();
                ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
            }

            // 根据当前控制器类型选择对应的控制器
            // 使用 PostOnMainThread 延迟设置 SelectedController，确保 ComboBox 已完成 ItemsSource 更新
            var targetController = ControllerOptions.FirstOrDefault(c => c.ControllerType == CurrentController)
                ?? ControllerOptions.FirstOrDefault();
            DispatcherHelper.PostOnMainThread(() =>
            {
                SelectedController = targetController;
                _isSyncing = false;
            });
        }
        catch (Exception e)
        {
            LoggerHelper.Error(e);
            // 出错时使用默认控制器
            var defaultControllers = CreateDefaultControllers();
            ControllerOptions = new ObservableCollection<MaaInterface.MaaResourceController>(defaultControllers);
            DispatcherHelper.PostOnMainThread(() =>
            {
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
            LoggerHelper.Error(e);
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
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, value.ToList().Select(model => model.InterfaceItem));
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
        if (IsRunning)
        {
            ToastHelper.Warn(LangKeys.ConfirmExitTitle.ToLocalization());
            LoggerHelper.Warning(LangKeys.ConfirmExitTitle.ToLocalization());
            return;
        }

        if (CurrentResources.Count == 0 || string.IsNullOrWhiteSpace(CurrentResource) || CurrentResources.All(r => r.Name != CurrentResource))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.ResourceNotSelected.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        var beforeTask = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
        var skipDeviceCheck = beforeTask.Contains("StartupSoftware", StringComparison.OrdinalIgnoreCase)
            || Instances.ConnectSettingsUserControlModel.AutoDetectOnConnectionFailed;

        if (!skipDeviceCheck)
        {
            if (CurrentController != MaaControllerTypes.PlayCover && CurrentDevice == null)
            {
                ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.DeviceNotSelected.ToLocalization());
                LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
                return;
            }

            if (CurrentController == MaaControllerTypes.Adb
                && CurrentDevice is AdbDeviceInfo adbInfo
                && string.IsNullOrWhiteSpace(adbInfo.AdbSerial))
            {
                ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.AdbAddressEmpty.ToLocalization());
                LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
                return;
            }
        }

        if (CurrentController == MaaControllerTypes.PlayCover
            && string.IsNullOrWhiteSpace(Processor.Config.PlayCover.PlayCoverAddress))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.PlayCoverAddressEmpty.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
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

        Processor.Start();
    }

    public void StopTask(Action? action = null)
    {
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

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var task in TaskItemViewModels)
            task.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var task in TaskItemViewModels)
            task.IsChecked = false;
    }

    [RelayCommand]
    private void AddTask()
    {
        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.AdbEditor.ToLocalization()).WithViewModel(dialog => new AddTaskDialogViewModel(dialog, Processor.TasksSource)).TryShow();
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
            if (presetTask.Enabled.HasValue)
                dragItem.IsCheckedWithNull = presetTask.Enabled.Value;

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

        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, TaskItemViewModels.ToList().Select(model => model.InterfaceItem));
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
            TaskItemViewModels.Add(item.Clone());
        }

        // 恢复特殊任务到列表末尾
        foreach (var special in specialTasks)
        {
            TaskItemViewModels.Add(special);
        }

        // 更新任务的资源支持状态
        UpdateTasksForResource(CurrentResource);

        // 保存配置
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, TaskItemViewModels.ToList().Select(model => model.InterfaceItem));
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
    [ObservableProperty] private object? _currentDevice;

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
        ChangedDevice(value);

        // 仅 ComboBox 手动选中设备时，根据"刷新后尝试连接"设置自动连接
        if (!_suppressAutoConnect
            && !_isSyncing
            && value != null
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
                    LoggerHelper.Warning($"Auto connect after device selection failed: {ex.Message}");
                }
            }, name: "选中设备后自动连接", catchException: true, shouldLog: true);
        }
    }

    public void ChangedDevice(object? value)
    {
        if (_isSyncing) return;
        var igoreToast = false;
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
        if (value is DesktopWindowInfo window)
        {
            if (!igoreToast) ToastHelper.Info(LangKeys.WindowSelectionMessage.ToLocalizationFormatted(false, ""), window.Name);
            var isSameWindow = Processor.Config.DesktopWindow.HWnd == window.Handle
                && Processor.Config.DesktopWindow.HWnd != IntPtr.Zero;
            Processor.Config.DesktopWindow.Name = window.Name;
            Processor.Config.DesktopWindow.HWnd = window.Handle;
            // 记录 ClassName 和 WindowName，下次启动时优先匹配
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.DesktopWindowClassName, window.ClassName);
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.DesktopWindowName, window.Name);
            // 正在连接或设备未变更时跳过 SetTasker，避免打断进行中的连接
            if (!Processor.IsConnecting && !isSameWindow)
                Task.Run(() => Processor.SetTasker());
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
                Task.Run(() => Processor.SetTasker());
            Processor.InstanceConfiguration.SetValue(ConfigurationKeys.AdbDevice, device);
        }
    }

    [ObservableProperty] private MaaControllerTypes _currentController = MaaControllerTypes.Adb;

    partial void OnCurrentControllerChanged(MaaControllerTypes value)
    {
        if (_isSyncing) return;
        Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentController, value.ToString());
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
            Devices = [];
            CurrentDevice = null;
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

    [RelayCommand]
    private void CustomAdb()
    {
        var deviceInfo = CurrentDevice as AdbDeviceInfo;

        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.AdbEditor.ToLocalization()).WithViewModel(dialog => new AdbEditorDialogViewModel(deviceInfo, dialog)).Dismiss().ByClickingBackground().TryShow();
    }

    [RelayCommand]
    private void EditPlayCover()
    {
        Instances.DialogManager.CreateDialog().WithTitle(LangKeys.PlayCoverEditor.ToLocalization())
            .WithViewModel(dialog => new PlayCoverEditorDialogViewModel(Processor.Config.PlayCover, dialog))
            .Dismiss().ByClickingBackground().TryShow();
    }

    private CancellationTokenSource? _refreshCancellationTokenSource;

    [RelayCommand]
    private async Task Reconnect()
    {
        if (CurrentResources.Count == 0 || string.IsNullOrWhiteSpace(CurrentResource) || CurrentResources.All(r => r.Name != CurrentResource))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.ResourceNotSelected.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        if (CurrentController != MaaControllerTypes.PlayCover && CurrentDevice == null)
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.DeviceNotSelected.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        if (CurrentController == MaaControllerTypes.Adb
            && CurrentDevice is AdbDeviceInfo adbInfo
            && string.IsNullOrWhiteSpace(adbInfo.AdbSerial))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.AdbAddressEmpty.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
            return;
        }

        if (CurrentController == MaaControllerTypes.PlayCover
            && string.IsNullOrWhiteSpace(Processor.Config.PlayCover.PlayCoverAddress))
        {
            ToastHelper.Warn(LangKeys.CannotStart.ToLocalization(), LangKeys.PlayCoverAddressEmpty.ToLocalization());
            LoggerHelper.Warning(LangKeys.CannotStart.ToLocalization());
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
            LoggerHelper.Warning($"Reconnect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Refresh()
    {
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
                        LoggerHelper.Warning($"Auto connect after refresh failed: {ex.Message}");
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
        // DisposableObservableCollection 会自动调用所有元素的 Dispose()
        Processor.ClearLogs();
    }

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    [RelayCommand]
    private async Task Export()
    {
        var storageProvider = Instances.RootView?.StorageProvider;
        await FileLogExporter.CompressRecentLogs(storageProvider);
    }

    public void AutoDetectDevice(CancellationToken token = default)
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

        ToastHelper.Info(GetDetectionMessage(controllerType));
        SetConnected(false);
        token.ThrowIfCancellationRequested();
        var (devices, index) = isAdb ? DetectAdbDevices() : DetectWin32Windows();
        token.ThrowIfCancellationRequested();
        UpdateDeviceList(devices, index);
        token.ThrowIfCancellationRequested();
        HandleControllerSettings(controllerType);
        token.ThrowIfCancellationRequested();
        UpdateConnectionStatus(devices.Count > 0, controllerType);
    }

    private string GetDetectionMessage(MaaControllerTypes controllerType) =>
        controllerType == MaaControllerTypes.Adb
            ? "EmulatorDetectionStarted".ToLocalization()
            : "WindowDetectionStarted".ToLocalization();

    private (ObservableCollection<object> devices, int index) DetectAdbDevices()
    {
        var devices = MaaProcessor.Toolkit.AdbDevice.Find();
        var index = CalculateAdbDeviceIndex(devices);
        return (new(devices), index);
    }

    private int CalculateAdbDeviceIndex(IList<AdbDeviceInfo> devices)
    {
        if (CurrentDevice is AdbDeviceInfo info)
        {
            LoggerHelper.Info($"Current device: {JsonConvert.SerializeObject(info)}");

            // 使用指纹匹配设备
            var matchedDevices = devices
                .Where(device => device.MatchesFingerprint(info))
                .ToList();

            LoggerHelper.Info($"Found {matchedDevices.Count} devices matching fingerprint");

            // 多匹配时排序：先比AdbSerial前缀（冒号前），再比设备名称
            if (matchedDevices.Any())
            {
                matchedDevices.Sort((a, b) =>
                {
                    var aPrefix = a.AdbSerial.Split(':', 2)[0];
                    var bPrefix = b.AdbSerial.Split(':', 2)[0];
                    int prefixCompare = string.Compare(aPrefix, bPrefix, StringComparison.Ordinal);
                    return prefixCompare != 0 ? prefixCompare : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
                return devices.IndexOf(matchedDevices.First());
            }
        }

        var config = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
        if (string.IsNullOrWhiteSpace(config)) return 0;

        var targetNumber = ExtractNumberFromEmulatorConfig(config);
        return devices.Select((d, i) =>
                TryGetIndexFromConfig(d.Config, out var index) && index == targetNumber ? i : -1)
            .FirstOrDefault(i => i >= 0);
    }


    public static int ExtractNumberFromEmulatorConfig(string emulatorConfig)
    {
        var match = Regex.Match(emulatorConfig, @"\d+");

        if (match.Success)
        {
            return int.Parse(match.Value);
        }

        return 0;
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
        LoggerHelper.Info(JsonConvert.SerializeObject(parts));
        return parts.Length == 2 && int.TryParse(parts[1], out port);
    }

    private (ObservableCollection<object> devices, int index) DetectWin32Windows()
    {
        Thread.Sleep(500);
        var windows = MaaProcessor.Toolkit.Desktop.Window.Find().Where(win => !string.IsNullOrWhiteSpace(win.Name)).ToList();
        var (index, filtered) = CalculateWindowIndex(windows);
        return (new(filtered), index);
    }

    private (int index, List<DesktopWindowInfo> afterFiltered) CalculateWindowIndex(List<DesktopWindowInfo> windows)
    {
        var controller = MaaProcessor.Interface?.Controller?
            .FirstOrDefault(c => c.Type?.Equals("win32", StringComparison.OrdinalIgnoreCase) == true);

        if (controller?.Win32 == null)
        {
            var idx = MatchPreviousWindow(windows);
            return (idx >= 0 ? idx : Math.Max(0, windows.FindIndex(win => !string.IsNullOrWhiteSpace(win.Name))), windows);
        }

        var filtered = windows.Where(win =>
            !string.IsNullOrWhiteSpace(win.Name)).ToList();

        filtered = ApplyRegexFilters(filtered, controller.Win32);

        var matchedIdx = MatchPreviousWindow(filtered);
        return (matchedIdx >= 0 ? matchedIdx : (filtered.Count > 0 ? 0 : 0), filtered.ToList());
    }

    /// <summary>
    /// 在窗口列表中匹配上次选中的窗口（优先 ClassName+Name 完全匹配，其次 ClassName 匹配）。
    /// 当 CurrentDevice 为 null（启动初始化时）会从保存的配置中读取上次选中的窗口信息进行匹配，
    /// 后续刷新时 CurrentDevice 已有值，不会走配置回退逻辑。
    /// </summary>
    private int MatchPreviousWindow(List<DesktopWindowInfo> windows)
    {
        if (windows.Count == 0)
            return -1;

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
    {
        var filtered = windows;
        if (!string.IsNullOrWhiteSpace(win32.WindowRegex))
        {
            var regex = new Regex(win32.WindowRegex);
            filtered = filtered.Where(w => regex.IsMatch(w.Name)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(win32.ClassRegex))
        {
            var regex = new Regex(win32.ClassRegex);
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
                Devices = devices;
                if (devices.Count > index)
                    CurrentDevice = devices[index];
                else
                    CurrentDevice = null;
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

        // 同一控制器类型只应用一次 interface.json 的设置，避免每次刷新都覆盖用户配置
        if (_lastAppliedControllerSettingsType == controllerType)
            return;
        _lastAppliedControllerSettingsType = controllerType;

        var controller = MaaProcessor.Interface?.Controller?
            .FirstOrDefault(c => c.Type?.Equals(controllerType.ToJsonKey(), StringComparison.OrdinalIgnoreCase) == true);

        if (controller == null) return;

        var isAdb = controllerType == MaaControllerTypes.Adb;
        HandleInputSettings(controller, isAdb);
        HandleScreenCapSettings(controller, isAdb);
    }

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
            _ => null
        };
    }

    /// <summary>
    /// 解析 Win32ScreencapMethod，支持旧版 long 格式和新版 string 格式
    /// </summary>
    private static Win32ScreencapMethod? ParseWin32ScreencapMethod(object? value)
    {
        if (value == null) return null;

        // 新版 string 格式（枚举名）
        if (value is string strValue)
        {
            if (Enum.TryParse<Win32ScreencapMethod>(strValue, ignoreCase: true, out var result))
                return result;
            return null;
        }

        // 旧版 long 格式
        var longValue = Convert.ToInt64(value);
        return longValue switch
        {
            1 => Win32ScreencapMethod.GDI,
            2 => Win32ScreencapMethod.FramePool,
            4 => Win32ScreencapMethod.DXGI_DesktopDup,
            8 => Win32ScreencapMethod.DXGI_DesktopDup_Window,
            16 => Win32ScreencapMethod.PrintWindow,
            32 => Win32ScreencapMethod.ScreenDC,
            _ => null
        };
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
            var screenCap = controller.Win32?.ScreenCap;
            if (screenCap == null) return;
            var parsed = ParseWin32ScreencapMethod(screenCap);
            if (parsed != null)
                Instances.ConnectSettingsUserControlModel.Win32ControlScreenCapType = parsed.Value;
        }
    }

    private void UpdateConnectionStatus(bool hasDevices, MaaControllerTypes controllerType)
    {
        if (!hasDevices)
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
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover,
            _ => LangKeys.Window
        };
        ToastHelper.Warn(string.Format(
            LangKeys.TaskStackError.ToLocalization(),
            targetKey.ToLocalization(),
            ex.Message));

        LoggerHelper.Error(ex);
    }

    public void TryReadAdbDeviceFromConfig(bool inTask = true, bool refresh = false, bool allowAutoDetect = true)
    {
        if (CurrentController == MaaControllerTypes.PlayCover)
        {
            SetConnected(false);
            return;
        }

        if (!allowAutoDetect)
        {
            if (CurrentController != MaaControllerTypes.Adb
                || !Processor.InstanceConfiguration.GetValue(ConfigurationKeys.RememberAdb, true)
                || !Processor.InstanceConfiguration.TryGetValue(ConfigurationKeys.AdbDevice, out AdbDeviceInfo savedDevice,
                    new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>()))
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
                return;
            }

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
            ChangedDevice(savedDevice);
            return;
        }

        if (refresh
            || CurrentController != MaaControllerTypes.Adb
            || !Processor.InstanceConfiguration.GetValue(ConfigurationKeys.RememberAdb, true)
            || Processor.Config.AdbDevice.AdbPath != "adb"
            || !Processor.InstanceConfiguration.TryGetValue(ConfigurationKeys.AdbDevice, out AdbDeviceInfo savedDevice1,
                new UniversalEnumConverter<AdbInputMethods>(), new UniversalEnumConverter<AdbScreencapMethods>()))
        {
            _refreshCancellationTokenSource?.Cancel();
            _refreshCancellationTokenSource = new CancellationTokenSource();
            if (inTask)
                TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token), name: "刷新设备");
            else
                AutoDetectDevice(_refreshCancellationTokenSource.Token);
            return;
        }
        // 检查是否启用指纹匹配功能
        var useFingerprintMatching = Processor.InstanceConfiguration.GetValue(ConfigurationKeys.UseFingerprintMatching, true);

        if (useFingerprintMatching)
        {
            // 使用指纹匹配设备，而不是直接使用保存的设备信息
            // 因为雷电模拟器等的AdbSerial每次启动都会变化
            LoggerHelper.Info("Reading saved ADB device from configuration, using fingerprint matching.");
            LoggerHelper.Info($"Saved device fingerprint: {savedDevice1.GenerateDeviceFingerprint()}");

            // 搜索当前可用的设备
            var currentDevices = MaaProcessor.Toolkit.AdbDevice.Find();

            // 尝试通过指纹匹配找到对应的设备（当任一方index为-1时不比较index）
            AdbDeviceInfo? matchedDevice = null;
            foreach (var device in currentDevices)
            {
                if (device.MatchesFingerprint(savedDevice1))
                {
                    matchedDevice = device;
                    LoggerHelper.Info($"Found matching device by fingerprint: {device.Name} ({device.AdbSerial})");
                    break;
                }
            }

            if (matchedDevice != null)
            {
                // 使用新搜索到的设备信息（AdbSerial等可能已更新）
                DispatcherHelper.RunOnMainThread(() =>
                {
                    _suppressAutoConnect = true;
                    try
                    {
                        Devices = new ObservableCollection<object>(currentDevices);
                        CurrentDevice = matchedDevice;
                    }
                    finally
                    {
                        _suppressAutoConnect = false;
                    }
                });
                ChangedDevice(matchedDevice);
            }
            else
            {
                // 没有找到匹配的设备，执行自动检测
                LoggerHelper.Info("No matching device found by fingerprint, performing auto detection.");
                _refreshCancellationTokenSource?.Cancel();
                _refreshCancellationTokenSource = new CancellationTokenSource();
                if (inTask)
                    TaskManager.RunTask(() => AutoDetectDevice(_refreshCancellationTokenSource.Token), name: "刷新设备");
                else
                    AutoDetectDevice(_refreshCancellationTokenSource.Token);
            }
        }
        else
        {
            // 不使用指纹匹配，直接使用保存的设备信息
            LoggerHelper.Info("Reading saved ADB device from configuration, fingerprint matching disabled.");
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
            ChangedDevice(savedDevice1);
        }
    }

    #endregion

    #region 资源

    [ObservableProperty] private ObservableCollection<MaaInterface.MaaInterfaceResource> _currentResources = [];
    private string _currentResource = string.Empty;

    public string CurrentResource
    {
        get => _currentResource;
        set
        {
            if (string.Equals(_currentResource, value, StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                // SetTasker 内部会同步等待旧 Tasker 停止，移到后台线程避免阻塞 UI
                Task.Run(() => Processor.SetTasker());
            }

            SetNewProperty(ref _currentResource, value);
            HandlePropertyChanged(ConfigurationKeys.Resource, value);

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

    /// <summary>
    /// 判断是否为真正的资源选项项（排除全局选项项和控制器选项项）
    /// </summary>
    private static bool IsRealResourceOptionItem(DragItemViewModel item) =>
        item.IsResourceOptionItem &&
        item.ResourceItem?.Name != "__GlobalOption__" &&
        item.ResourceItem?.Name?.StartsWith("__ControllerOption__") != true;

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
                return existing;
            if (savedDict?.TryGetValue(optionName, out var saved) == true)
            {
                return new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = saved.Name,
                    Index = saved.Index,
                    Data = saved.Data != null ? new Dictionary<string, string?>(saved.Data) : null,
                    SelectedCases = saved.SelectedCases != null ? new List<string>(saved.SelectedCases) : null,
                };
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
            SubOptions = opt.SubOptions != null ? CloneSubOptions(opt.SubOptions) : null
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
    private bool _liveViewNoImageLogged;

    private void UpdateLiveViewTimerInterval()
    {
        var interval = GetLiveViewRefreshInterval();
        _liveViewTimer.Interval = Math.Max(1, interval * 1000);
    }

    private void OnLiveViewTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
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
                var status = Processor.PostScreencap();
                if (status != MaaJobStatus.Succeeded)
                {
                    if (Processor.HandleScreencapStatus(status))
                    {
                        SetConnected(false);
                        DispatcherHelper.PostOnMainThread(() =>
                        {
                            AddLogByKey(LangKeys.ScreencapTimeoutDisconnected, Brushes.OrangeRed, changeColor: false);
                        });
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
                        LoggerHelper.Warning($"[LiveView] 已连接但获取画面为空 (截图方式: {screencapType}, 控制器: {controllerType}). {reason}");
                        AddLog($"warn: {LangKeys.LiveViewNoImageWarning.ToLocalizationFormatted(false, screencapType, reason)}", (IBrush?)null);
                    }
                    return;
                }

                _liveViewNoImageLogged = false;
                _ = UpdateLiveViewImageAsync(buffer);
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

    private static int _liveViewSemaphoreCurrentCount = 2;
    private const int LiveViewSemaphoreMaxCount = 5;
    private static int _liveViewSemaphoreFailCount = 0;
    private static readonly SemaphoreSlim _liveViewSemaphore = new(_liveViewSemaphoreCurrentCount, LiveViewSemaphoreMaxCount);

    private readonly WriteableBitmap?[] _liveViewImageCache = new WriteableBitmap?[LiveViewSemaphoreMaxCount];

    /// <summary>
    /// Live View 是否可见（已连接且有图像）
    /// </summary>
    public bool IsLiveViewVisible => EnableLiveView && IsConnected && LiveViewImage != null;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLiveViewVisible));
        _liveViewNoImageLogged = false;
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
            if (++_liveViewSemaphoreFailCount < 3)
            {
                buffer?.Dispose();
                return;
            }

            _liveViewSemaphoreFailCount = 0;

            if (_liveViewSemaphoreCurrentCount < LiveViewSemaphoreMaxCount)
            {
                _liveViewSemaphoreCurrentCount++;
                _liveViewSemaphore.Release();
                LoggerHelper.Info($"LiveView Semaphore Full, increase semaphore count to {_liveViewSemaphoreCurrentCount}");
            }

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
                    _liveViewWriteableBitmap?.Dispose();
                    _liveViewWriteableBitmap = null;
                    Array.Fill(_liveViewImageCache, null);
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
                    LoggerHelper.Warning($"LiveView WriteBgrToBitmap failed: {ex.Message}");
                }
            });

            Interlocked.Exchange(ref _liveViewImageNewestCount, count);
            _liveViewSemaphoreFailCount = 0;

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
            LoggerHelper.Warning($"LiveView update failed: {ex.Message}");
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

    [ObservableProperty] private string? _currentConfiguration = ConfigurationManager.GetCurrentConfiguration();

    partial void OnCurrentConfigurationChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !value.Equals(ConfigurationManager.GetCurrentConfiguration(), StringComparison.OrdinalIgnoreCase))
        {
            ConfigurationManager.SwitchConfiguration(value);
        }

    }

    #endregion
}
