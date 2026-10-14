using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace MFAAvalonia.Helper.ValueType;

public enum TaskRunState
{
    None,
    Queued,
    Running,
    Succeeded,
    Failed,
    Stopped,
    Skipped
}

public partial class DragItemViewModel : ObservableObject
{
    [JsonIgnore]
    public TaskQueueViewModel? OwnerViewModel { get; set; }

    public DragItemViewModel(MaaInterface.MaaInterfaceTask? interfaceItem)
    {
        InterfaceItem = interfaceItem;
        if (interfaceItem != null)
        {
            interfaceItem.InitializeIcon();
        }
        UpdateDisplayName();
        UpdateIconFromInterfaceItem();
        InitializeSupportStatus();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// 构造函数，用于创建全局资源设置项
    /// </summary>
    /// <param name="resource">资源配置</param>
    public DragItemViewModel(MaaInterface.MaaInterfaceResource resource)
    {
        ResourceItem = resource;
        IsResourceOptionItem = true;
        // 使用资源的 Label 解析本地化名称，无 Label 时回退到"资源预设配置"
        Name = GetResourceOptionDisplayName(resource);

        // 设置图标
        ResolvedIcon = resource.ResolvedIcon;
        HasIcon = resource.HasIcon;

        // 全局资源设置项默认选中且不可更改
        _isCheckedWithNull = true; // null 表示选中但不可更改
        _isInitialized = true;

        LanguageHelper.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty] private string _name = string.Empty;

    /// <summary>验证不通过时标记为 true，用于 UI 红圈提示</summary>
    [ObservableProperty] [JsonIgnore] private bool _hasValidationError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunStatusVisible))]
    [NotifyPropertyChangedFor(nameof(IsRunQueued))]
    [NotifyPropertyChangedFor(nameof(IsRunRunning))]
    [NotifyPropertyChangedFor(nameof(IsRunSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsRunFailed))]
    [NotifyPropertyChangedFor(nameof(IsRunStopped))]
    [NotifyPropertyChangedFor(nameof(IsRunSkipped))]
    [NotifyPropertyChangedFor(nameof(IsRunElapsedVisible))]
    [JsonIgnore]
    private TaskRunState _runState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunElapsedText))]
    [JsonIgnore]
    private TimeSpan _runElapsed;

    [ObservableProperty] [JsonIgnore] private int _completedRunCount;
    [ObservableProperty] [JsonIgnore] private int _totalRunCount;
    [ObservableProperty] [JsonIgnore] private string? _runErrorMessage;

    [JsonIgnore] public DateTimeOffset? RunStartedAt { get; set; }
    [JsonIgnore] public long RunId { get; set; }

    [JsonIgnore] public bool IsRunStatusVisible => RunState != TaskRunState.None;
    [JsonIgnore] public bool IsRunQueued => RunState == TaskRunState.Queued;
    [JsonIgnore] public bool IsRunRunning => RunState == TaskRunState.Running;
    [JsonIgnore] public bool IsRunSucceeded => RunState == TaskRunState.Succeeded;
    [JsonIgnore] public bool IsRunFailed => RunState == TaskRunState.Failed;
    [JsonIgnore] public bool IsRunStopped => RunState == TaskRunState.Stopped;
    [JsonIgnore] public bool IsRunSkipped => RunState == TaskRunState.Skipped;
    [JsonIgnore] public bool IsRunElapsedVisible =>
        RunState is TaskRunState.Running or TaskRunState.Succeeded or TaskRunState.Failed or TaskRunState.Stopped;

    [JsonIgnore]
    public string RunElapsedText => RunElapsed.TotalHours >= 1
        ? $"{(int)RunElapsed.TotalHours}:{RunElapsed.Minutes:00}:{RunElapsed.Seconds:00}"
        : $"{RunElapsed.Minutes:00}:{RunElapsed.Seconds:00}";

    /// <summary>解析后的图标路径（用于 UI 绑定）</summary>
    [ObservableProperty] private string? _resolvedIcon;

    /// <summary>是否有图标</summary>
    [ObservableProperty] private bool _hasIcon;


    private bool? _isCheckedWithNull = false;
    private bool _isInitialized;

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether the key is checked with null.
    /// </summary>
    [JsonIgnore]
    public bool? IsCheckedWithNull
    {
        get => _isCheckedWithNull;
        set
        {
            if (!_isInitialized)
            {
                _isInitialized = true;
                SetProperty(ref _isCheckedWithNull, value);
                if (InterfaceItem != null) InterfaceItem.Check = IsChecked;
            }
            else
            {
                SetProperty(ref _isCheckedWithNull, value);
                if (InterfaceItem != null)
                    InterfaceItem.Check = _isCheckedWithNull;

                if (ConfigurationManager.IsSwitching) return;

                (OwnerViewModel?.Processor.InstanceConfiguration ?? ConfigurationManager.CurrentInstance).SetValue(ConfigurationKeys.TaskItems,
                    (OwnerViewModel ?? Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel)?.TaskItemViewModels.Where(m => !m.IsResourceOptionItem).Select(model => model.InterfaceItem).ToList());
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether the key is checked.
    /// </summary>
    public bool IsChecked
    {
        get => IsCheckedWithNull != false;
        set => IsCheckedWithNull = value;
    }


    private bool _enableSetting;

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether the setting enabled.
    /// </summary>
    [JsonIgnore]
    public bool EnableSetting
    {
        get => _enableSetting;
        set
        {
            SetProperty(ref _enableSetting, value);
            OwnerViewModel?.RequestSetOption(this, value);
        }
    }

    private MaaInterface.MaaInterfaceTask? _interfaceItem;

    public MaaInterface.MaaInterfaceTask? InterfaceItem
    {
        get => _interfaceItem;
        set
        {
            if (value != null)
            {
                IsVisible = value is { Advanced.Count: > 0 } || value is { Option.Count: > 0 } || value.Repeatable == true || !string.IsNullOrWhiteSpace(value.Description) || value.Document is { Count: > 0 };
                IsCheckedWithNull = value.Check;
            }

            SetProperty(ref _interfaceItem, value);
            UpdateDisplayName();
            UpdateIconFromInterfaceItem();
        }
    }

    [ObservableProperty] private bool _isVisible = true;

    [JsonIgnore]
    public bool HasMobileOptions => IsResourceOptionItem
        ? ResourceItem?.SelectOptions is { Count: > 0 }
        : InterfaceItem?.Option is { Count: > 0 }
          || InterfaceItem?.Advanced is { Count: > 0 }
          || InterfaceItem?.Repeatable == true;

    /// <summary>
    /// 指示这是否是一个全局资源设置项。
    /// 全局资源设置项的 checkbox 默认选中且不可更改，不参与任务执行，
    /// 但其 option 生成的参数会参与到所有任务的 MaaToken merge 中。
    /// </summary>
    [ObservableProperty] [JsonIgnore] private bool _isResourceOptionItem = false;

    /// <summary>
    /// 对应的资源配置（仅当 IsResourceOptionItem 为 true 时有效）
    /// </summary>
    [JsonIgnore]
    public MaaInterface.MaaInterfaceResource? ResourceItem { get; set; }
    
    /// <summary>
    /// 指示任务是否支持当前选中的资源包。
    /// 当资源包变化时，此属性会被更新。
    /// </summary>
    [ObservableProperty] [JsonIgnore] private bool _isResourceSupported = true;

    /// <summary>
    /// 指示任务是否支持当前选中的控制器。
    /// 当控制器变化时，此属性会被更新。
    /// </summary>
    [ObservableProperty] [JsonIgnore] private bool _isControllerSupported = true;

    /// <summary>
    /// 指示任务是否同时支持当前资源包与控制器。
    /// </summary>
    [ObservableProperty] [JsonIgnore] private bool _isTaskSupported = true;

    /// <summary>
    /// 检查任务是否支持指定的资源包
    /// </summary>
    /// <param name="resourceName">资源包名称</param>
    /// <returns>如果任务支持该资源包或未指定资源限制，则返回 true</returns>
    public bool SupportsResource(string? resourceName)
    {
        return MaaInterfaceActivationHelper.IsTaskSupportedByResource(
            MaaProcessor.Interface,
            InterfaceItem,
            resourceName);
    }

    /// <summary>
    /// 检查任务是否支持指定的控制器
    /// </summary>
    /// <param name="controllerName">控制器名称</param>
    /// <returns>如果任务支持该控制器或未指定控制器限制，则返回 true</returns>
    public bool SupportsController(string? controllerName)
    {
        return MaaInterfaceActivationHelper.IsTaskSupportedByController(
            MaaProcessor.Interface,
            InterfaceItem,
            controllerName);
    }

    /// <summary>
    /// 更新任务对指定资源包的支持状态
    /// </summary>
    /// <param name="resourceName">资源包名称</param>
    public void UpdateResourceSupport(string? resourceName)
    {
        IsResourceSupported = SupportsResource(resourceName);
        UpdateTaskSupport();
    }

    /// <summary>
    /// 更新任务对指定控制器的支持状态
    /// </summary>
    /// <param name="controllerName">控制器名称</param>
    public void UpdateControllerSupport(string? controllerName)
    {
        IsControllerSupported = SupportsController(controllerName);
        UpdateTaskSupport();
    }

    private void UpdateTaskSupport()
    {
        IsTaskSupported = IsResourceSupported && IsControllerSupported;
    }

    private void InitializeSupportStatus()
    {
        if (IsResourceOptionItem)
            return;

        try
        {
            var resourceName = (OwnerViewModel ?? Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel)?.CurrentResource;
            UpdateResourceSupport(resourceName);

            var controllerName = GetCurrentControllerName();
            UpdateControllerSupport(controllerName);
        }
        catch (Exception)
        {
            UpdateTaskSupport();
        }
    }

    private string? GetCurrentControllerName()
    {
        var currentControllerType = (OwnerViewModel ?? Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel)?.CurrentController
            ?? MaaControllerTypes.None;
        return MaaInterfaceActivationHelper.ResolveControllerName(MaaProcessor.Interface, currentControllerType);
    }
    
    private void UpdateContent()
    {
        UpdateDisplayName();
        if (IsResourceOptionItem && ResourceItem != null)
        {
            ResolvedIcon = ResourceItem.ResolvedIcon;
            HasIcon = ResourceItem.HasIcon;
            return;
        }
        UpdateIconFromInterfaceItem();
    }

    private static string GetResourceOptionDisplayName(MaaInterface.MaaInterfaceResource resource)
    {
        // 合成的特殊资源项直接使用 MFA 自身的 i18n，不走 interface 协议的 $-前缀解析
        if (resource.Name == "__GlobalOption__")
            return LangKeys.GlobalOption.ToLocalization();
        if (resource.Name?.StartsWith("__ControllerOption__") == true)
            return LangKeys.ControllerPresetConfig.ToLocalization();

        // 普通资源项走 interface 协议的 i18n
        return string.IsNullOrWhiteSpace(resource.Label)
            ? LangKeys.ResourcePresetConfig.ToLocalization()
            : LanguageHelper.GetLocalizedDisplayName(resource.Label, resource.Name ?? LangKeys.ResourcePresetConfig);
    }

    private void UpdateDisplayName()
    {
        if (IsResourceOptionItem && ResourceItem != null)
        {
            Name = GetResourceOptionDisplayName(ResourceItem);
            return;
        }

        if (InterfaceItem == null)
        {
            Name = LangKeys.Unnamed.ToLocalization();
            return;
        }

        var displayName = !string.IsNullOrWhiteSpace(InterfaceItem.Remark)
            ? InterfaceItem.Remark!
            : !string.IsNullOrWhiteSpace(InterfaceItem.DisplayNameOverride)
                ? InterfaceItem.DisplayNameOverride!
                : LanguageHelper.GetLocalizedDisplayName(InterfaceItem.DisplayName, InterfaceItem.Name ?? LangKeys.Unnamed);

        Name = displayName;
    }

    public void RefreshDisplayName()
    {
        UpdateDisplayName();
    }

    private void UpdateIconFromInterfaceItem()
    {
        if (InterfaceItem != null)
        {
            ResolvedIcon = InterfaceItem.ResolvedIcon;
            HasIcon = InterfaceItem.HasIcon;
        }
        else
        {
            ResolvedIcon = null;
            HasIcon = false;
        }
    }

    private void OnLanguageChanged(object sender, EventArgs e)
    {
        UpdateContent();
    }

    /// <summary>
    /// Creates a deep copy of the current <see cref="DragItemViewModel"/> instance.
    /// </summary>
    /// <returns>A new <see cref="DragItemViewModel"/> instance that is a deep copy of the current instance.</returns>
    public DragItemViewModel Clone()
    {
        DragItemViewModel clone;

        if (IsResourceOptionItem && ResourceItem != null)
        {
            // 克隆资源设置项
            clone = new DragItemViewModel(ResourceItem) { OwnerViewModel = this.OwnerViewModel };
        }
        else
        {
            // 克隆普通任务项
            MaaInterface.MaaInterfaceTask? clonedInterfaceItem = InterfaceItem?.Clone();
            clone = new(clonedInterfaceItem) { OwnerViewModel = this.OwnerViewModel };
        }

        // Copy all other properties to the new instance
        clone.Name = this.Name;
        clone.IsCheckedWithNull = this.IsCheckedWithNull;
        clone.EnableSetting = this.EnableSetting;
        clone.IsVisible = this.IsVisible;
        clone.IsResourceSupported = this.IsResourceSupported;
        clone.IsControllerSupported = this.IsControllerSupported;
        clone.IsTaskSupported = this.IsTaskSupported;
        clone.ResolvedIcon = this.ResolvedIcon;
        clone.HasIcon = this.HasIcon;
        clone.IsResourceOptionItem = this.IsResourceOptionItem;

        return clone;
    }
}
