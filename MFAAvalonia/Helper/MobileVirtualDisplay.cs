using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace MFAAvalonia.Helper;

public interface IMobileVirtualDisplayBackend : IDisposable
{
    bool IsRunning { get; }
    int DisplayId { get; }
    bool CanRestore { get; }
    long CapturedFrameCount { get; }
    event Action? StateChanged;
    event Action<byte[]>? FrameReady;
    Task<MobileVirtualDisplayResult> StartAsync(string packageName, int width, int height, int dpi);
    Task<MobileVirtualDisplayResult> StopAsync();
    Task<MobileVirtualDisplayResult> RestoreAsync();
}

public readonly record struct MobileVirtualDisplayResult(bool Success, string Message, int DisplayId = -1)
{
    public static MobileVirtualDisplayResult Failed(string message) => new(false, message);
    public static MobileVirtualDisplayResult Succeeded(string message, int displayId) => new(true, message, displayId);
}

public static class MobileVirtualDisplay
{
    public static IMobileVirtualDisplayBackend? Backend { get; set; }
    public static Func<Control?>? PreviewControlFactory { get; set; }
    public static bool IsSupported => Backend != null;
}
