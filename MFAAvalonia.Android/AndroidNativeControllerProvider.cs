using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using System;
using System.IO;
using System.Text.Json;

namespace MFAAvalonia.Android;

internal sealed class AndroidNativeControllerProvider
{
    private readonly Activity _activity;
    private readonly ShizukuConnectionManager _shizuku;
    private readonly ShizukuAuthorizationPrompt _authorizationPrompt;
    private readonly AndroidVirtualDisplayBackend _displayBackend;

    public AndroidNativeControllerProvider(Activity activity, ShizukuConnectionManager shizuku,
        ShizukuAuthorizationPrompt authorizationPrompt, AndroidVirtualDisplayBackend displayBackend)
    {
        _activity = activity;
        _shizuku = shizuku;
        _authorizationPrompt = authorizationPrompt;
        _displayBackend = displayBackend;
    }

    public MaaController? Create(MaaControllerTypes controllerType)
    {
        // Mobile interface files still describe their controller as ADB. On Android that
        // logical controller is backed by MaaAndroidNativeControlUnit instead.
        if (controllerType == MaaControllerTypes.None)
            return null;

        if (!_shizuku.IsServiceAvailable || !_shizuku.HasPermission)
        {
            _authorizationPrompt.ShowIfNeeded();
            throw new InvalidOperationException(_shizuku.StatusMessage);
        }
        if (!_shizuku.IsUserServiceReady
            && !_shizuku.WaitForUserServiceReady(TimeSpan.FromSeconds(12)))
        {
            _authorizationPrompt.ShowIfNeeded();
            throw new InvalidOperationException(_shizuku.StatusMessage);
        }

        var nativeLibraryDir = _activity.ApplicationInfo?.NativeLibraryDir
            ?? throw new InvalidOperationException("Android native library directory is unavailable.");
        const string bridgeLibraryName = "libmfabridge.so";
        var bridgePath = Path.Combine(nativeLibraryDir, bridgeLibraryName);
        if (!File.Exists(bridgePath))
            throw new InvalidOperationException(
                "Android Native bridge is not ready. Install and authorize Shizuku, then reconnect the Android controller.");

        var metrics = _activity.Resources?.DisplayMetrics
            ?? throw new InvalidOperationException("Android display metrics are unavailable.");
        // The controller targets landscape games. Some Android hosts keep MFA's activity in
        // portrait and do not auto-rotate virtual displays, so activity metrics cannot be used
        // verbatim here without Android letterboxing the game into a thin horizontal strip.
        var useVirtualDisplay = MobileRunConfiguration.Mode == MobileRunMode.VirtualDisplay;
        var activityDisplay = Build.VERSION.SdkInt >= BuildVersionCodes.R
            ? _activity.Display
            : null;
        var activityDisplayId = activityDisplay?.DisplayId ?? 0;
        MobileRunConfiguration.MfaDisplayId = activityDisplayId;
        if (!useVirtualDisplay && Build.VERSION.SdkInt >= BuildVersionCodes.M
                               && !Settings.CanDrawOverlays(_activity))
        {
            MobileRunConfiguration.RequestCurrentScreenOverlayPermission?.Invoke();
            throw new InvalidOperationException(
                "当前屏模式需要悬浮窗权限，以便在游戏画面上返回 MFA 或停止任务。");
        }
        var currentDisplayId = useVirtualDisplay
            ? activityDisplayId
            : _shizuku.ResolveCurrentScreenDisplayId(activityDisplayId);
        MobileRunConfiguration.ActiveDisplayId = useVirtualDisplay ? -1 : currentDisplayId;
        var displayInfo = useVirtualDisplay
            ? default
            : _shizuku.GetDisplayInfo(currentDisplayId);
        var virtualWidth = useVirtualDisplay
            ? MobileRunConfiguration.Resolution == MobileRunResolution.P1080 ? 1920 : 1280
            : Math.Max(displayInfo.Width, displayInfo.Height);
        var virtualHeight = useVirtualDisplay
            ? MobileRunConfiguration.Resolution == MobileRunResolution.P1080 ? 1080 : 720
            : Math.Min(displayInfo.Width, displayInfo.Height);
        global::Android.Util.Log.Info("MfaVirtualDisplay",
            useVirtualDisplay
                ? $"Controller virtual display size: {virtualWidth}x{virtualHeight}."
                : $"Controller current display: id={currentDisplayId}, size={virtualWidth}x{virtualHeight}, " +
                  $"rotation={displayInfo.Rotation}, layerStack={displayInfo.LayerStack}.");
        var bridgeResult = NativeBridgeInterop.Configure((uint)virtualWidth, (uint)virtualHeight);
        if (bridgeResult != 0)
            throw new InvalidOperationException(
                $"MFA Android native bridge initialization failed (result={bridgeResult}).");
        if (useVirtualDisplay && (!_displayBackend.IsRunning || _displayBackend.IsPrimaryCapture))
        {
            var dpi = (int)metrics.DensityDpi;
            var displayResult = _displayBackend.StartAsync(string.Empty, virtualWidth, virtualHeight, dpi)
                .GetAwaiter().GetResult();
            if (!displayResult.Success)
                throw new InvalidOperationException(displayResult.Message);
        }
        else if (!useVirtualDisplay && (!_displayBackend.IsRunning || !_displayBackend.IsPrimaryCapture))
        {
            var captureResult = _displayBackend.StartPrimaryCaptureAsync(
                    currentDisplayId, virtualWidth, virtualHeight)
                .GetAwaiter().GetResult();
            if (!captureResult.Success)
                throw new InvalidOperationException(captureResult.Message);
        }
        // Creating a virtual display first tries the root UserService and may fall back
        // to the shell helper. Configure the native controller only after that choice is
        // final, otherwise Maa sends StartApp/touch requests to the old root port while
        // capture belongs to the shell-created display.
        if (NativeBridgeInterop.SetInputPort((uint)_shizuku.UserServicePort) != 0)
            throw new InvalidOperationException("MFA Android native input bridge initialization failed.");
        if (useVirtualDisplay)
            _shizuku.SetGameProcessKeepAlive(_displayBackend.DisplayId, true);
        var config = JsonSerializer.Serialize(new
        {
            // NativeCaptureInterop has already loaded this library into the app process.
            // Use its soname so Android's linker returns that same instance. Loading it
            // again through an absolute path can create a second frame store: capture
            // writes to one instance while MaaFW waits forever on the other.
            library_path = bridgeLibraryName,
            screen_resolution = new
            {
                width = virtualWidth,
                height = virtualHeight,
            },
            display_id = useVirtualDisplay && _displayBackend.DisplayId >= 0
                ? _displayBackend.DisplayId
                : currentDisplayId,
            // Match MaaFwApp's display ownership rules. A virtual display must get a
            // fresh task/process; reusing a task already attached to another display
            // leaves Unity and other Surface-based games frozen on one of the screens.
            // Current-screen mode must preserve the user's existing game process.
            force_stop = useVirtualDisplay,
        });

        return new MaaAndroidNativeController(config);
    }
}
