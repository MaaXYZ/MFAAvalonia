using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.Highlighting;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Windows;
using Avalonia.VisualTree;
using MFAAvalonia.Views.Mobile;
using System;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class AboutUserControl : UserControl
{
    public AboutUserControl()
    {
        InitializeComponent();

    }
    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        await FileLogExporter.CompressRecentLogs(storageProvider);
    }
    
    private void DisplayAnnouncement(object? sender, RoutedEventArgs e)
    {
       AnnouncementViewModel.CheckAnnouncement(true);
    }
    
    private void ClearCache_Click(object? sender, RoutedEventArgs e)
    {
        if (!Instances.RootViewModel.Idle)
        {
            ToastHelper.Warn(
                LangKeys.Warning.ToLocalization(),
                LangKeys.StopTaskBeforeClearCache.ToLocalization());
            return;
        }

        var processors = MaaProcessor.Processors.ToList();
        foreach (var processor in processors)
        {
            try
            {
                processor.SetTasker();
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"清理缓存前停止实例 {processor.InstanceId} 的 Tasker 失败: {ex}");
                ToastHelper.Error(LangKeys.ClearCacheFailed.ToLocalization(), $"停止实例 {processor.InstanceId} 失败");
                return;
            }
        }

        var remainingTasker = processors.FirstOrDefault(p => p.MaaTasker != null || p.ScreenshotTasker != null);
        if (remainingTasker != null)
        {
            LoggerHelper.Warning($"清理缓存中止：实例 {remainingTasker.InstanceId} 仍存在未释放 Tasker。");
            ToastHelper.Error(LangKeys.ClearCacheFailed.ToLocalization(), $"实例 {remainingTasker.InstanceId} 仍在占用资源");
            return;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var debugDirectory = Path.Combine(baseDirectory, "debug");
        var logsDirectory = Path.Combine(baseDirectory, "logs");

        try
        {
            ClearDirectory(debugDirectory);
            ClearDirectory(logsDirectory);
            Directory.CreateDirectory(debugDirectory);
            Directory.CreateDirectory(logsDirectory);
            ToastHelper.Success(LangKeys.ClearCacheSuccess.ToLocalization());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"清理缓存失败: {ex.Message}");
            ToastHelper.Error(LangKeys.ClearCacheFailed.ToLocalization(), ex.Message);
        }
    }
    
    private static void ClearDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"清理缓存项失败: {entry}, {ex.Message}");
            }
        }
    }
    
    private void ShowLicense_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as ViewModels.Pages.SettingsViewModel;
        if (viewModel != null && !string.IsNullOrEmpty(viewModel.ResourceLicense))
        {
            LicenseView.ShowLicense(viewModel.ResourceLicense);
        }
    }

    private void StartTutorial_Click(object? sender, RoutedEventArgs e)
    {
        var rootContent = this.GetVisualAncestors().OfType<RootViewContent>().FirstOrDefault();
        rootContent?.TryStartTutorial();
    }
}

