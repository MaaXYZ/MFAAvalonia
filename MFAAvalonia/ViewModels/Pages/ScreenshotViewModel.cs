using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using System;
using System.IO;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace MFAAvalonia.ViewModels.Pages;

public partial class ScreenshotViewModel : ViewModelBase
{
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    [ObservableProperty] private Bitmap? _screenshotImage;
    [ObservableProperty] private string _taskName = string.Empty;
    [RelayCommand]
    private void Screenshot()
    {
        // if (Instances.TaskQueueViewModel.Processor.MaaTasker == null)
        // {
        //     ToastHelper.Warn(LangKeys.Warning.ToLocalization(), (Instances.TaskQueueViewModel.CurrentController == MaaControllerTypes.Adb
        //         ? LangKeys.Emulator.ToLocalization()
        //         : LangKeys.Window.ToLocalization()) + LangKeys.Unconnected.ToLocalization() + "!");
        //     return;
        // }
        try
        {
            var vm = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
            if (vm == null) return;

            if (vm.Processor.MaaTasker is not { IsInitialized: true })
            {
                ToastHelper.Info(LangKeys.Tip.ToLocalization(), LangKeys.ConnectingTo.ToLocalizationFormatted(true, vm.CurrentController == MaaControllerTypes.Adb ? "Emulator" : "Window"));
                vm.Processor.TaskQueue.Enqueue(new MFATask
                {
                    Name = "截图前启动",
                    Type = MFATask.MFATaskType.MFA,
                    Action = async () => await vm.Processor.TestConnecting(),
                    OwnerViewModel = vm,
                });
                vm.Processor.TaskQueue.Enqueue(new MFATask
                {
                    Name = "截图任务",
                    Type = MFATask.MFATaskType.MFA,
                    Action = async () => await TaskManager.RunTaskAsync(() =>
                    {
                        var bitmap = vm.Processor.GetBitmapImage(false);
                        if (bitmap == null)
                        {
                            ToastHelper.Warn(LangKeys.ScreenshotFailed.ToLocalization());
                            return;
                        }

                        DispatcherHelper.PostOnMainThread((() =>
                        {
                            var oldImage = ScreenshotImage;
                            ScreenshotImage = bitmap;
                            oldImage?.Dispose();
                            TaskName = string.Empty;
                        }));
                    }, name: "截图测试"),
                    OwnerViewModel = vm,
                });
                vm.Processor.Start(true, checkUpdate: false);

            }
            else
                TaskManager.RunTaskAsync(() =>
                {
                    // Manual screenshots must request a fresh frame even when live view is disabled.
                    var bitmap = vm.Processor.GetBitmapImage(false);
                    if (bitmap == null)
                    {
                        ToastHelper.Warn(LangKeys.ScreenshotFailed.ToLocalization());
                        return;
                    }

                    DispatcherHelper.PostOnMainThread((() =>
                    {
                        var oldImage = ScreenshotImage;
                        ScreenshotImage = bitmap;
                        oldImage?.Dispose();
                        TaskName = string.Empty;
                    }));
                }, name: "截图测试");
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"执行截图流程失败：原因={e.Message}", e);
        }
    }

    [RelayCommand]
    private void SaveScreenshot()
    {
        if (ScreenshotImage == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.ScreenshotEmpty.ToLocalization());
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

        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        if (storageProvider.SaveFilePickerAsync(options).Result is { } result && result.TryGetLocalPath() is { } path)
        {
            using var stream = File.Create(path);
            ScreenshotImage.Save(stream);
        }
    }
}
