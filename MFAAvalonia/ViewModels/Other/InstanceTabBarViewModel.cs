using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.Other;

public partial class InstanceTabBarViewModel : ViewModelBase
{
    public ObservableCollection<InstanceTabViewModel> Tabs { get; } = new();
    public ObservableCollection<RecentClosedInstanceItem> RecentClosedTabs { get; } = new();
    public string DropdownSearchWatermark => "InstanceDropdownSearch".ToLocalization();
    public string OpenConfigsHeaderText => "InstanceDropdownOpenConfigs".ToLocalization();
    public string RecentClosedHeaderText => "InstanceDropdownRecentClosed".ToLocalization();
    public string NoOpenConfigsText => "InstanceDropdownNoOpenConfigs".ToLocalization();
    public string NoRecentClosedText => "InstanceDropdownNoRecentClosed".ToLocalization();
    public string CloseConfigTooltipText => "InstanceDropdownCloseConfig".ToLocalization();
    public bool HasRecentClosedItems => RecentClosedTabs.Count > 0;
    public bool ShowRecentClosedSection => HasRecentClosedItems && IsRecentClosedExpanded;

    private bool _isReloading;

    [ObservableProperty] private InstanceTabViewModel? _activeTab;
    [ObservableProperty] private bool _isDropdownOpen;
    [ObservableProperty] private bool _hasInstancePresets;
    [ObservableProperty] private List<MaaInterface.MaaInterfacePreset>? _instancePresets;
    [ObservableProperty] private bool _isAddMenuOpen;
    [ObservableProperty] private string _presetSearchText = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isOpenTabsExpanded = true;
    [ObservableProperty] private bool _isRecentClosedExpanded = true;
    [ObservableProperty] private bool _showOpenTabsEmptyState;
    [ObservableProperty] private bool _showRecentClosedEmptyState;

    public ObservableCollection<InstanceTabViewModel> FilteredTabs { get; } = new();
    public ObservableCollection<RecentClosedInstanceItem> FilteredRecentClosedTabs { get; } = new();
    public ObservableCollection<MaaInterface.MaaInterfacePreset> FilteredInstancePresets { get; } = new();

    partial void OnIsDropdownOpenChanged(bool value)
    {
        if (value)
        {
            SearchText = string.Empty;
            RefreshFilteredTabs();
            RefreshFilteredRecentClosedTabs();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshFilteredTabs();
        RefreshFilteredRecentClosedTabs();
    }

    partial void OnIsOpenTabsExpandedChanged(bool value)
    {
        ShowOpenTabsEmptyState = value && FilteredTabs.Count == 0;
    }

    partial void OnIsRecentClosedExpandedChanged(bool value)
    {
        ShowRecentClosedEmptyState = value && FilteredRecentClosedTabs.Count == 0;
        OnPropertyChanged(nameof(ShowRecentClosedSection));
    }

    partial void OnPresetSearchTextChanged(string value)
    {
        RefreshFilteredInstancePresets();
    }

    partial void OnIsAddMenuOpenChanged(bool value)
    {
        if (value)
        {
            PresetSearchText = string.Empty;
            RefreshFilteredInstancePresets();
        }
    }

    private void RefreshFilteredTabs()
    {
        FilteredTabs.Clear();
        var query = SearchText?.Trim() ?? string.Empty;
        foreach (var tab in Tabs)
        {
            if (string.IsNullOrEmpty(query) || tab.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTabs.Add(tab);
            }
        }

        ShowOpenTabsEmptyState = IsOpenTabsExpanded && FilteredTabs.Count == 0;
    }

    private void RefreshFilteredRecentClosedTabs()
    {
        FilteredRecentClosedTabs.Clear();
        var query = SearchText?.Trim() ?? string.Empty;
        foreach (var item in RecentClosedTabs)
        {
            if (string.IsNullOrEmpty(query)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.MetaText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredRecentClosedTabs.Add(item);
            }
        }

        ShowRecentClosedEmptyState = IsRecentClosedExpanded && FilteredRecentClosedTabs.Count == 0;
        OnPropertyChanged(nameof(HasRecentClosedItems));
        OnPropertyChanged(nameof(ShowRecentClosedSection));
    }

    private void RefreshFilteredInstancePresets()
    {
        FilteredInstancePresets.Clear();
        var presets = InstancePresets;
        if (presets == null || presets.Count == 0)
        {
            return;
        }

        var query = PresetSearchText?.Trim() ?? string.Empty;
        foreach (var preset in presets)
        {
            if (string.IsNullOrEmpty(query)
                || (!string.IsNullOrWhiteSpace(preset.DisplayName)
                    && preset.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(preset.DisplayDescription)
                    && preset.DisplayDescription.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredInstancePresets.Add(preset);
            }
        }
    }

    [RelayCommand]
    private void ToggleDropdown()
    {
        IsDropdownOpen = !IsDropdownOpen;
    }

    [RelayCommand]
    private void ToggleOpenTabsExpanded()
    {
        IsOpenTabsExpanded = !IsOpenTabsExpanded;
    }

    [RelayCommand]
    private void ToggleRecentClosedExpanded()
    {
        IsRecentClosedExpanded = !IsRecentClosedExpanded;
    }

    [RelayCommand]
    private void SelectInstance(InstanceTabViewModel? tab)
    {
        if (tab != null)
        {
            ActiveTab = tab;
            IsDropdownOpen = false;
        }
    }

    [RelayCommand]
    private async Task ReopenRecentClosed(RecentClosedInstanceItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ConfigContent)) return;

        var targetInstanceId = Tabs.Any(t => t.InstanceId == item.InstanceId)
            ? MaaProcessorManager.CreateInstanceId()
            : item.InstanceId;

        var targetPath = Path.Combine(InstanceConfiguration.InstancesDir, $"{targetInstanceId}.json");
        Directory.CreateDirectory(InstanceConfiguration.InstancesDir);
        File.WriteAllText(targetPath, item.ConfigContent);

        var processor = MaaProcessorManager.Instance.CreateInstance(targetInstanceId, false);
        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            MaaProcessorManager.Instance.SetInstanceName(targetInstanceId, item.Name);
        }

        await Task.Run(() => processor.InitializeData());

        ReloadTabs();
        var tab = Tabs.FirstOrDefault(t => t.Processor == processor);
        if (tab != null)
        {
            ActiveTab = tab;
        }

        RecentClosedTabs.Remove(item);
        RefreshFilteredRecentClosedTabs();
    }


    public InstanceTabBarViewModel()
    {
        ReloadTabs();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        MaaProcessor.Processors.CollectionChanged += (_, _) =>
        {
            DispatcherHelper.PostOnMainThread(ReloadTabs);
        };
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        DispatcherHelper.PostOnMainThread(() =>
        {
            OnPropertyChanged(nameof(DropdownSearchWatermark));
            OnPropertyChanged(nameof(OpenConfigsHeaderText));
            OnPropertyChanged(nameof(RecentClosedHeaderText));
            OnPropertyChanged(nameof(NoOpenConfigsText));
            OnPropertyChanged(nameof(NoRecentClosedText));
            OnPropertyChanged(nameof(CloseConfigTooltipText));

            foreach (var item in RecentClosedTabs)
            {
                item.UpdateLocalization();
            }

            RefreshFilteredRecentClosedTabs();
        });
    }

    public void ReloadTabs()
    {
        _isReloading = true;
        try
        {
            var processors = MaaProcessor.Processors.ToList();

            // 移除已不存在的
            var toRemove = Tabs.Where(t => !processors.Contains(t.Processor)).ToList();
            foreach (var t in toRemove)
                Tabs.Remove(t);

            // 添加新的
            foreach (var processor in processors)
            {
                if (Tabs.All(t => t.Processor != processor))
                    Tabs.Add(new InstanceTabViewModel(processor));
            }

            // 按 MaaProcessorManager 的 Instances 顺序排序
            var orderedInstances = MaaProcessorManager.Instance.Instances.ToList();
            for (var i = 0; i < orderedInstances.Count; i++)
            {
                var tab = Tabs.FirstOrDefault(t => t.Processor == orderedInstances[i]);
                if (tab != null)
                {
                    var currentIndex = Tabs.IndexOf(tab);
                    if (currentIndex != i && i < Tabs.Count)
                        Tabs.Move(currentIndex, i);
                }
            }

            RefreshFilteredTabs();
            RefreshFilteredRecentClosedTabs();
        }
        finally
        {
            _isReloading = false;
        }

        var current = MaaProcessorManager.Instance.Current;
        if (current != null)
        {
            var tab = Tabs.FirstOrDefault(t => t.InstanceId == current.InstanceId);
            if (tab != null && ActiveTab != tab)
                ActiveTab = tab;
        }
        else if (Tabs.Count > 0 && ActiveTab == null)
        {
            ActiveTab = Tabs.First();
        }
    }

    /// <summary>
    /// 拖拽排序后保存标签顺序
    /// </summary>
    public void SaveTabOrder()
    {
        var orderedIds = Tabs.Select(t => t.InstanceId);
        MaaProcessorManager.Instance.UpdateInstanceOrder(orderedIds);
    }

    partial void OnActiveTabChanged(InstanceTabViewModel? oldValue, InstanceTabViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsActive = false;
        if (newValue != null)
        {
            newValue.IsActive = true;
            // 排序期间 Tabs.Move 可能导致 SelectedItem 变化触发此回调，跳过副作用
            if (_isReloading) return;
            if (MaaProcessorManager.Instance.Current != newValue.Processor)
            {
                SwitchToInstance(newValue.Processor);
            }
            else
            {
                SyncConnectSettingsForCurrentInstance();
            }
        }
    }
    private void SwitchToInstance(MaaProcessor processor)
    {
        // 切换前保存当前实例的任务状态
        var vm = MaaProcessorManager.Instance.Current?.ViewModel;
        if (vm != null)
        {
            MFAAvalonia.Configuration.ConfigurationManager.CurrentInstance.SetValue(
                MFAAvalonia.Configuration.ConfigurationKeys.TaskItems,
                vm.TaskItemViewModels.ToList().Select(model => model.InterfaceItem));
        }

        if (MaaProcessorManager.Instance.SwitchCurrent(processor.InstanceId))
        {
            // ReloadConfigurationForSwitch(false) 会刷新实例级配置（ConnectSettings 等），无需重复调用 SyncConnectSettingsForCurrentInstance
            Instances.ReloadConfigurationForSwitch(false);
        }
    }

    /// <summary>
    /// 同步更新连接设置 ViewModel，使其反映当前实例的配置。
    /// 使用 IsSyncing 标志跳过所有副作用（SetTasker、写回配置），仅做纯内存属性赋值。
    /// </summary>
    private static void SyncConnectSettingsForCurrentInstance()
    {
        if (!Instances.IsResolved<MFAAvalonia.ViewModels.UsersControls.Settings.ConnectSettingsUserControlModel>())
            return;

        var connect = Instances.ConnectSettingsUserControlModel;
        var config = MFAAvalonia.Configuration.ConfigurationManager.CurrentInstance;

        connect.IsSyncing = true;
        try
        {
            connect.CurrentControllerType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.CurrentController,
                MaaControllerTypes.Adb, MaaControllerTypes.None,
                new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaControllerTypes>());
            connect.RememberAdb = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.RememberAdb, true);
            connect.UseFingerprintMatching = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.UseFingerprintMatching, true);
            connect.AdbControlScreenCapType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AdbControlScreenCapType,
                MaaFramework.Binding.AdbScreencapMethods.None,
                new System.Collections.Generic.List<MaaFramework.Binding.AdbScreencapMethods>
                {
                    MaaFramework.Binding.AdbScreencapMethods.All,
                    MaaFramework.Binding.AdbScreencapMethods.Default
                }, new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaFramework.Binding.AdbScreencapMethods>());
            connect.AdbControlInputType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AdbControlInputType,
                MaaFramework.Binding.AdbInputMethods.None,
                new System.Collections.Generic.List<MaaFramework.Binding.AdbInputMethods>
                {
                    MaaFramework.Binding.AdbInputMethods.All,
                    MaaFramework.Binding.AdbInputMethods.Default
                }, new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaFramework.Binding.AdbInputMethods>());
            connect.Win32ControlScreenCapType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.Win32ControlScreenCapType,
                MaaFramework.Binding.Win32ScreencapMethod.FramePool, MaaFramework.Binding.Win32ScreencapMethod.None,
                new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaFramework.Binding.Win32ScreencapMethod>());
            connect.Win32ControlMouseType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.Win32ControlMouseType,
                MaaFramework.Binding.Win32InputMethod.SendMessage, MaaFramework.Binding.Win32InputMethod.None,
                new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaFramework.Binding.Win32InputMethod>());
            connect.Win32ControlKeyboardType = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.Win32ControlKeyboardType,
                MaaFramework.Binding.Win32InputMethod.SendMessage, MaaFramework.Binding.Win32InputMethod.None,
                new MFAAvalonia.Helper.Converters.UniversalEnumConverter<MaaFramework.Binding.Win32InputMethod>());
            connect.RetryOnDisconnected = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.RetryOnDisconnected, false);
            connect.RetryOnDisconnectedWin32 = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.RetryOnDisconnectedWin32, false);
            connect.AllowAdbRestart = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AllowAdbRestart, true);
            connect.AllowAdbHardRestart = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AllowAdbHardRestart, true);
            connect.AutoDetectOnConnectionFailed = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AutoDetectOnConnectionFailed, true);
            connect.AutoConnectAfterRefresh = config.GetValue(MFAAvalonia.Configuration.ConfigurationKeys.AutoConnectAfterRefresh, true);
        }
        finally
        {
            connect.IsSyncing = false;
        }
    }

    /// <summary>
    /// 通过实例ID切换到指定多开实例（供全局定时器调用）
    /// </summary>
    public void SwitchToInstanceById(string instanceId)
    {
        var tab = Tabs.FirstOrDefault(t => t.InstanceId == instanceId);
        if (tab != null)
        {
            ActiveTab = tab;
        }
    }

    public void RefreshInstancePresets()
    {
        var presets = MaaProcessor.Interface?.Preset;
        HasInstancePresets = presets is { Count: > 0 };
        InstancePresets = presets;
        RefreshFilteredInstancePresets();
    }

    [RelayCommand]
    private async Task AddInstance()
    {
        if (HasInstancePresets)
        {
            IsAddMenuOpen = true;
            return;
        }
        await AddInstanceCoreAsync(null);
    }

    [RelayCommand]
    private async Task AddInstanceWithPreset(MaaInterface.MaaInterfacePreset? preset)
    {
        IsAddMenuOpen = false;
        await AddInstanceCoreAsync(preset);
    }

    private async Task AddInstanceCoreAsync(MaaInterface.MaaInterfacePreset? preset)
    {
        var lastTab = Tabs.LastOrDefault();

        // 先将最右侧实例的当前任务列表（含勾选状态）保存到配置
        var lastVm = lastTab?.TaskQueueViewModel;
        if (lastVm != null)
        {
            lastTab!.Processor.InstanceConfiguration.SetValue(
                ConfigurationKeys.TaskItems,
                lastVm.TaskItemViewModels.Where(m => !m.IsResourceOptionItem).Select(model => model.InterfaceItem).ToList());
        }

        // 在创建实例前，先将配置文件复制到新实例位置，确保构造函数能读到完整配置
        string? newId = null;
        if (lastTab != null)
        {
            newId = MaaProcessorManager.CreateInstanceId();
            lastTab.Processor.InstanceConfiguration.CopyToNewInstance(newId);

            if (preset != null && lastVm != null)
            {
                var taskItemsWithoutSpecialTasks = lastVm.TaskItemViewModels
                    .Where(m => !m.IsResourceOptionItem)
                    .Where(m => !string.IsNullOrWhiteSpace(m.InterfaceItem?.Entry)
                        ? !AddTaskDialogViewModel.SpecialActionNames.Contains(m.InterfaceItem!.Entry!)
                        : true)
                    .Select(model => model.InterfaceItem)
                    .ToList();

                var newInstanceConfig = new InstanceConfiguration(newId);
                newInstanceConfig.SetValue(ConfigurationKeys.TaskItems, taskItemsWithoutSpecialTasks);
                newInstanceConfig.SetValue(
                    ConfigurationKeys.CurrentTasks,
                    taskItemsWithoutSpecialTasks
                        .Where(task => !string.IsNullOrWhiteSpace(task?.Name) && !string.IsNullOrWhiteSpace(task.Entry))
                        .Select(task => $"{task!.Name}{TaskLoader.NEW_SEPARATOR}{task.Entry}")
                        .Distinct()
                        .ToList());
            }
        }

        var processor = newId != null
            ? MaaProcessorManager.Instance.CreateInstance(newId, false)
            : MaaProcessorManager.Instance.CreateInstance(false);

        await Task.Run(() => processor.InitializeData());

        // 如果指定了预设，应用到新实例的 ViewModel，并使用预设的显示名称
        if (preset != null)
        {
            processor.ViewModel?.ApplyPresetCommand.Execute(preset);
            var presetDisplayName = preset.DisplayName;
            if (!string.IsNullOrWhiteSpace(presetDisplayName))
            {
                MaaProcessorManager.Instance.SetInstanceName(processor.InstanceId, presetDisplayName);
            }
        }

        var tab = Tabs.FirstOrDefault(t => t.Processor == processor);
        if (tab != null)
        {
            if (preset != null)
                tab.UpdateName();
            ActiveTab = tab;
        }
    }

    [RelayCommand]
    private async Task CloseInstance(InstanceTabViewModel? tab)
    {
        if (tab == null) return;

        if (Tabs.Count <= 1)
        {
            ToastHelper.Info(LangKeys.InstanceCannotCloseLast.ToLocalization());
            return;
        }

        if (tab.IsRunning)
        {
            var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
            {
                Content = LangKeys.InstanceRunningCloseConfirm.ToLocalization(),
                ActionButtonsPreset = SukiMessageBoxButtons.YesNo,
                IconPreset = SukiMessageBoxIcons.Warning
            }, new SukiMessageBoxOptions
            {
                Title = LangKeys.InstanceCloseTitle.ToLocalization()
            });

            if (!result.Equals(SukiMessageBoxResult.Yes)) return;

            tab.Processor.Stop(MFATask.MFATaskStatus.STOPPED);
        }

        // 检查定时任务是否使用了该实例，若有则重新分配
        ReassignTimersFromInstance(tab.InstanceId, tab.Name);

        var configPath = tab.Processor.InstanceConfiguration.GetConfigFilePath();
        var configContent = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var recentClosedItem = RecentClosedInstanceItem.FromTab(tab, configContent);

        if (MaaProcessorManager.Instance.RemoveInstance(tab.InstanceId))
        {
            RecentClosedTabs.Remove(RecentClosedTabs.FirstOrDefault(item => item.InstanceId == tab.InstanceId));
            RecentClosedTabs.Insert(0, recentClosedItem);
            while (RecentClosedTabs.Count > 12)
            {
                RecentClosedTabs.RemoveAt(RecentClosedTabs.Count - 1);
            }

            Tabs.Remove(tab);
            RefreshFilteredTabs();
            RefreshFilteredRecentClosedTabs();
            if (ActiveTab == tab || ActiveTab == null)
            {
                ActiveTab = Tabs.FirstOrDefault();
            }
        }
    }

    /// <summary>
    /// 将使用指定实例的定时任务重新分配到当前活跃实例
    /// </summary>
    private void ReassignTimersFromInstance(string instanceId, string instanceName)
    {
        var timerModel = TimerModel.Instance;
        var reassigned = false;

        foreach (var timer in timerModel.Timers)
        {
            if (timer.TimerConfig == instanceId)
            {
                // 分配到第一个非被删除的实例
                var fallback = Tabs.FirstOrDefault(t => t.InstanceId != instanceId);
                if (fallback != null)
                {
                    timer.TimerConfig = fallback.InstanceId;
                    reassigned = true;
                }
            }
        }

        if (reassigned)
        {
            ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.InstanceTimerReassigned.ToLocalizationFormatted(false, instanceName));
            timerModel.RefreshInstanceList();
        }
    }

    [RelayCommand]
    private void RenameInstance(InstanceTabViewModel? tab)
    {
        if (tab == null) return;

        Instances.DialogManager.CreateDialog()
            .WithTitle(LangKeys.TaskRename.ToLocalization())
            .WithViewModel(dialog => new RenameInstanceDialogViewModel(dialog, tab))
            .TryShow();
    }
}

public sealed partial class RecentClosedInstanceItem : ViewModelBase
{
    public string InstanceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    [ObservableProperty] private string _metaText = string.Empty;
    [ObservableProperty] private string _timeText = string.Empty;
    public string TaskCountText { get; init; } = string.Empty;
    public string ConfigContent { get; init; } = string.Empty;

    public void UpdateLocalization()
    {
        TimeText = "InstanceDropdownJustNow".ToLocalization();
    }

    public static RecentClosedInstanceItem FromTab(InstanceTabViewModel tab, string? configContent)
    {
        var metaParts = new[]
            {
                tab.ControllerBadgeText,
                tab.ResourceBadgeText
            }
            .Where(static part => !string.IsNullOrWhiteSpace(part) && part != "-");

        return new RecentClosedInstanceItem
        {
            InstanceId = tab.InstanceId,
            Name = tab.Name,
            MetaText = string.Join(" · ", metaParts),
            TimeText = "InstanceDropdownJustNow".ToLocalization(),
            TaskCountText = tab.TaskCountText,
            ConfigContent = configContent ?? string.Empty
        };
    }
}
