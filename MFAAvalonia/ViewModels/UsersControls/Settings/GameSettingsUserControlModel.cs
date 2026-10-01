using CommunityToolkit.Mvvm.ComponentModel;
using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using System.Collections.ObjectModel;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class GameSettingsUserControlModel : ViewModelBase
{
    [ObservableProperty] private bool _enableRecording = ConfigurationManager.Maa.GetValue(ConfigurationKeys.Recording, false);

    [ObservableProperty] private bool _enableSaveDraw = ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveDraw, false);

    [ObservableProperty] private bool _enableSaveOnError = ConfigurationManager.Maa.GetValue(ConfigurationKeys.SaveOnError, true);

    [ObservableProperty] private bool _showHitDraw = ConfigurationManager.Maa.GetValue(ConfigurationKeys.ShowHitDraw, false);

    [ObservableProperty] private string _prescript = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Prescript, string.Empty);

    [ObservableProperty] private string _postScript = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Postscript, string.Empty);

    [ObservableProperty] private bool _continueRunningWhenError = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.ContinueRunningWhenError, true);

    [ObservableProperty] private bool _resumeInterruptedQueue = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.ResumeInterruptedQueue, true);

    partial void OnEnableRecordingChanged(bool value)
    {
        ConfigurationManager.Maa.SetValue(ConfigurationKeys.Recording, value);
        //  MaaProcessor.Global.SetOption_Recording(value);
        Instances.RootViewModel.IsDebugMode = EnableSaveDraw || EnableRecording || ShowHitDraw || EnableSaveOnError;
        MaaProcessorManager.Instance.Current.SetTasker();
    }

    partial void OnEnableSaveDrawChanged(bool value)
    {
        ConfigurationManager.Maa.SetValue(ConfigurationKeys.SaveDraw, value);
        MaaProcessor.Global.SetOption_SaveDraw(value);
        Instances.RootViewModel.IsDebugMode = EnableSaveDraw || EnableRecording || ShowHitDraw || EnableSaveOnError;
        MaaProcessorManager.Instance.Current.SetTasker();
    }

    partial void OnEnableSaveOnErrorChanged(bool value)
    {
        ConfigurationManager.Maa.SetValue(ConfigurationKeys.SaveOnError, value);
        MaaProcessor.Global.SetOption(GlobalOption.SaveOnError, value);
        Instances.RootViewModel.IsDebugMode = EnableSaveDraw || EnableRecording || ShowHitDraw || EnableSaveOnError;
        MaaProcessorManager.Instance.Current.SetTasker();
    }

    partial void OnShowHitDrawChanged(bool value)
    {
        ConfigurationManager.Maa.SetValue(ConfigurationKeys.ShowHitDraw, value);
        MaaProcessor.Global.SetOption_DebugMode(value);
        Instances.RootViewModel.IsDebugMode = EnableSaveDraw || EnableRecording || ShowHitDraw || EnableSaveOnError;
        MaaProcessorManager.Instance.Current.SetTasker();
    }

    partial void OnPrescriptChanged(string value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.Prescript, value);
    }

    partial void OnPostScriptChanged(string value)
    {
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.Postscript, value);
    }

    partial void OnContinueRunningWhenErrorChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.ContinueRunningWhenError, value);

    partial void OnResumeInterruptedQueueChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.ResumeInterruptedQueue, value);

    // [ObservableProperty] private ObservableCollection<MaaInterface.MaaCustomResource> _currentResources = [];
    //
    // [ObservableProperty] private string _currentResource = ConfigurationHelper.GetValue(ConfigurationKeys.Resource, string.Empty);
    //
    // partial void OnCurrentResourceChanged(string value)
    // {
    //     ConfigurationHelper.SetValue(ConfigurationKeys.Resource, value);
    // }
}
