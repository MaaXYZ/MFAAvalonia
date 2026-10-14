using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls;
using MFAAvalonia.Views.UserControls.Settings;
using MFAAvalonia.Views.Pages;
using SukiUI.Dialogs;

namespace MFAAvalonia.Views.Mobile;

public partial class MobileTaskQueueView : UserControl
{
    private const double PreviewAspectRatio = 16.0 / 9.0;
    private TaskQueueViewModel? ViewModel => DataContext as TaskQueueViewModel;
    private Bitmap? _previewBitmap;
    private CancellationTokenSource? _previewCancellation;
    private IMobileVirtualDisplayBackend? _displayBackend;
    private int _displayFramePending;
    private readonly DispatcherTimer _previewStatsTimer;
    private long _fallbackFrameCount;
    private long _lastPreviewFrameCount;
    private DateTime _lastPreviewFrameSample;
    private DragItemViewModel? _activeOptionItem;
    private DragItemViewModel? _lastTaskMenuItem;
    private bool _loadingRunSettings;
    private bool _changingAndroidRunMode;
    private TaskQueueViewModel? _observedViewModel;

    public MobileTaskQueueView()
    {
        InitializeComponent();
        _previewStatsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _previewStatsTimer.Tick += OnPreviewStatsTick;
        NativePreviewHost.Content = MobileVirtualDisplay.PreviewControlFactory?.Invoke();
        SetLogTab(false);
        UpdateInstance();
        LoadAndroidRunSettings();
        MobileInstanceCoordinator.CurrentChanged += OnCurrentInstanceChanged;
        UpdateLanguage();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        AttachedToVisualTree += (_, _) =>
        {
            AttachVirtualDisplayPreview();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            StopPreviewStats();
            StopControllerPreview();
            DetachVirtualDisplayPreview();
            LanguageHelper.LanguageChanged -= OnLanguageChanged;
            MobileInstanceCoordinator.CurrentChanged -= OnCurrentInstanceChanged;
            if (_observedViewModel != null)
                _observedViewModel.PropertyChanged -= OnTaskViewModelPropertyChanged;
        };
    }

    private void OnCurrentInstanceChanged(object? sender, EventArgs e)
    {
        StopControllerPreview();
        UpdateInstance();
        LoadAndroidRunSettings();
        ShowTaskList();
    }

    private void UpdateInstance()
    {
        if (_observedViewModel != null)
            _observedViewModel.PropertyChanged -= OnTaskViewModelPropertyChanged;
        _observedViewModel = MaaProcessorManager.Instance.GetViewModel(MaaProcessorManager.Instance.Current.InstanceId);
        DataContext = _observedViewModel;
        _observedViewModel.PropertyChanged += OnTaskViewModelPropertyChanged;
    }

    private void OnTaskViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskQueueViewModel.IsRunning))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (MobileRunConfiguration.Mode == MobileRunMode.CurrentScreen && ViewModel?.IsRunning == true)
            {
                _previewCancellation ??= new CancellationTokenSource();
                _ = RefreshControllerPreviewAsync(ViewModel.Processor, _previewCancellation.Token);
                StartPreviewStats();
            }
            else if (MobileRunConfiguration.Mode == MobileRunMode.CurrentScreen)
            {
                StopControllerPreview();
            }
            UpdateAndroidRunSettingsEnabled();
            UpdatePreviewState();
        });
    }

    private void LoadAndroidRunSettings()
    {
        var config = ViewModel?.Processor.InstanceConfiguration;
        var modeName = config?.GetValue(ConfigurationKeys.AndroidRunMode,
            MobileRunMode.VirtualDisplay.ToString()) ?? MobileRunMode.VirtualDisplay.ToString();
        var resolutionName = config?.GetValue(ConfigurationKeys.AndroidResolution,
            MobileRunResolution.P720.ToString()) ?? MobileRunResolution.P720.ToString();
        if (!Enum.TryParse(modeName, out MobileRunMode mode))
            mode = MobileRunMode.VirtualDisplay;
        if (!Enum.TryParse(resolutionName, out MobileRunResolution resolution))
            resolution = MobileRunResolution.P720;
        MobileRunConfiguration.Mode = mode;
        MobileRunConfiguration.Resolution = resolution;
        _loadingRunSettings = true;
        AndroidRunModeSelector.SelectedIndex = mode == MobileRunMode.VirtualDisplay ? 0 : 1;
        AndroidResolutionSelector.SelectedIndex = resolution == MobileRunResolution.P720 ? 0 : 1;
        _loadingRunSettings = false;
        UpdateAndroidRunSettingsEnabled();
        UpdatePreviewState();
    }

    private async void OnAndroidRunModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingRunSettings || _changingAndroidRunMode)
            return;
        if (AndroidRunModeSelector.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out MobileRunMode mode))
            return;
        if (mode == MobileRunConfiguration.Mode)
            return;

        var previousMode = MobileRunConfiguration.Mode;
        _changingAndroidRunMode = true;
        AndroidRunModeSelector.IsEnabled = false;
        AndroidResolutionSelector.IsEnabled = false;
        try
        {
            StopControllerPreview();
            if (_displayBackend?.IsRunning == true)
                await _displayBackend.StopAsync();

            MobileRunConfiguration.Mode = mode;
            ViewModel?.Processor.InstanceConfiguration.SetValue(
                ConfigurationKeys.AndroidRunMode, mode.ToString());
            if (ViewModel?.Processor is { } processor)
                await Task.Run(() => processor.SetTasker());

            if (mode == MobileRunMode.CurrentScreen)
                MobileRunConfiguration.RequestCurrentScreenOverlayPermission?.Invoke();
        }
        catch (Exception ex)
        {
            MobileRunConfiguration.Mode = previousMode;
            _loadingRunSettings = true;
            AndroidRunModeSelector.SelectedIndex = previousMode == MobileRunMode.VirtualDisplay ? 0 : 1;
            _loadingRunSettings = false;
            LoggerHelper.Error($"切换 Android 运行模式失败：{ex.Message}");
            VirtualDisplayStatus.Text = MobileLocalization.Format("VirtualOperationFailed", ex.Message);
        }
        finally
        {
            _changingAndroidRunMode = false;
            UpdateAndroidRunSettingsEnabled();
            UpdatePreviewState();
        }
    }

    private void OnAndroidResolutionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingRunSettings)
            return;
        if (AndroidResolutionSelector.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse(tag, out MobileRunResolution resolution))
            return;
        MobileRunConfiguration.Resolution = resolution;
        ViewModel?.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.AndroidResolution, resolution.ToString());
        ViewModel?.Processor.SetTasker();
    }

    private void UpdateAndroidRunSettingsEnabled()
    {
        var idle = ViewModel?.IsRunning != true && !_changingAndroidRunMode;
        AndroidRunModeSelector.IsEnabled = idle;
        AndroidResolutionSelector.IsEnabled = idle
                                              && MobileRunConfiguration.Mode == MobileRunMode.VirtualDisplay;
    }

    private void UpdatePreviewState()
    {
        var running = MobileRunConfiguration.Mode == MobileRunMode.VirtualDisplay
            ? _displayBackend?.IsRunning == true
            : ViewModel?.IsRunning == true;
        NativePreviewHost.IsVisible = _displayBackend?.IsRunning == true;
        VirtualDisplayStatus.Text = running
            ? MobileLocalization.Get("Running")
            : MobileRunConfiguration.Mode == MobileRunMode.VirtualDisplay
                ? MobileLocalization.Get("VirtualStopped")
                : MobileLocalization.Get("CurrentScreen");
        VirtualDisplayFpsBadge.IsVisible = true;
        VirtualDisplayRunningBadge.IsVisible = true;
        if (running)
            StartPreviewStats();
        else
            StopPreviewStats();
    }

    private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e) => UpdateLanguage();

    private void OnPreviewHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var availableWidth = e.NewSize.Width;
        var availableHeight = e.NewSize.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
            return;

        var width = Math.Min(availableWidth, availableHeight * PreviewAspectRatio);
        NativePreviewHost.Width = width;
        NativePreviewHost.Height = width / PreviewAspectRatio;
    }

    private void UpdateLanguage()
    {
        VirtualDisplayTitle.Text = MobileLocalization.Get("VirtualDisplay");
        VirtualDisplayModeItem.Content = MobileLocalization.Get("AndroidVirtualDisplayMode");
        CurrentScreenModeItem.Content = MobileLocalization.Get("AndroidCurrentScreenMode");
        if (_previewCancellation == null)
            VirtualDisplayStatus.Text = MobileLocalization.Get("VirtualStopped");
        TaskQueueTitle.Text = MobileLocalization.Get("TaskQueue");
        TaskQueueTabText.Text = MobileLocalization.Get("TaskQueue");
        LogTabText.Text = MobileLocalization.Get("UserLogs");
        UserLogsTitle.Text = MobileLocalization.Get("UserLogs");
        TaskQueueDescription.Text = MobileLocalization.Get("TaskQueueDescription");
        OptionIntroductionTitle.Text = MobileLocalization.Get("TaskDescription");
        StartTasksText.Text = MobileLocalization.Get("StartTasks");
        StopTasksText.Text = MobileLocalization.Get("StopTasks");
        if (_activeOptionItem != null)
            UpdateOptionIntroduction(_activeOptionItem);
    }

    private void ShowTaskQueueTab(object? sender, RoutedEventArgs e) => SetLogTab(false);

    private void ShowLogTab(object? sender, RoutedEventArgs e) => SetLogTab(true);

    private void SetLogTab(bool showLogs)
    {
        TaskQueueHost.IsVisible = !showLogs;
        TaskToggleButton.IsVisible = !showLogs;
        LogHost.IsVisible = showLogs;
        TaskQueueTabButton.Classes.Set("selected", !showLogs);
        LogTabButton.Classes.Set("selected", showLogs);
    }

    private void OpenTaskOptions(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: DragItemViewModel item } || ViewModel == null)
            return;

        MobileOptionPanel.Children.Clear();
        var generator = new TaskOptionGenerator(ViewModel, SaveConfiguration);
        if (item.IsResourceOptionItem)
            generator.GenerateResourceOptionPanelContent(MobileOptionPanel, item);
        else
            generator.GeneratePanelContent(MobileOptionPanel, item);

        _activeOptionItem = item;
        UpdateOptionIntroduction(item);
        OptionTitle.Text = item.Name;
        PreviewHost.IsVisible = false;
        TaskList.IsVisible = false;
        OptionHost.IsVisible = true;
        SelectAllButton.IsVisible = false;
    }

    private void ShowTasks(object? sender, RoutedEventArgs e) => ShowTaskList();

    private void ShowTaskList()
    {
        MobileOptionPanel.Children.Clear();
        _activeOptionItem = null;
        OptionIntroduction.Markdown = string.Empty;
        OptionIntroductionCard.IsVisible = false;
        OptionHost.IsVisible = false;
        PreviewHost.IsVisible = true;
        TaskList.IsVisible = true;
        SelectAllButton.IsVisible = true;
    }

    private void UpdateOptionIntroduction(DragItemViewModel item)
    {
        var source = item.IsResourceOptionItem
            ? item.ResourceItem?.Description
            : TaskQueueView.GetTooltipText(item.InterfaceItem?.Description, item.InterfaceItem?.Document);
        var introduction = TaskQueueView.ConvertCustomMarkup(source ?? string.Empty);

        OptionIntroduction.Markdown = introduction;
        OptionIntroductionCard.IsVisible = !string.IsNullOrWhiteSpace(introduction);
    }

    private void SaveConfiguration()
    {
        if (ViewModel == null)
            return;

        var config = ViewModel.Processor.InstanceConfiguration;
        config.SetValue(ConfigurationKeys.TaskItems,
            ViewModel.TaskItemViewModels
                .Where(item => !item.IsResourceOptionItem)
                .Select(item => item.InterfaceItem)
                .ToList());

        var resourceItems = ViewModel.TaskItemViewModels
            .Where(item => item.IsResourceOptionItem && item.ResourceItem?.SelectOptions != null)
            .ToList();
        var global = resourceItems.FirstOrDefault(item => item.ResourceItem?.Name == "__GlobalOption__");
        if (global?.ResourceItem?.SelectOptions != null)
            config.SetValue(ConfigurationKeys.GlobalOptionItems, global.ResourceItem.SelectOptions);

        const string controllerPrefix = "__ControllerOption__";
        var controllerOptions = resourceItems
            .Where(item => item.ResourceItem?.Name?.StartsWith(controllerPrefix) == true)
            .ToDictionary(item => item.ResourceItem!.Name![controllerPrefix.Length..], item => item.ResourceItem!.SelectOptions!);
        if (controllerOptions.Count > 0)
            config.SetValue(ConfigurationKeys.ControllerOptionItems, controllerOptions);

        var resourceOptions = resourceItems
            .Where(item => item.ResourceItem?.Name != "__GlobalOption__"
                           && item.ResourceItem?.Name?.StartsWith(controllerPrefix) != true)
            .ToDictionary(item => item.ResourceItem!.Name ?? string.Empty, item => item.ResourceItem!.SelectOptions!);
        config.SetValue(ConfigurationKeys.ResourceOptionItems, resourceOptions);
    }

    private void MoveTaskUp(object? sender, RoutedEventArgs e) => MoveTask(sender, -1);

    private void MoveTaskDown(object? sender, RoutedEventArgs e) => MoveTask(sender, 1);

    private void MoveTask(object? sender, int direction)
    {
        if (sender is not Button { CommandParameter: DragItemViewModel item }
            || ViewModel is not { IsRunning: false } viewModel
            || item.IsResourceOptionItem)
        {
            return;
        }

        var items = viewModel.TaskItemViewModels;
        var currentIndex = items.IndexOf(item);
        if (currentIndex < 0)
            return;

        var targetIndex = currentIndex + direction;
        while (targetIndex >= 0 && targetIndex < items.Count && items[targetIndex].IsResourceOptionItem)
            targetIndex += direction;

        if (targetIndex < 0 || targetIndex >= items.Count)
            return;

        items.Move(currentIndex, targetIndex);
        SaveConfiguration();
    }

    private void TaskItem_OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: DragItemViewModel item } control
            || control.ContextMenu is not ContextMenu menu)
            return;

        _lastTaskMenuItem = item;
        menu.DataContext = item;
        ApplyTaskMenuEnabledStates(menu, item);
    }

    private void TaskMenu_OnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        var item = _lastTaskMenuItem ?? menu.DataContext as DragItemViewModel;
        menu.DataContext = item;
        ApplyTaskMenuEnabledStates(menu, item);
    }

    private static void ApplyTaskMenuEnabledStates(ContextMenu menu, DragItemViewModel? item)
    {
        var menuItems = menu.Items?.OfType<MenuItem>().ToList();
        if (menuItems is not { Count: >= 6 })
            return;

        var editable = item is { IsResourceOptionItem: false };
        var runnable = editable && Instances.RootViewModel.Idle;
        menuItems[0].IsEnabled = runnable;
        menuItems[1].IsEnabled = runnable;
        for (var index = 2; index < 6; index++)
            menuItems[index].IsEnabled = editable;
    }

    private DragItemViewModel? GetMenuItem(object? sender) =>
        (sender as MenuItem)?.DataContext as DragItemViewModel;

    private void RunSingleTask(object? sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is { IsResourceOptionItem: false } item && ViewModel is { } viewModel)
            viewModel.Processor.Start([item], ignoreCheckedState: true);
    }

    private void RunCheckedFromCurrent(object? sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { IsResourceOptionItem: false } item || ViewModel is not { } viewModel)
            return;

        var index = viewModel.TaskItemViewModels.IndexOf(item);
        if (index < 0)
            return;

        var tasks = viewModel.TaskItemViewModels.Skip(index)
            .Where(task => (ReferenceEquals(task, item) || task.IsChecked) && task.IsTaskSupported)
            .ToList();
        if (tasks.Count > 0)
            viewModel.Processor.Start(tasks, ignoreCheckedState: true);
    }

    private void CopyTask(object? sender, RoutedEventArgs e)
    {
        var item = GetMenuItem(sender);
        if (item?.InterfaceItem == null || item.IsResourceOptionItem)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            _ = clipboard.SetTextAsync($"{item.InterfaceItem.Name}{TaskLoader.NEW_SEPARATOR}{item.InterfaceItem.Entry}");
    }

    private async void PasteTask(object? sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { IsResourceOptionItem: false } selected || ViewModel is not { } viewModel)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        var parts = text?.Split(TaskLoader.NEW_SEPARATOR, StringSplitOptions.None);
        if (parts is not { Length: 2 })
            return;

        var source = viewModel.Processor.TasksSource.FirstOrDefault(task =>
            task.InterfaceItem?.Name == parts[0] && task.InterfaceItem?.Entry == parts[1]);
        if (source?.InterfaceItem == null)
            return;

        var output = source.Clone();
        output.InterfaceItem.Option?.ForEach(option => TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, option));
        output.OwnerViewModel = viewModel;
        viewModel.RefreshTaskSupportForCurrentContext(output);
        var index = viewModel.TaskItemViewModels.IndexOf(selected);
        viewModel.TaskItemViewModels.Insert(index >= 0 ? index + 1 : viewModel.TaskItemViewModels.Count, output);
        SaveConfiguration();
    }

    private void EditTaskRemark(object? sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { IsResourceOptionItem: false, InterfaceItem: { } interfaceItem } item
            || ViewModel is not { } viewModel)
            return;

        Instances.DialogManager.CreateDialog()
            .WithTitle(LangKeys.TaskRemarkTitle.ToLocalization())
            .WithViewModel(dialog => new TaskRemarkDialogViewModel(dialog,
                interfaceItem.DisplayNameOverride, interfaceItem.Remark, (displayName, remark) =>
                {
                    interfaceItem.DisplayNameOverride = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
                    interfaceItem.Remark = string.IsNullOrWhiteSpace(remark) ? null : remark;
                    item.RefreshDisplayName();
                    SaveConfiguration();
                }))
            .TryShow();
    }

    private void DeleteTask(object? sender, RoutedEventArgs e)
    {
        if (GetMenuItem(sender) is not { IsResourceOptionItem: false } item || ViewModel is not { } viewModel)
            return;

        viewModel.TaskItemViewModels.Remove(item);
        SaveConfiguration();
    }

    private async Task RefreshControllerPreviewAsync(MaaProcessor processor, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var bitmap = await Task.Run(() => processor.GetLiveView(false), token);
                if (bitmap != null)
                    Dispatcher.UIThread.Post(() => SetPreviewBitmap(bitmap), DispatcherPriority.Background);
                await Task.Delay(250, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                    VirtualDisplayStatus.Text = MobileLocalization.Format("VirtualOperationFailed", ex.Message));
                await Task.Delay(1000, token);
            }
        }
    }

    private void SetPreviewBitmap(Bitmap bitmap)
    {
        Interlocked.Increment(ref _fallbackFrameCount);
        var old = _previewBitmap;
        _previewBitmap = bitmap;
        VirtualDisplayImage.Source = bitmap;
        VirtualDisplayImage.IsVisible = true;
        old?.Dispose();
        if (!_previewStatsTimer.IsEnabled)
            StartPreviewStats();
    }

    private void AttachVirtualDisplayPreview()
    {
        NativePreviewHost.Content ??= MobileVirtualDisplay.PreviewControlFactory?.Invoke();
        var backend = MobileVirtualDisplay.Backend;
        if (ReferenceEquals(_displayBackend, backend))
        {
            EnableNativePreviewIfRunning();
            return;
        }

        DetachVirtualDisplayPreview();
        _displayBackend = backend;
        if (_displayBackend != null)
        {
            _displayBackend.FrameReady += OnVirtualDisplayFrameReady;
            _displayBackend.StateChanged += OnVirtualDisplayStateChanged;
            EnableNativePreviewIfRunning();
        }
    }

    private void EnableNativePreviewIfRunning()
    {
        if (_displayBackend?.IsRunning != true)
            return;

        NativePreviewHost.IsVisible = true;
        _previewCancellation ??= new CancellationTokenSource();
        VirtualDisplayStatus.Text = MobileLocalization.Get("VirtualStarted");
        StartPreviewStats();
    }

    private void DetachVirtualDisplayPreview()
    {
        if (_displayBackend != null)
        {
            _displayBackend.FrameReady -= OnVirtualDisplayFrameReady;
            _displayBackend.StateChanged -= OnVirtualDisplayStateChanged;
        }
        _displayBackend = null;
        Interlocked.Exchange(ref _displayFramePending, 0);
    }

    private void OnVirtualDisplayStateChanged() =>
        Dispatcher.UIThread.Post(UpdatePreviewState, DispatcherPriority.Background);

    private void OnVirtualDisplayFrameReady(byte[] jpeg)
    {
        if (_displayBackend?.IsRunning != true)
            return;

        // The Android controller starts its virtual display lazily when a task connects.
        // Enable its preview without requiring the separate package-name button.
        _previewCancellation ??= new CancellationTokenSource();

        if (Interlocked.Exchange(ref _displayFramePending, 1) != 0)
            return;

        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            var bitmap = new Bitmap(stream);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    SetPreviewBitmap(bitmap);
                    VirtualDisplayStatus.Text = MobileLocalization.Get("VirtualStarted");
                }
                finally
                {
                    Interlocked.Exchange(ref _displayFramePending, 0);
                }
            }, DispatcherPriority.Background);
        }
        catch
        {
            Interlocked.Exchange(ref _displayFramePending, 0);
        }
    }

    private void StopControllerPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        NativePreviewHost.IsVisible = false;
        VirtualDisplayImage.IsVisible = false;
        StopPreviewStats();
    }

    private void StartPreviewStats()
    {
        _lastPreviewFrameCount = GetPreviewFrameCount();
        _lastPreviewFrameSample = DateTime.UtcNow;
        VirtualDisplayRunningBadge.IsVisible = true;
        VirtualDisplayFpsBadge.IsVisible = true;
        VirtualDisplayFpsText.Text = "FPS: 0.0";
        _previewStatsTimer.Start();
    }

    private void StopPreviewStats()
    {
        _previewStatsTimer.Stop();
        VirtualDisplayRunningBadge.IsVisible = true;
        VirtualDisplayFpsBadge.IsVisible = true;
        VirtualDisplayFpsText.Text = "FPS: 0.0";
    }

    private void OnPreviewStatsTick(object? sender, EventArgs e)
    {
        if (_displayBackend?.IsRunning != true && _previewCancellation == null)
        {
            StopPreviewStats();
            UpdatePreviewState();
            return;
        }

        var now = DateTime.UtcNow;
        var elapsedSeconds = (now - _lastPreviewFrameSample).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        var frameCount = GetPreviewFrameCount();
        var frameDelta = Math.Max(0, frameCount - _lastPreviewFrameCount);
        VirtualDisplayFpsText.Text = $"FPS: {frameDelta / elapsedSeconds:F1}";
        _lastPreviewFrameCount = frameCount;
        _lastPreviewFrameSample = now;
    }

    private long GetPreviewFrameCount() =>
        _displayBackend?.IsRunning == true
            ? _displayBackend.CapturedFrameCount
            : Interlocked.Read(ref _fallbackFrameCount);
}
