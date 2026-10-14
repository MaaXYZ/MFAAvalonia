using Avalonia.Collections;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace MFAAvalonia.ViewModels.Pages;

public partial class SettingsViewModel : ViewModelBase
{
    private bool _hotkeysInitialized;
    private bool _syncingCurrentConfiguration;

    [ObservableProperty] private bool _helpImproveSoftware =
        bool.TryParse(GlobalConfiguration.GetValue(ConfigurationKeys.HelpImproveSoftware, bool.TrueString), out var helpImproveSoftware)
        && helpImproveSoftware;

    partial void OnHelpImproveSoftwareChanged(bool value) => TelemetryService.SetEnabled(value);

    protected override void Initialize()
    {
        _hotKeyShowGui = MFAHotKey.Parse(GlobalConfiguration.GetValue(ConfigurationKeys.ShowGui, ""));
        _hotKeyLinkStart = MFAHotKey.Parse(GlobalConfiguration.GetValue(ConfigurationKeys.LinkStart, ""));

        DispatcherHelper.PostOnMainThread(InitializeHotkeysAfterStartup);
    }

    private void InitializeHotkeysAfterStartup()
    {
        if (_hotkeysInitialized)
        {
            return;
        }

        _hotkeysInitialized = true;

        SetHotKey(ref _hotKeyShowGui, _hotKeyShowGui, ConfigurationKeys.ShowGui,
            Instances.RootViewModel.ToggleVisibleCommand, LangKeys.HotKeyShowGui);

        SetHotKey(ref _hotKeyLinkStart, _hotKeyLinkStart, ConfigurationKeys.LinkStart,
            new RelayCommand(() => Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel?.ToggleCommand.Execute(null)),
            LangKeys.HotKeyLinkStart);
    }

    #region 多开实例管理

    /// <summary>
    /// 多开实例列表（用于设置界面的配置管理）
    /// </summary>
    public ObservableCollection<InstanceTabViewModel> ConfigurationList => Instances.InstanceTabBarViewModel.Tabs;
    public ObservableCollection<InstanceTabViewModel> FilteredConfigurationList { get; } = new();

    [ObservableProperty] private InstanceTabViewModel? _currentConfiguration;
    [ObservableProperty] private bool _isConfigurationPopupOpen;
    [ObservableProperty] private string _configurationSearchText = string.Empty;

    public SettingsViewModel()
    {
        var tabVm = Instances.InstanceTabBarViewModel;
        tabVm.PropertyChanged += OnInstanceTabBarViewModelPropertyChanged;
        tabVm.Tabs.CollectionChanged += OnConfigurationListCollectionChanged;

        ApplyInterfaceMetadata(MaaProcessor.Interface);
        RefreshCurrentConfiguration();
        RefreshFilteredConfigurationList();
    }

    public void ApplyInterfaceMetadata(MaaInterface? maaInterface)
    {
        if (maaInterface == null)
        {
            ShowResourceIssues = false;
            ResourceGithub = string.Empty;
            ResourceIssues = string.Empty;
            ResourceDescription = string.Empty;
            ResourceContact = string.Empty;
            ResourceLicense = string.Empty;
            HasResourceDescription = false;
            HasResourceContact = false;
            HasResourceLicense = false;
            return;
        }

        ShowResourceIssues = !string.IsNullOrWhiteSpace(maaInterface.Url) || !string.IsNullOrWhiteSpace(maaInterface.Github);
        ResourceGithub = (!string.IsNullOrWhiteSpace(maaInterface.Github) ? maaInterface.Github : maaInterface.Url) ?? string.Empty;
        ResourceIssues = string.IsNullOrWhiteSpace(ResourceGithub)
            ? string.Empty
            : $"{ResourceGithub}/issues";
    }

    partial void OnCurrentConfigurationChanged(InstanceTabViewModel? value)
    {
        if (value != null && !_syncingCurrentConfiguration)
        {
            Instances.InstanceTabBarViewModel.ActiveTab = value;
        }

        IsConfigurationPopupOpen = false;
    }

    public void RefreshCurrentConfiguration()
    {
        _syncingCurrentConfiguration = true;
        try
        {
            CurrentConfiguration = Instances.InstanceTabBarViewModel.ActiveTab
                                   ?? ConfigurationList.FirstOrDefault();
        }
        finally
        {
            _syncingCurrentConfiguration = false;
        }
    }

    partial void OnConfigurationSearchTextChanged(string value)
    {
        RefreshFilteredConfigurationList();
    }

    [RelayCommand]
    private void ToggleConfigurationPopup()
    {
        IsConfigurationPopupOpen = !IsConfigurationPopupOpen;
        if (IsConfigurationPopupOpen)
        {
            RefreshFilteredConfigurationList();
        }
    }

    [RelayCommand]
    private void SelectConfiguration(InstanceTabViewModel? configuration)
    {
        if (configuration == null) return;
        CurrentConfiguration = configuration;
    }

    [ObservableProperty] private string _newConfigurationName = string.Empty;

    [RelayCommand]
    private void AddConfiguration()
    {
        // 如果用户输入了名称，检查是否已存在
        if (!string.IsNullOrWhiteSpace(NewConfigurationName))
        {
            var configExists = ConfigurationList.Any(tab =>
                tab.Name.Equals(NewConfigurationName, System.StringComparison.OrdinalIgnoreCase));
            
            if (configExists)
            {
                ToastHelper.Error(LangKeys.ConfigNameAlreadyExists.ToLocalizationFormatted(false, NewConfigurationName));
                return;
            }
        }
        
        // 添加新的多开实例
        Instances.InstanceTabBarViewModel.AddInstanceCommand.Execute(null);
        
        // 如果用户输入了名称，则设置实例名称
        if (!string.IsNullOrWhiteSpace(NewConfigurationName))
        {
            var newTab = Instances.InstanceTabBarViewModel.ActiveTab;
            if (newTab != null)
            {
                MaaProcessorManager.Instance.SetInstanceName(newTab.InstanceId, NewConfigurationName);
                newTab.UpdateName();
            }
            NewConfigurationName = string.Empty;
        }
        
        ToastHelper.Success(LangKeys.ConfigAddedSuccessfully.ToLocalizationFormatted(false,
            Instances.InstanceTabBarViewModel.ActiveTab?.Name ?? ""));
    }

    #endregion 多开实例管理

    private void OnInstanceTabBarViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceTabBarViewModel.ActiveTab))
        {
            RefreshCurrentConfiguration();
        }
    }

    private void OnConfigurationListCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshFilteredConfigurationList();
        RefreshCurrentConfiguration();
    }

    private void RefreshFilteredConfigurationList()
    {
        FilteredConfigurationList.Clear();
        var query = ConfigurationSearchText?.Trim() ?? string.Empty;
        foreach (var item in ConfigurationList)
        {
            if (string.IsNullOrEmpty(query)
                || item.Name.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            {
                FilteredConfigurationList.Add(item);
            }
        }
    }

    #region HotKey
    [ObservableProperty] private bool _enableHotKey = true;
    private MFAHotKey _hotKeyShowGui = MFAHotKey.NOTSET;

    public MFAHotKey HotKeyShowGui
    {
        get => _hotKeyShowGui;
        set => SetHotKey(ref _hotKeyShowGui, value, ConfigurationKeys.ShowGui, Instances.RootViewModel.ToggleVisibleCommand,
            LangKeys.HotKeyShowGui);
    }

    private MFAHotKey _hotKeyLinkStart = MFAHotKey.NOTSET;

    public MFAHotKey HotKeyLinkStart
    {
        get => _hotKeyLinkStart;
        set => SetHotKey(ref _hotKeyLinkStart, value, ConfigurationKeys.LinkStart,
            new RelayCommand(() => Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel?.ToggleCommand.Execute(null)),
            LangKeys.HotKeyLinkStart);
    }

    public void SetHotKey(ref MFAHotKey value, MFAHotKey? newValue, string type, ICommand command, string ownerResourceKey)
    {
        if (newValue != null)
        {
            if (!GlobalHotkeyService.Register(newValue.Gesture, command, ownerResourceKey, out var occupiedBy))
            {
                newValue = new MFAHotKey(true)
                {
                    ResourceKey = "HotKeyOccupiedBy",
                    ResourceArgsKeys = [occupiedBy ?? "UnknownItem"]
                };
            }
            GlobalConfiguration.SetValue(type, newValue.ToString());
            SetProperty(ref value, newValue);
        }
    }

    #endregion HotKey
    
    #region 资源

    [ObservableProperty] private bool _showResourceIssues;
    [ObservableProperty] private string _resourceIssues = string.Empty;
    [ObservableProperty] private string _resourceGithub = string.Empty;

    [ObservableProperty] private string _resourceContact = string.Empty;
    [ObservableProperty] private string _resourceDescription = string.Empty;
    [ObservableProperty] private string _resourceLicense = string.Empty;
    [ObservableProperty] private bool _hasResourceContact;
    [ObservableProperty] private bool _hasResourceDescription;
    [ObservableProperty] private bool _hasResourceLicense;

    #endregion
}
