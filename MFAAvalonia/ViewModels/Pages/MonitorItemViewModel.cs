using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using SukiUI.Controls;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.Pages;

public partial class MonitorItemViewModel : ViewModelBase
{
    public MaaProcessor Processor { get; }
    public DateTime CreatedAt { get; } = DateTime.Now;
    private TaskQueueViewModel? _subscribedViewModel;
    private Window? _fullScreenWindow;
    private readonly MonitorViewModel? _owner;

    [ObservableProperty] private Bitmap? _image;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private bool _hasImage;

    public double CardWidth => _owner?.CardWidth ?? 240;
    public double CardHeight => _owner?.CardHeight ?? 420;

    public MonitorItemViewModel(MaaProcessor processor, MonitorViewModel? owner = null)
    {
        Processor = processor;
        _owner = owner;
        if (_owner != null)
            _owner.PropertyChanged += OnOwnerPropertyChanged;
        UpdateInfo();
        TrySubscribeViewModel();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorViewModel.CardWidth)
            or nameof(MonitorViewModel.CardHeight)
            or nameof(MonitorViewModel.CardScale))
        {
            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(CardHeight));
        }
    }

    private void TrySubscribeViewModel()
    {
        var vm = Processor.ViewModel;
        if (vm == _subscribedViewModel) return;
        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = vm;
        if (vm != null)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            SyncImage(vm.LiveViewImage);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskQueueViewModel.LiveViewImage))
            SyncImage(_subscribedViewModel?.LiveViewImage);
    }

    private void SyncImage(Bitmap? source)
    {
        Image = source;
        HasImage = source != null;
    }

    public void UpdateInfo()
    {
        Name = MaaProcessorManager.Instance.GetInstanceName(Processor.InstanceId);
        IsConnected = Processor.ViewModel?.IsConnected ?? false;
        IsRunning = Processor.TaskQueue.Count > 0;
        TrySubscribeViewModel();
    }

    [RelayCommand]
    private void Switch()
    {
        var tab = Instances.InstanceTabBarViewModel.Tabs.FirstOrDefault(t => t.Processor == Processor);
        if (tab != null)
        {
            Instances.InstanceTabBarViewModel.ActiveTab = tab;
            // 导航到主页
            var sideMenu = Instances.TopLevel?.GetVisualDescendants().OfType<SukiSideMenu>().FirstOrDefault();
            var homeItem = sideMenu?.Items.OfType<SukiSideMenuItem>().FirstOrDefault();
            if (homeItem != null)
                sideMenu!.SelectedItem = homeItem;
        }
    }
    [RelayCommand]
    private void StartTask()
    {
        Processor.Start();
    }

    [RelayCommand]
    private void StopTask()
    {
        Processor.Stop(MFATask.MFATaskStatus.STOPPED);
    }

    [RelayCommand]
    private async Task Connect()
    {
        try
        {
            if (Processor.ViewModel != null)
                await Processor.ViewModel.ReconnectCommand.ExecuteAsync(null);
            else
                await Processor.ReconnectAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex.Message == MaaProcessor.ConnectionFailedAfterAllRetriesMessage)
        {
            LoggerHelper.Warning($"监控页重连失败：原因={ex.Message}");
        }
        finally
        {
            UpdateInfo();
        }
    }

    [RelayCommand]
    private void Rename()
    {
        var tab = Instances.InstanceTabBarViewModel.Tabs.FirstOrDefault(t => t.Processor == Processor);
        if (tab != null)
            Instances.InstanceTabBarViewModel.RenameInstanceCommand.Execute(tab);
    }

    [RelayCommand]
    private void Delete()
    {
        var tab = Instances.InstanceTabBarViewModel.Tabs.FirstOrDefault(t => t.Processor == Processor);
        if (tab != null)
            Instances.InstanceTabBarViewModel.CloseInstanceCommand.Execute(tab);
    }

    [RelayCommand]
    private void FullScreen()
    {
        if (Image == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.ScreenshotEmpty.ToLocalization());
            return;
        }

        if (_fullScreenWindow is { IsVisible: true })
        {
            _fullScreenWindow.Activate();
            return;
        }

        var imageControl = new Image
        {
            Stretch = Avalonia.Media.Stretch.Uniform
        };
        imageControl.Bind(Avalonia.Controls.Image.SourceProperty, new Binding(nameof(Image)) { Source = this });

        var window = new Window
        {
            Title = Name,
            Content = imageControl,
            Background = Avalonia.Media.Brushes.Black,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowState = WindowState.FullScreen
        };

        window.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F11)
                window.Close();
        };
        window.Closed += (_, _) => _fullScreenWindow = null;

        _fullScreenWindow = window;
        if (Instances.TopLevel is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    [RelayCommand]
    private async Task SaveScreenshot()
    {
        if (Image == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.ScreenshotEmpty.ToLocalization());
            return;
        }

        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        var options = new FilePickerSaveOptions
        {
            Title = LangKeys.SaveScreenshot.ToLocalization(),
            FileTypeChoices =
            [
                new FilePickerFileType("PNG")
                {
                    Patterns = ["*.png"]
                }
            ]
        };

        var result = await storageProvider.SaveFilePickerAsync(options);
        if (result?.TryGetLocalPath() is not { } path) return;

        using var stream = File.Create(path);
        Image.Save(stream);
    }

    [RelayCommand]
    private async Task CopyScreenshot()
    {
        if (Image == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.ScreenshotEmpty.ToLocalization());
            return;
        }

        var clipboard = Instances.Clipboard;
        if (clipboard == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DataFormat.Bitmap, Image));

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "MFAAvalonia_Clipboard", $"monitor_{Guid.NewGuid()}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            Image.Save(tempPath);
            IStorageFile? storageFile = null;
            var storageProvider = Instances.StorageProvider;
            if (storageProvider != null)
                storageFile = await storageProvider.TryGetFileFromPathAsync(tempPath);
            if (storageFile != null)
                dataTransfer.Add(DataTransferItem.CreateFile(storageFile));
        }
        catch
        {
            // ignore temp-file fallback errors
        }

        await clipboard.SetDataAsync(dataTransfer);
        ToastHelper.Info(LangKeys.CopiedToClipboard.ToLocalization());
    }

    [RelayCommand]
    private void Disconnect()
    {
        Processor.Stop(MFATask.MFATaskStatus.STOPPED);
        Processor.SetTasker();
        SyncImage(null);
        UpdateInfo();
    }

    public void Dispose()
    {
        if (_subscribedViewModel != null)
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_owner != null)
            _owner.PropertyChanged -= OnOwnerPropertyChanged;

        if (_fullScreenWindow is { IsVisible: true })
            _fullScreenWindow.Close();
    }
}
