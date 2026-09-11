using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;
using SukiUI.Dialogs;
using System;
using System.Linq;
using System.ComponentModel;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.ViewModels.Windows;

public partial class RootViewModel : ViewModelBase
{
    private TaskQueueViewModel? _trackedActiveTaskQueueViewModel;

    protected override void Initialize()
    {
        if (OperatingSystem.IsAndroid())
        {
            return;
        }

        DispatcherHelper.PostOnMainThread(() =>
        {
            HookActiveInstanceState();
            CheckDebug();
        });
    }

    [ObservableProperty] private bool _idle = true;
    [ObservableProperty] private bool _currentInstanceIdle = true;
    [ObservableProperty] private bool _isWindowVisible = true;

    [ObservableProperty] private bool _isRunning;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRunning));
    }

    private void HookActiveInstanceState()
    {
        Instances.InstanceTabBarViewModel.PropertyChanged -= OnInstanceTabBarPropertyChanged;
        Instances.InstanceTabBarViewModel.PropertyChanged += OnInstanceTabBarPropertyChanged;
        SubscribeToActiveTaskQueueViewModel(Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel);
    }

    private void OnInstanceTabBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceTabBarViewModel.ActiveTab))
        {
            SubscribeToActiveTaskQueueViewModel(Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel);
        }
    }

    private void SubscribeToActiveTaskQueueViewModel(TaskQueueViewModel? taskQueueViewModel)
    {
        if (ReferenceEquals(_trackedActiveTaskQueueViewModel, taskQueueViewModel))
        {
            UpdateCurrentInstanceIdle();
            return;
        }

        if (_trackedActiveTaskQueueViewModel != null)
        {
            _trackedActiveTaskQueueViewModel.PropertyChanged -= OnActiveTaskQueueViewModelPropertyChanged;
        }

        _trackedActiveTaskQueueViewModel = taskQueueViewModel;

        if (_trackedActiveTaskQueueViewModel != null)
        {
            _trackedActiveTaskQueueViewModel.PropertyChanged += OnActiveTaskQueueViewModelPropertyChanged;
        }

        UpdateCurrentInstanceIdle();
    }

    private void OnActiveTaskQueueViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskQueueViewModel.IsRunning) or nameof(TaskQueueViewModel.Idle))
        {
            UpdateCurrentInstanceIdle();
        }
    }

    private void UpdateCurrentInstanceIdle()
    {
        var isIdle = _trackedActiveTaskQueueViewModel?.Idle ?? true;
        CurrentInstanceIdle = isIdle;
        Idle = isIdle;
    }

    public static string Version
    {
        get
        {
            // var version = Assembly.GetExecutingAssembly().GetName().Version;
            // var major = version.Major;
            // var minor = version.Minor >= 0 ? version.Minor : 0;
            // var patch = version.Build >= 0 ? version.Build : 0;
            // return $"v{SemVersion.Parse($"{major}.{minor}.{patch}")}";
            return "v2.16.2-beta.1"; // Hardcoded version for now, replace with dynamic versioning later
        }
    }

    [ObservableProperty] private Action? _tempResourceUpdateAction;
   
    [ObservableProperty] private string? _windowUpdateInfo = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplicationDisplayName))]
    private string? _resourceName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplicationDisplayName))]
    private bool _isResourceNameVisible;

    [ObservableProperty] private string? _resourceVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplicationDisplayName))]
    private string? _customTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplicationDisplayName))]
    private bool _isCustomTitleVisible;

    public string ApplicationDisplayName
    {
        get
        {
            if (IsCustomTitleVisible && !string.IsNullOrWhiteSpace(CustomTitle))
                return CustomTitle.Trim();

            if (IsResourceNameVisible && !string.IsNullOrWhiteSpace(ResourceName))
                return ResourceName.Trim();

            return LangKeys.AppTitle.ToLocalization();
        }
    }

    public void RefreshApplicationDisplayName() => OnPropertyChanged(nameof(ApplicationDisplayName));

    [ObservableProperty] private bool _lockController;

    [ObservableProperty] private bool _isDebugMode = ConfigurationManager.Maa.GetValue(ConfigurationKeys.Recording, false)
        || ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveDraw, false)
        || ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false);
    private bool _shouldTip = true;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private bool _isConfigSwitching;
    [ObservableProperty] private double _configSwitchProgress;

    partial void OnIsConfigSwitchingChanging(bool value)
    {
        Console.WriteLine("状态：" + value);
    }
    
    public void SetConfigSwitchingState(bool isSwitching)
    {
        IsConfigSwitching = isSwitching;
        if (!isSwitching)
        {
            ConfigSwitchProgress = 0;
        }
    }

    public void SetConfigSwitchProgress(double progress)
    {
        ConfigSwitchProgress = Math.Clamp(progress, 0, 100);
    }
    
    [RelayCommand]
    private void TryUpdate()
    {
        TempResourceUpdateAction?.Invoke();
    }
    
    partial void OnLockControllerChanged(bool value)
    {
        var vm = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
        if (value && vm?.SelectedController != null)
        {
            vm.ShouldShow = (int)vm.SelectedController.ControllerType;
        }
    }

    public void CheckDebug()
    {
        var vm = Instances.TryGetResolved<InstanceTabBarViewModel>(out var tabBarViewModel)
            ? tabBarViewModel.ActiveTab?.TaskQueueViewModel
            : null;
        if (IsDebugMode && _shouldTip && vm != null && !vm.Processor.IsV3)
        {
            DispatcherHelper.PostOnMainThread(() =>
            {
                Instances.DialogManager.CreateDialog().OfType(NotificationType.Warning).WithContent(LangKeys.DebugModeWarning.ToLocalization()).WithActionButton(LangKeys.Ok.ToLocalization(), dialog => { }, true).TryShow();
                _shouldTip = false;
            });
        }
    }

    public void SetUpdating(bool isUpdating)
    {
        DispatcherHelper.RunOnMainThread(() => IsUpdating = isUpdating);
    }
    
    partial void OnIsDebugModeChanged(bool value)
    {
        if (value)
            CheckDebug();
    }
    private string _resourceNameKey = "";
    private string _resourceFallbackKey = "";
    private string _customTitleKey = "";
    private string _customTitleFallbackKey = "";
    public void ShowResourceName(string name)
    {
        ResourceName = name;
        IsResourceNameVisible = true;

    }

    public void ShowResourceKeyAndFallBack(string? key, string? fallback)
    {
        _resourceNameKey = key ?? string.Empty;
        _resourceFallbackKey = fallback ?? string.Empty;
        UpdateName();
        LanguageHelper.LanguageChanged += (_, __) => UpdateName();
        IsResourceNameVisible = true;
    }

    public void UpdateName()
    {
        var result = LanguageHelper.GetLocalizedDisplayName(_resourceNameKey, _resourceFallbackKey);
        if (result.Equals("debug", StringComparison.OrdinalIgnoreCase))
            IsResourceNameVisible = false;
        else
            ResourceName = result;
    }

    public void ShowResourceVersion(string version)
    {
        version = version.StartsWith("v") ? version : "v" + version;
        ResourceVersion = version;
    }

    public void ShowCustomTitle(string title)
    {
        CustomTitle = title;
        IsCustomTitleVisible = true;
        IsResourceNameVisible = false;
    }

    public void ShowCustomTitleAndFallBack(string? key, string? fallback)
    {
        _customTitleKey = key ?? string.Empty;
        _customTitleFallbackKey = fallback ?? string.Empty;
        UpdateCustomTitle();
        LanguageHelper.LanguageChanged += (_, __) => UpdateCustomTitle();
        if (!string.IsNullOrWhiteSpace(CustomTitle))
        {
            IsCustomTitleVisible = true;
            IsResourceNameVisible = false;
        }
    }

    public void UpdateCustomTitle()
    {
        CustomTitle = LanguageHelper.GetLocalizedDisplayName(_customTitleKey, _customTitleFallbackKey);
    }

    [RelayCommand]
    public void ToggleVisible()
    {
        IsWindowVisible = !IsWindowVisible;
    }
}
