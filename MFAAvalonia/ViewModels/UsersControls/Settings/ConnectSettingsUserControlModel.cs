using CommunityToolkit.Mvvm.ComponentModel;
using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using System.Collections.ObjectModel;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class ConnectSettingsUserControlModel : ViewModelBase
{
    /// <summary>
    /// 批量同步标志，为 true 时跳过属性变更的副作用（SetTasker、写回配置等）
    /// </summary>
    public bool IsSyncing { get; set; }

    /// <summary>
    /// 构造完成后从当前实例配置重新同步所有属性。
    /// 字段初始化器可能在 MaaProcessorManager.Current 还是 "default" 时就已执行，
    /// 导致枚举类型属性（截图模式、触控模式）被错误地设为 Instance.default 的值。
    /// 此处在 IsSyncing 保护下重新读取，确保值来自正确的实例配置且不触发副作用。
    /// </summary>
    protected override void Initialize()
    {
        IsSyncing = true;
        try
        {
            var config = ConfigurationManager.CurrentInstance;
            CurrentControllerType = config.GetValue(ConfigurationKeys.CurrentController,
                MaaControllerTypes.Adb, MaaControllerTypes.None, new UniversalEnumConverter<MaaControllerTypes>());
            RememberAdb = config.GetValue(ConfigurationKeys.RememberAdb, true);
            UseFingerprintMatching = config.GetValue(ConfigurationKeys.UseFingerprintMatching, true);
            AdbControlScreenCapType = config.GetValue(ConfigurationKeys.AdbControlScreenCapType,
                AdbScreencapMethods.None, [AdbScreencapMethods.All, AdbScreencapMethods.Default],
                new UniversalEnumConverter<AdbScreencapMethods>());
            AdbControlInputType = config.GetValue(ConfigurationKeys.AdbControlInputType,
                AdbInputMethods.None, [AdbInputMethods.All, AdbInputMethods.Default],
                new UniversalEnumConverter<AdbInputMethods>());
            Win32ControlScreenCapType = config.GetValue(ConfigurationKeys.Win32ControlScreenCapType,
                Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None,
                new UniversalEnumConverter<Win32ScreencapMethods>());
            Win32ControlMouseType = config.GetValue(ConfigurationKeys.Win32ControlMouseType,
                Win32InputMethod.SendMessage, Win32InputMethod.None,
                new UniversalEnumConverter<Win32InputMethod>());
            Win32ControlKeyboardType = config.GetValue(ConfigurationKeys.Win32ControlKeyboardType,
                Win32InputMethod.SendMessage, Win32InputMethod.None,
                new UniversalEnumConverter<Win32InputMethod>());
            MacOSControlScreenCapType = config.GetValue(ConfigurationKeys.MacOSControlScreenCapType,
                MacOSScreencapMethod.ScreenCaptureKit, default(MacOSScreencapMethod), new UniversalEnumConverter<MacOSScreencapMethod>());
            MacOSControlInputType = config.GetValue(ConfigurationKeys.MacOSControlInputType,
                MacOSInputMethod.GlobalEvent, default(MacOSInputMethod), new UniversalEnumConverter<MacOSInputMethod>());
            GamepadControlScreenCapType = config.GetValue(ConfigurationKeys.GamepadControlScreenCapType,
                Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None, new UniversalEnumConverter<Win32ScreencapMethods>());
            GamepadType = config.GetValue(ConfigurationKeys.GamepadType,
                MaaFramework.Binding.GamepadType.Xbox360, default(MaaFramework.Binding.GamepadType), new UniversalEnumConverter<MaaFramework.Binding.GamepadType>());
            RetryOnDisconnected = config.GetValue(ConfigurationKeys.RetryOnDisconnected, false);
            RetryOnDisconnectedWin32 = config.GetValue(ConfigurationKeys.RetryOnDisconnectedWin32, false);
            AllowAdbRestart = config.GetValue(ConfigurationKeys.AllowAdbRestart, true);
            AllowAdbHardRestart = config.GetValue(ConfigurationKeys.AllowAdbHardRestart, true);
            AutoDetectOnConnectionFailed = config.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true);
            AutoConnectAfterRefresh = config.GetValue(ConfigurationKeys.AutoConnectAfterRefresh, true);
            AgentTcpMode = config.GetValue(ConfigurationKeys.AgentTcpMode, false);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// 当前控制器类型，用于连接设置页面 ADB/Win32 卡片可见性切换
    /// </summary>
    [ObservableProperty] private MaaControllerTypes _currentControllerType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.CurrentController, MaaControllerTypes.Adb, MaaControllerTypes.None, new UniversalEnumConverter<MaaControllerTypes>());

    [ObservableProperty] private bool _rememberAdb = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RememberAdb, true);

    partial void OnRememberAdbChanged(bool value)
    {
        if (IsSyncing) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.RememberAdb, value);
    }

    [ObservableProperty] private bool _useFingerprintMatching = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.UseFingerprintMatching, true);

    partial void OnUseFingerprintMatchingChanged(bool value)
    {
        if (IsSyncing) return;
        ConfigurationManager.CurrentInstance.SetValue(ConfigurationKeys.UseFingerprintMatching, value);
    }

    public static ObservableCollection<LocalizationViewModel> AdbControlScreenCapTypes =>
    [
        new("Default")
        {
            Other = AdbScreencapMethods.None
        },
        new("RawWithGzip")
        {
            Other = AdbScreencapMethods.RawWithGzip
        },
        new("RawByNetcat")
        {
            Other = AdbScreencapMethods.RawByNetcat
        },
        new("Encode")
        {
            Other = AdbScreencapMethods.Encode
        },
        new("EncodeToFileAndPull")
        {
            Other = AdbScreencapMethods.EncodeToFileAndPull
        },
        new("MinicapDirect")
        {
            Other = AdbScreencapMethods.MinicapDirect
        },
        new("MinicapStream")
        {
            Other = AdbScreencapMethods.MinicapStream
        },
        new("EmulatorExtras")
        {
            Other = AdbScreencapMethods.EmulatorExtras
        }
    ];

    public static ObservableCollection<LocalizationViewModel> AdbControlInputTypes =>
    [
        new("AutoDetect")
        {
            Other = AdbInputMethods.None
        },
        new("MiniTouch")
        {
            Other = AdbInputMethods.MinitouchAndAdbKey
        },
        new("MaaTouch")
        {
            Other = AdbInputMethods.Maatouch
        },
        new("AdbInput")
        {
            Other = AdbInputMethods.AdbShell
        },
        new("EmulatorExtras")
        {
            Other = AdbInputMethods.EmulatorExtras
        },
    ];
    public static ObservableCollection<Win32ScreencapMethods> Win32ControlScreenCapTypes =>
    [
        Win32ScreencapMethods.FramePool, Win32ScreencapMethods.DXGI_DesktopDup, Win32ScreencapMethods.DXGI_DesktopDup_Window, Win32ScreencapMethods.PrintWindow, Win32ScreencapMethods.ScreenDC, Win32ScreencapMethods.GDI
    ];
    public static ObservableCollection<Win32InputMethod> Win32ControlInputTypes =>
    [
        Win32InputMethod.SendMessage, Win32InputMethod.Seize, Win32InputMethod.PostMessage, Win32InputMethod.LegacyEvent, Win32InputMethod.PostThreadMessage, Win32InputMethod.SendMessageWithCursorPos,
        Win32InputMethod.PostMessageWithCursorPos, Win32InputMethod.SendMessageWithWindowPos,
        Win32InputMethod.PostMessageWithWindowPos
    ];
    public static ObservableCollection<MacOSScreencapMethod> MacOSControlScreenCapTypes =>
    [
        MacOSScreencapMethod.ScreenCaptureKit
    ];
    public static ObservableCollection<MacOSInputMethod> MacOSControlInputTypes =>
    [
        MacOSInputMethod.GlobalEvent, MacOSInputMethod.PostToPid
    ];
    public static ObservableCollection<Win32ScreencapMethods> GamepadControlScreenCapTypes => Win32ControlScreenCapTypes;
    public static ObservableCollection<MaaFramework.Binding.GamepadType> GamepadTypes =>
    [
        MaaFramework.Binding.GamepadType.Xbox360, MaaFramework.Binding.GamepadType.DualShock4
    ];

    [ObservableProperty] private AdbScreencapMethods _adbControlScreenCapType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AdbControlScreenCapType, AdbScreencapMethods.None, [AdbScreencapMethods.All, AdbScreencapMethods.Default], new UniversalEnumConverter<AdbScreencapMethods>());
    [ObservableProperty] private AdbInputMethods _adbControlInputType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AdbControlInputType, AdbInputMethods.None, [AdbInputMethods.All, AdbInputMethods.Default], new UniversalEnumConverter<AdbInputMethods>());
    [ObservableProperty] private Win32ScreencapMethods _win32ControlScreenCapType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlScreenCapType, Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None, new UniversalEnumConverter<Win32ScreencapMethods>());
    [ObservableProperty] private Win32InputMethod _win32ControlMouseType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlMouseType, Win32InputMethod.SendMessage, Win32InputMethod.None, new UniversalEnumConverter<Win32InputMethod>());
    [ObservableProperty] private Win32InputMethod _win32ControlKeyboardType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.Win32ControlKeyboardType, Win32InputMethod.SendMessage, Win32InputMethod.None, new UniversalEnumConverter<Win32InputMethod>());
    [ObservableProperty] private MacOSScreencapMethod _macOSControlScreenCapType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.MacOSControlScreenCapType, MacOSScreencapMethod.ScreenCaptureKit, default(MacOSScreencapMethod), new UniversalEnumConverter<MacOSScreencapMethod>());
    [ObservableProperty] private MacOSInputMethod _macOSControlInputType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.MacOSControlInputType, MacOSInputMethod.GlobalEvent, default(MacOSInputMethod), new UniversalEnumConverter<MacOSInputMethod>());
    [ObservableProperty] private Win32ScreencapMethods _gamepadControlScreenCapType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.GamepadControlScreenCapType, Win32ScreencapMethods.FramePool, Win32ScreencapMethods.None, new UniversalEnumConverter<Win32ScreencapMethods>());
    [ObservableProperty] private MaaFramework.Binding.GamepadType _gamepadType =
        ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.GamepadType, MaaFramework.Binding.GamepadType.Xbox360, default(MaaFramework.Binding.GamepadType), new UniversalEnumConverter<MaaFramework.Binding.GamepadType>());

    partial void OnAdbControlScreenCapTypeChanged(AdbScreencapMethods value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AdbControlScreenCapType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnAdbControlInputTypeChanged(AdbInputMethods value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AdbControlInputType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnWin32ControlScreenCapTypeChanged(Win32ScreencapMethods value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.Win32ControlScreenCapType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnWin32ControlMouseTypeChanged(Win32InputMethod value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.Win32ControlMouseType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnWin32ControlKeyboardTypeChanged(Win32InputMethod value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.Win32ControlKeyboardType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnMacOSControlScreenCapTypeChanged(MacOSScreencapMethod value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.MacOSControlScreenCapType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnMacOSControlInputTypeChanged(MacOSInputMethod value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.MacOSControlInputType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnGamepadControlScreenCapTypeChanged(Win32ScreencapMethods value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.GamepadControlScreenCapType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    partial void OnGamepadTypeChanged(MaaFramework.Binding.GamepadType value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.GamepadType, value.ToString(), () => MaaProcessorManager.Instance.Current.SetTasker());
    }

    [ObservableProperty] private bool _retryOnDisconnected = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RetryOnDisconnected, false);

    partial void OnRetryOnDisconnectedChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.RetryOnDisconnected, value);
    }

    [ObservableProperty] private bool _retryOnDisconnectedWin32 = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.RetryOnDisconnectedWin32, false);

    partial void OnRetryOnDisconnectedWin32Changed(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.RetryOnDisconnectedWin32, value);
    }

    [ObservableProperty] private bool _allowAdbRestart = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AllowAdbRestart, true);

    partial void OnAllowAdbRestartChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AllowAdbRestart, value);
    }

    [ObservableProperty] private bool _allowAdbHardRestart = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AllowAdbHardRestart, true);

    partial void OnAllowAdbHardRestartChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AllowAdbHardRestart, value);
    }

    [ObservableProperty] private bool _autoDetectOnConnectionFailed = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AutoDetectOnConnectionFailed, true);

    partial void OnAutoDetectOnConnectionFailedChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AutoDetectOnConnectionFailed, value);
    }

    [ObservableProperty] private bool _autoConnectAfterRefresh = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AutoConnectAfterRefresh, true);

    partial void OnAutoConnectAfterRefreshChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AutoConnectAfterRefresh, value);
    }

    [ObservableProperty] private bool _agentTcpMode = ConfigurationManager.CurrentInstance.GetValue(ConfigurationKeys.AgentTcpMode, false);

    partial void OnAgentTcpModeChanged(bool value)
    {
        if (IsSyncing) return;
        HandlePropertyChanged(ConfigurationKeys.AgentTcpMode, value);
    }
}
