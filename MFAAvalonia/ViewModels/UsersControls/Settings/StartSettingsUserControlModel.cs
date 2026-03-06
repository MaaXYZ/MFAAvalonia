using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using SukiUI.Dialogs;
using System.Collections.Generic;
using System.ComponentModel;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class StartSettingsUserControlModel : ViewModelBase
{
    private readonly LocalizationViewModel _closeSoftwareItem = new(LangKeys.CloseEmulator);
    private readonly LocalizationViewModel _closeSoftwareAndMFAItem = new(LangKeys.CloseEmulatorAndMFA);
    private readonly LocalizationViewModel _closeSoftwareAndRestartMFAItem = new(LangKeys.CloseEmulatorAndRestartMFA);
    private readonly AvaloniaList<LocalizationViewModel> _afterTaskList;
    private TaskQueueViewModel? _trackedTaskQueueViewModel;
    private bool _isAdbController;

    public StartSettingsUserControlModel()
    {
        _afterTaskList =
        [
            new(LangKeys.None),
            new(LangKeys.CloseMFA),
            _closeSoftwareItem,
            _closeSoftwareAndMFAItem,
            new(LangKeys.ShutDown),
            new(LangKeys.ShutDownOnce),
            _closeSoftwareAndRestartMFAItem,
            new(LangKeys.RestartPC),
        ];
    }

    protected override void Initialize()
    {
        base.Initialize();

        Instances.InstanceTabBarViewModel.PropertyChanged += OnInstanceTabBarPropertyChanged;
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        SubscribeToTaskQueueViewModel(Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel);
        RebuildAfterTaskList();
    }

    [ObservableProperty] private bool _autoMinimize = ConfigurationManager.Current.GetValue(ConfigurationKeys.AutoMinimize, false);

    [ObservableProperty] private bool _autoHide = ConfigurationManager.Current.GetValue(ConfigurationKeys.AutoHide, false);

    [ObservableProperty] private string _softwarePath = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.SoftwarePath, string.Empty);

    [ObservableProperty] private string _emulatorConfig = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);

    [ObservableProperty] private double _waitSoftwareTime = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.WaitSoftwareTime, 60.0);


    partial void OnAutoMinimizeChanged(bool value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.AutoMinimize, value);
    }

    partial void OnAutoHideChanged(bool value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.AutoHide, value);
    }

    partial void OnSoftwarePathChanged(string value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.SoftwarePath, value);
    }

    partial void OnEmulatorConfigChanged(string value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.EmulatorConfig, value);
    }

    partial void OnWaitSoftwareTimeChanged(double value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.WaitSoftwareTime, value);
    }

    [RelayCommand]
    async private Task SelectSoft()
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        // 配置文件选择器选项
        var options = new FilePickerOpenOptions
        {
            Title = LangKeys.SelectExecutableFile.ToLocalization(),
            FileTypeFilter =
            [
                new FilePickerFileType(LangKeys.ExeFilter.ToLocalization())
                {
                    Patterns = ["*"] // 支持所有文件类型
                }
            ]
        };

        var result = await storageProvider.OpenFilePickerAsync(options);

        // 处理选择结果
        if (result is { Count: > 0 } && result[0].TryGetLocalPath() is { } path)
        {
            SoftwarePath = path;
        }
    }


    public AvaloniaList<LocalizationViewModel> BeforeTaskList =>
    [
        new("None"),
        new("StartupSoftware"),
        new("StartupSoftwareAndScript"),
        new("StartupScriptOnly"),
    ];

    public AvaloniaList<LocalizationViewModel> AfterTaskList => _afterTaskList;


    [ObservableProperty] private string? _beforeTask = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.BeforeTask, "None");

    partial void OnBeforeTaskChanged(string? value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.BeforeTask, value);
    }

    [ObservableProperty] private string? _afterTask = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AfterTask, "None");

    partial void OnAfterTaskChanged(string? value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.AfterTask, value);
    }

    [RelayCommand]
    private void QuickSettings()
    {
        Instances.DialogManager.CreateDialog().WithTitle("EmulatorMultiInstanceEditor").WithViewModel(dialog => new MultiInstanceEditorDialogViewModel(dialog)).Dismiss().ByClickingBackground().TryShow();
    }

    private void OnInstanceTabBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstanceTabBarViewModel.ActiveTab))
        {
            return;
        }

        SubscribeToTaskQueueViewModel(Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel);
        RebuildAfterTaskList();
    }

    private void SubscribeToTaskQueueViewModel(TaskQueueViewModel? taskQueueViewModel)
    {
        if (_trackedTaskQueueViewModel == taskQueueViewModel)
        {
            return;
        }

        if (_trackedTaskQueueViewModel != null)
        {
            _trackedTaskQueueViewModel.PropertyChanged -= OnTaskQueueViewModelPropertyChanged;
        }

        _trackedTaskQueueViewModel = taskQueueViewModel;

        if (_trackedTaskQueueViewModel != null)
        {
            _trackedTaskQueueViewModel.PropertyChanged += OnTaskQueueViewModelPropertyChanged;
        }
    }

    private void OnTaskQueueViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskQueueViewModel.CurrentController))
        {
            RebuildAfterTaskList();
        }
    }

    private void RebuildAfterTaskList()
    {
        _isAdbController = (_trackedTaskQueueViewModel?.CurrentController
            ?? ConfigurationManager.CurrentInstance.GetValue(
                ConfigurationKeys.CurrentController,
                MaaControllerTypes.Adb,
                MaaControllerTypes.None,
                new UniversalEnumConverter<MaaControllerTypes>())) == MaaControllerTypes.Adb;

        UpdateAfterTaskDisplayNames();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateAfterTaskDisplayNames();
    }

    private void UpdateAfterTaskDisplayNames()
    {
        _closeSoftwareItem.Name = (_isAdbController ? LangKeys.CloseEmulator : LangKeys.CloseTargetProgram).ToLocalization();
        _closeSoftwareAndMFAItem.Name = (_isAdbController ? LangKeys.CloseEmulatorAndMFA : LangKeys.CloseTargetProgramAndMFA).ToLocalization();
        _closeSoftwareAndRestartMFAItem.Name = (_isAdbController
            ? LangKeys.CloseEmulatorAndRestartMFA
            : LangKeys.CloseTargetProgramAndRestartMFA).ToLocalization();
    }
}
