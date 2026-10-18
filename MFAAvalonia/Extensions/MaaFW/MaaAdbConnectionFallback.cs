using MaaFramework.Binding;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// MuMu 专用 ADB 截图方式不可用时回退到通用方式。
/// </summary>
public static class MaaAdbConnectionFallback
{
    public static AdbScreencapMethods GetFallbackScreenCap(AdbScreencapMethods screenCap) =>
        screenCap.HasFlag(AdbScreencapMethods.EmulatorExtras)
            ? AdbScreencapMethods.Default
            : screenCap;
}
