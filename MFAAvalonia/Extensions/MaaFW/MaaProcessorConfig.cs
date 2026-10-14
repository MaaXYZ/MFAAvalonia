using MaaFramework.Binding;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// MaaFW 配置类
/// </summary>
public class MaaFWConfiguration
{
    public AdbDeviceCoreConfig AdbDevice { get; set; } = new();
    public DesktopWindowCoreConfig DesktopWindow { get; set; } = new();
    public MacOSWindowCoreConfig MacOSWindow { get; set; } = new();
    public WlRootsCoreConfig WlRoots { get; set; } = new();
    public GamepadCoreConfig Gamepad { get; set; } = new();

    public PlayCoverCoreConfig PlayCover { get; set; } = new();
}

/// <summary>
/// 桌面窗口核心配置
/// </summary>
public class DesktopWindowCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public nint HWnd { get; set; }
    public Win32InputMethod Mouse { get; set; } = Win32InputMethod.SendMessage;
    public Win32InputMethod KeyBoard { get; set; } = Win32InputMethod.SendMessage;
    public Win32ScreencapMethods ScreenCap { get; set; } = Win32ScreencapMethods.FramePool;
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}

/// <summary>
/// ADB 设备核心配置
/// </summary>
public class AdbDeviceCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public string AdbPath { get; set; } = "adb";
    public string AdbSerial { get; set; } = "";
    public string Config { get; set; } = "{}";
    public AdbInputMethods Input { get; set; } = AdbInputMethods.Default;
    public AdbScreencapMethods ScreenCap { get; set; } = AdbScreencapMethods.Default;
    public AdbDeviceInfo? Info { get; set; } = null;
}

/// <summary>
/// PlayCover 设备核心配置
/// </summary>
public class PlayCoverCoreConfig
{
    public string Name { get; set; } = string.Empty;
    public string PlayCoverAddress { get; set; } = "";
    public string UUID { get; set; } = "maa.playcover";
}

/// <summary>
/// macOS 原生窗口核心配置
/// </summary>
public class MacOSWindowCoreConfig
{
    public uint WindowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public MacOSScreencapMethod ScreenCap { get; set; } = MacOSScreencapMethod.ScreenCaptureKit;
    public MacOSInputMethod Input { get; set; } = MacOSInputMethod.GlobalEvent;
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}

public class GamepadCoreConfig
{
    public GamepadType Type { get; set; } = GamepadType.Xbox360;
    public Win32ScreencapMethods ScreenCap { get; set; } = Win32ScreencapMethods.FramePool;
}

public class WlRootsCoreConfig
{
    public string SocketPath { get; set; } = string.Empty;
    public bool UseWin32VirtualKeyCodes { get; set; }
    public LinkOption Link { get; set; } = LinkOption.Start;
    public CheckStatusOption Check { get; set; } = CheckStatusOption.ThrowIfNotSucceeded;
}
