using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaaFramework.Binding;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Views.UserControls;
using SukiUI.Dialogs;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls;

public partial class AdbEditorDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _adbName = LangKeys.Emulator.ToLocalization();
    [ObservableProperty] private string _adbPath = string.Empty;
    [ObservableProperty] private string _adbSerial = string.Empty;
    [ObservableProperty] private string _adbConfig = "{}";
    private readonly AdbScreencapMethods _screencapMethods;
    private readonly AdbInputMethods _adbInputMethods;
    public ISukiDialog Dialog { get; set; }
    public AdbEditorDialogViewModel(AdbDeviceInfo? info, ISukiDialog dialog)
    {
        AdbName = info?.Name ?? AdbName;
        AdbPath = info?.AdbPath ?? AdbPath;
        AdbSerial = info?.AdbSerial ?? AdbSerial;
        AdbConfig = info?.Config ?? AdbConfig;
        _screencapMethods = info?.ScreencapMethods ?? AdbScreencapMethods.Default;
        _adbInputMethods = info?.InputMethods ?? AdbInputMethods.Maatouch;
        Dialog = dialog;
    }

    [RelayCommand]
    async private Task Load()
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
            Title = LangKeys.LoadFileTitle.ToLocalization(),
            FileTypeFilter =
            [
                new FilePickerFileType(LangKeys.AllFilter.ToLocalization())
                {
                    Patterns = ["*"] // 支持所有文件类型
                }
            ]
        };

        var result = await storageProvider.OpenFilePickerAsync(options);

        // 处理选择结果
        if (result is { Count: > 0 } && result[0].TryGetLocalPath() is { } path)
        {
            AdbPath = path;
        }
    }

    [RelayCommand]
    public async Task Save()
    {
        var vm = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
        if (vm != null)
        {
            // 保存即应用自定义连接目标，允许模拟器尚未启动、设备尚未被发现的情况。
            // 是否立即尝试连接由设备选择流程中的连接设置决定。
            await vm.ApplyCustomAdbDevice(Output);
        }

        Dialog.Dismiss();
    }

    public AdbDeviceInfo Output => new(AdbName, AdbPath, AdbSerial, _screencapMethods,
        _adbInputMethods, AdbConfig);
}
