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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System;
using System.Linq;
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
    private bool _isSynchronizingStartupSettings;

    [ObservableProperty]
    private bool _globalStartEnabled = GlobalConfiguration.GetValue(
        ConfigurationKeys.GlobalStartEnabled,
        bool.FalseString) == bool.TrueString;

    public ObservableCollection<GlobalStartInstanceEntry> GlobalStartInstanceEntries { get; } = [];
    public ObservableCollection<ExtraLaunchEntry> ExtraLaunchEntries { get; } = [];

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
        RemoveLegacyGlobalEmulatorConfiguration();
        RefreshGlobalStartInstances();
        LoadExtraLaunchEntries();
    }

    partial void OnGlobalStartEnabledChanged(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.GlobalStartEnabled, value.ToString());
        if (value)
            RefreshGlobalStartInstances();
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
        if (_isSynchronizingStartupSettings) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.SoftwarePath, value);
        SyncCurrentGlobalEntry(entry => entry.SoftwarePath = value);
    }

    partial void OnEmulatorConfigChanged(string value)
    {
        if (_isSynchronizingStartupSettings) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.EmulatorConfig, value);
        SyncCurrentGlobalEntry(entry => entry.EmulatorConfig = value);
    }

    partial void OnWaitSoftwareTimeChanged(double value)
    {
        if (_isSynchronizingStartupSettings) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.WaitSoftwareTime, value);
        SyncCurrentGlobalEntry(entry => entry.WaitSoftwareTime = value);
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
        if (_isSynchronizingStartupSettings) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.BeforeTask, value);
        SyncCurrentGlobalEntry(entry => entry.BeforeTask = value);
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

    [RelayCommand]
    private void RefreshGlobalStartInstances()
    {
        GlobalStartInstanceEntries.Clear();
        var manager = MaaProcessorManager.Instance;
        foreach (var (id, name) in manager.GetAllInstanceIdsAndNames())
        {
            manager.EnsureInstanceLoaded(id);
            var vm = manager.GetViewModel(id);
            if (vm != null)
                GlobalStartInstanceEntries.Add(new GlobalStartInstanceEntry(this, id, name, vm.Processor.InstanceConfiguration));
        }
    }

    private void SyncCurrentGlobalEntry(Action<GlobalStartInstanceEntry> update)
    {
        var currentId = MaaProcessorManager.Instance.Current.InstanceId;
        var entry = GlobalStartInstanceEntries.FirstOrDefault(item => item.InstanceId == currentId);
        if (entry == null) return;

        _isSynchronizingStartupSettings = true;
        try
        {
            update(entry);
        }
        finally
        {
            _isSynchronizingStartupSettings = false;
        }
    }

    internal void SyncCurrentSettingsFromEntry(GlobalStartInstanceEntry entry)
    {
        if (_isSynchronizingStartupSettings
            || entry.InstanceId != MaaProcessorManager.Instance.Current.InstanceId)
            return;

        _isSynchronizingStartupSettings = true;
        try
        {
            BeforeTask = entry.BeforeTask;
            SoftwarePath = entry.SoftwarePath;
            EmulatorConfig = entry.EmulatorConfig;
            WaitSoftwareTime = entry.WaitSoftwareTime;
        }
        finally
        {
            _isSynchronizingStartupSettings = false;
        }
    }

    [RelayCommand]
    private void AddExtraLaunchEntry()
    {
        ExtraLaunchEntries.Add(new ExtraLaunchEntry(this)
        {
            Name = $"{LangKeys.ExtraLaunchItem.ToLocalization()} {ExtraLaunchEntries.Count + 1}"
        });
        SaveExtraLaunchEntries();
    }

    internal void RemoveExtraLaunchEntry(ExtraLaunchEntry entry)
    {
        ExtraLaunchEntries.Remove(entry);
        SaveExtraLaunchEntries();
    }

    internal void SaveExtraLaunchEntries()
    {
        var previousCountText = GlobalConfiguration.GetValue(ConfigurationKeys.GlobalExtraLaunchCount, "0");
        var previousCount = int.TryParse(previousCountText, out var parsedCount) ? parsedCount : 0;
        GlobalConfiguration.SetValue(ConfigurationKeys.GlobalExtraLaunchCount, ExtraLaunchEntries.Count.ToString());
        for (var i = 0; i < ExtraLaunchEntries.Count; i++)
        {
            var entry = ExtraLaunchEntries[i];
            GlobalConfiguration.SetValue(string.Format(ConfigurationKeys.GlobalExtraLaunchNameKeyFormat, i), entry.Name);
            GlobalConfiguration.SetValue(string.Format(ConfigurationKeys.GlobalExtraLaunchPathKeyFormat, i), entry.SoftwarePath);
            GlobalConfiguration.SetValue(string.Format(ConfigurationKeys.GlobalExtraLaunchArgsKeyFormat, i), entry.Arguments);
            GlobalConfiguration.SetValue(string.Format(ConfigurationKeys.GlobalExtraLaunchWaitKeyFormat, i), entry.WaitTime.ToString());
            GlobalConfiguration.SetValue(string.Format(ConfigurationKeys.GlobalExtraLaunchEnabledKeyFormat, i), entry.IsIncluded.ToString());
        }

        if (previousCount <= ExtraLaunchEntries.Count) return;
        GlobalConfiguration.RemoveValues(Enumerable.Range(ExtraLaunchEntries.Count, previousCount - ExtraLaunchEntries.Count)
            .SelectMany(i => new[]
            {
                string.Format(ConfigurationKeys.GlobalExtraLaunchNameKeyFormat, i),
                string.Format(ConfigurationKeys.GlobalExtraLaunchPathKeyFormat, i),
                string.Format(ConfigurationKeys.GlobalExtraLaunchArgsKeyFormat, i),
                string.Format(ConfigurationKeys.GlobalExtraLaunchWaitKeyFormat, i),
                string.Format(ConfigurationKeys.GlobalExtraLaunchEnabledKeyFormat, i)
            }));
    }

    private void LoadExtraLaunchEntries()
    {
        ExtraLaunchEntries.Clear();
        var countText = GlobalConfiguration.GetValue(ConfigurationKeys.GlobalExtraLaunchCount, "0");
        var count = int.TryParse(countText, out var parsedCount) ? Math.Max(0, parsedCount) : 0;
        for (var i = 0; i < count; i++)
        {
            var name = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchNameKeyFormat, i),
                $"{LangKeys.ExtraLaunchItem.ToLocalization()} {i + 1}");
            var path = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchPathKeyFormat, i), string.Empty);
            var args = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchArgsKeyFormat, i), string.Empty);
            var waitText = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchWaitKeyFormat, i), "0");
            var wait = double.TryParse(waitText, out var parsedWait) ? parsedWait : 0;
            var enabled = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchEnabledKeyFormat, i),
                bool.TrueString) == bool.TrueString;
            ExtraLaunchEntries.Add(new ExtraLaunchEntry(this, name, path, args, wait, enabled));
        }
    }

    private static void RemoveLegacyGlobalEmulatorConfiguration()
    {
        var keys = new List<string>
        {
            "GlobalBeforeTask",
            "GlobalSoftwarePath",
            "GlobalEmulatorConfig",
            "GlobalWaitSoftwareTime",
            "GlobalEmulatorCount"
        };

        keys.AddRange(Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            $"GlobalEmulator_{i}_Path",
            $"GlobalEmulator_{i}_Args",
            $"GlobalEmulator_{i}_Name"
        }));
        GlobalConfiguration.RemoveValues(keys);
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

public partial class GlobalStartInstanceEntry : ObservableObject
{
    private readonly StartSettingsUserControlModel _parent;
    private readonly InstanceConfiguration _configuration;

    public GlobalStartInstanceEntry(
        StartSettingsUserControlModel parent,
        string instanceId,
        string instanceName,
        InstanceConfiguration configuration)
    {
        _parent = parent;
        InstanceId = instanceId;
        InstanceName = instanceName;
        _configuration = configuration;
        _beforeTask = configuration.GetValue(ConfigurationKeys.BeforeTask, "None");
        _softwarePath = configuration.GetValue(ConfigurationKeys.SoftwarePath, string.Empty);
        _emulatorConfig = configuration.GetValue(ConfigurationKeys.EmulatorConfig, string.Empty);
        _waitSoftwareTime = configuration.GetValue(ConfigurationKeys.WaitSoftwareTime, 60.0);
        _isIncluded = configuration.GetValue(ConfigurationKeys.IncludeInGlobalStart, true);
    }

    public string InstanceId { get; }
    public string InstanceName { get; }

    public AvaloniaList<LocalizationViewModel> BeforeTaskList { get; } =
    [
        new("None"),
        new("StartupSoftware"),
        new("StartupSoftwareAndScript"),
        new("StartupScriptOnly")
    ];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private string? _beforeTask;
    [ObservableProperty] private string _softwarePath;
    [ObservableProperty] private string _emulatorConfig;
    [ObservableProperty] private double _waitSoftwareTime;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnBeforeTaskChanged(string? value)
    {
        _configuration.SetValue(ConfigurationKeys.BeforeTask, value ?? "None");
        _parent.SyncCurrentSettingsFromEntry(this);
    }

    partial void OnSoftwarePathChanged(string value)
    {
        _configuration.SetValue(ConfigurationKeys.SoftwarePath, value);
        _parent.SyncCurrentSettingsFromEntry(this);
    }

    partial void OnEmulatorConfigChanged(string value)
    {
        _configuration.SetValue(ConfigurationKeys.EmulatorConfig, value);
        _parent.SyncCurrentSettingsFromEntry(this);
    }

    partial void OnWaitSoftwareTimeChanged(double value)
    {
        _configuration.SetValue(ConfigurationKeys.WaitSoftwareTime, value);
        _parent.SyncCurrentSettingsFromEntry(this);
    }

    partial void OnIsIncludedChanged(bool value) =>
        _configuration.SetValue(ConfigurationKeys.IncludeInGlobalStart, value);

    [RelayCommand]
    private async Task BrowsePath()
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null) return;

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.SelectExecutableFile.ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LangKeys.ExeFilter.ToLocalization()) { Patterns = ["*"] }]
        });

        if (result is { Count: > 0 } && result[0].TryGetLocalPath() is { } path)
            SoftwarePath = path;
    }
}

public partial class ExtraLaunchEntry : ObservableObject
{
    private readonly StartSettingsUserControlModel _parent;

    public ExtraLaunchEntry(StartSettingsUserControlModel parent)
        : this(parent, string.Empty, string.Empty, string.Empty, 0, true)
    {
    }

    public ExtraLaunchEntry(
        StartSettingsUserControlModel parent,
        string name,
        string softwarePath,
        string arguments,
        double waitTime,
        bool isIncluded)
    {
        _parent = parent;
        _name = name;
        _softwarePath = softwarePath;
        _arguments = arguments;
        _waitTime = waitTime;
        _isIncluded = isIncluded;
    }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isIncluded;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _softwarePath;
    [ObservableProperty] private string _arguments;
    [ObservableProperty] private double _waitTime;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Remove() => _parent.RemoveExtraLaunchEntry(this);

    partial void OnIsIncludedChanged(bool value) => _parent.SaveExtraLaunchEntries();
    partial void OnNameChanged(string value) => _parent.SaveExtraLaunchEntries();
    partial void OnSoftwarePathChanged(string value) => _parent.SaveExtraLaunchEntries();
    partial void OnArgumentsChanged(string value) => _parent.SaveExtraLaunchEntries();
    partial void OnWaitTimeChanged(double value) => _parent.SaveExtraLaunchEntries();

    [RelayCommand]
    private async Task BrowsePath()
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null) return;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.SelectExecutableFile.ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LangKeys.ExeFilter.ToLocalization()) { Patterns = ["*"] }]
        });
        if (result is { Count: > 0 } && result[0].TryGetLocalPath() is { } path)
            SoftwarePath = path;
    }
}
