using System;
using System.Threading;

namespace MFAAvalonia.Helper;

public enum MobileRunMode
{
    VirtualDisplay,
    CurrentScreen
}

public enum MobileRunResolution
{
    P720,
    P1080
}

public readonly record struct MobileDisplayTarget(int DisplayId, string? PackageName);

public static class MobileRunConfiguration
{
    private static int _activeDisplayId = -1;
    private static int _mfaDisplayId = -1;

    public static MobileRunMode Mode { get; set; } = MobileRunMode.VirtualDisplay;
    public static MobileRunResolution Resolution { get; set; } = MobileRunResolution.P720;
    public static int ActiveDisplayId
    {
        get => Volatile.Read(ref _activeDisplayId);
        set => Volatile.Write(ref _activeDisplayId, value);
    }
    public static int MfaDisplayId
    {
        get => Volatile.Read(ref _mfaDisplayId);
        set => Volatile.Write(ref _mfaDisplayId, value);
    }
    public static Func<MobileDisplayTarget>? ResolveFocusedDisplay { get; set; }
    public static Action? StopBackgroundGameKeepAlive { get; set; }
    public static Action? RequestCurrentScreenOverlayPermission { get; set; }
}
