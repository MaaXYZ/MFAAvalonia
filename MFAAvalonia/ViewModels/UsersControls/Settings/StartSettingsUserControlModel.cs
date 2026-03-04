using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;
using SukiUI.Dialogs;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class StartSettingsUserControlModel : ViewModelBase
{
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


    public AvaloniaList<LocalizationViewModel> AfterTaskList =>
    [
        new("None"),
        new("CloseMFA"),
        new("CloseEmulator"),
        new("CloseEmulatorAndMFA"),
        new("ShutDown"),
        new("ShutDownOnce"),
        new("CloseEmulatorAndRestartMFA"),
        new("RestartPC"),
    ];


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
}
