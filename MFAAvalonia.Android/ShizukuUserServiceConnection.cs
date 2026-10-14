using Android.Content;
using Android.OS;
using Android.Views;
using Rikka.Shizuku;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Android;

internal sealed class ShizukuUserServiceConnection : Java.Lang.Object, IServiceConnection
{
    private const int HealthTransaction = 1;
    private const int CreateDisplayTransaction = 2;
    private const int ReleaseDisplayTransaction = 3;
    private const int StartAppTransaction = 4;
    private const int CreatePrimaryCaptureTransaction = 5;
    private const int GetDisplayInfoTransaction = 6;
    private const int ResolveCaptureDisplayTransaction = 7;
    private const int GetFocusedDisplayTransaction = 8;
    private const int SetGameKeepAliveTransaction = 9;
    private const int OverlayInputStateTransaction = 1;
    private const string ServiceClassName = "com.fox.MFAAvalonia.MfaShizukuUserService";
    private static int _serviceVersion = 100;
    private readonly Action<bool, int, int, string?> _stateChanged;
    private Shizuku.UserServiceArgs? _args;
    private IBinder? _service;
    private IBinder? _rootService;
    private IBinder? _shellService;
    private bool _usingRootService;
    private Context? _context;
    private readonly OverlayInputStateBinder _overlayInputStateBinder = new();

    public ShizukuUserServiceConnection(Action<bool, int, int, string?> stateChanged) => _stateChanged = stateChanged;

    public void Bind(Context context)
    {
        if (_args != null) return;
        _context = context.ApplicationContext ?? context;
        _args = new Shizuku.UserServiceArgs(new ComponentName(context.PackageName, ServiceClassName));
        _args.ProcessNameSuffix("mfa_service");
        _args.Daemon(false);
        _args.Debuggable(true);
        // Match MaaFwApp: every binding gets a fresh version and tag, so Shizuku
        // never reconnects a stale UserService after an app update or reconnect.
        _args.Version(Interlocked.Increment(ref _serviceVersion));
        _args.Tag($"mfa-android-controller-{Guid.NewGuid():N}");
        Shizuku.BindUserService(_args, this);
    }

    public async void OnServiceConnected(ComponentName? name, IBinder? service)
    {
        if (service == null)
        {
            _stateChanged(false, -1, -1, "Shizuku UserService returned an empty binder.");
            return;
        }

        try
        {
            service = await PrepareServiceFallbackAsync(service);
            var (uid, port) = Handshake(service);
            _service = service;
            _stateChanged(true, uid, port, null);
        }
        catch (Exception ex)
        {
            _stateChanged(false, -1, -1, ex.Message);
        }
    }

    private async Task<IBinder> PrepareServiceFallbackAsync(IBinder initialService)
    {
        var uid = ReadServiceUid(initialService);
        if (uid != 0)
        {
            _usingRootService = false;
            if (uid == 2000)
                _shellService = initialService;
            return initialService;
        }

        _rootService = initialService;
        _usingRootService = true;
        var context = _context ?? throw new InvalidOperationException("Android context is unavailable.");
        var uri = global::Android.Net.Uri.Parse($"content://{context.PackageName}.mfa.shell.bootstrap");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var result = context.ContentResolver?.Call(uri!, "take", null, null);
            var shellService = result?.GetBinder("service");
            if (shellService?.IsBinderAlive == true && ReadServiceUid(shellService) == 2000)
            {
                _shellService = shellService;
                return initialService;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("The shell virtual-display helper did not attach within 5 seconds.");
    }

    private (int Uid, int Port) Handshake(IBinder service)
    {
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(global::Android.OS.Process.MyPid());
        data.WriteStrongBinder(_overlayInputStateBinder);
        service.Transact(HealthTransaction, data, reply, 0);
        var uid = reply.ReadInt();
        var port = reply.ReadInt();
        if (port is <= 0 or > 65535)
            throw new InvalidOperationException($"Shizuku UserService returned an invalid input port: {port}.");
        return (uid, port);
    }

    private static int ReadServiceUid(IBinder service)
    {
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        service.Transact(HealthTransaction, data, reply, 0);
        return reply.ReadInt();
    }

    public void OnServiceDisconnected(ComponentName? name)
    {
        _service = null;
        _usingRootService = false;
        _stateChanged(false, -1, -1, "Shizuku UserService disconnected.");
    }

    public int CreateVirtualDisplay(int width, int height, int dpi, Surface surface)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        var result = CreateVirtualDisplayCore(service, width, height, dpi, surface);
        if (result.DisplayId < 0 && _usingRootService
                                 && _shellService?.IsBinderAlive == true)
        {
            global::Android.Util.Log.Warn("MfaVirtualDisplay",
                $"Root display creation failed ({result.Error}); retrying with shell helper.");
            service = _shellService;
            var (uid, port) = Handshake(service);
            _service = service;
            _usingRootService = false;
            _stateChanged(true, uid, port, null);
            result = CreateVirtualDisplayCore(service, width, height, dpi, surface);
        }

        if (result.DisplayId < 0)
            throw new InvalidOperationException(
                $"Shizuku virtual display creation failed: {result.Error ?? "unknown error"} " +
                $"(last flags=0x{result.Flags:X}).");
        global::Android.Util.Log.Info("MfaVirtualDisplay",
            $"UserService created display {result.DisplayId} with flags 0x{result.Flags:X}.");
        return result.DisplayId;
    }

    private static (int DisplayId, string? Error, int Flags) CreateVirtualDisplayCore(
        IBinder service, int width, int height, int dpi, Surface surface)
    {
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(width);
        data.WriteInt(height);
        data.WriteInt(dpi);
        surface.WriteToParcel(data, ParcelableWriteFlags.None);
        service.Transact(CreateDisplayTransaction, data, reply, 0);
        var displayId = reply.ReadInt();
        var error = reply.ReadString();
        var flags = reply.ReadInt();
        return (displayId, error, flags);
    }

    public void ReleaseVirtualDisplay()
    {
        var service = _service;
        if (service == null)
            return;
        using var data = Parcel.Obtain();
        service.Transact(ReleaseDisplayTransaction, data, null, 0);
    }

    public void CreatePrimaryDisplayCapture(int displayId, int width, int height, Surface surface)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(displayId);
        data.WriteInt(width);
        data.WriteInt(height);
        surface.WriteToParcel(data, ParcelableWriteFlags.None);
        service.Transact(CreatePrimaryCaptureTransaction, data, reply, 0);
        var error = reply.ReadString();
        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Primary display capture failed: {error}");
    }

    public (int Width, int Height, int Rotation, int LayerStack) GetDisplayInfo(int displayId)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(displayId);
        service.Transact(GetDisplayInfoTransaction, data, reply, 0);
        var result = (reply.ReadInt(), reply.ReadInt(), reply.ReadInt(), reply.ReadInt());
        if (result.Item1 <= 0 || result.Item2 <= 0)
            throw new InvalidOperationException(
                $"Display {displayId} returned an invalid logical size: {result.Item1}x{result.Item2}.");
        return result;
    }

    public int ResolveCurrentScreenDisplayId(int fallbackDisplayId)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(fallbackDisplayId);
        service.Transact(ResolveCaptureDisplayTransaction, data, reply, 0);
        var displayId = reply.ReadInt();
        return displayId >= 0 ? displayId : fallbackDisplayId;
    }

    public (int DisplayId, string? PackageName) GetFocusedDisplayTarget(int fallbackDisplayId)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(fallbackDisplayId);
        service.Transact(GetFocusedDisplayTransaction, data, reply, 0);
        var displayId = reply.ReadInt();
        var packageName = reply.ReadString();
        return (displayId >= 0 ? displayId : fallbackDisplayId, packageName);
    }

    public void SetGameProcessKeepAlive(int displayId, bool enabled)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        data.WriteInt(displayId);
        data.WriteInt(enabled ? 1 : 0);
        service.Transact(SetGameKeepAliveTransaction, data, null, 0);
    }

    public int StartApp(int displayId, string target, bool forceStop)
    {
        var service = _service
            ?? throw new InvalidOperationException("Shizuku UserService is not connected.");
        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        data.WriteInt(displayId);
        data.WriteInt(forceStop ? 1 : 0);
        data.WriteString(target);
        service.Transact(StartAppTransaction, data, reply, 0);
        return reply.ReadInt();
    }

    public void Unbind()
    {
        if (_args == null) return;
        ReleaseVirtualDisplay();
        _service = null;
        _rootService = null;
        _shellService = null;
        _usingRootService = false;
        Shizuku.UnbindUserService(_args, this, true);
        _args.Dispose();
        _args = null;
    }

    private sealed class OverlayInputStateBinder : Binder
    {
        protected override bool OnTransact(int code, Parcel data, Parcel? reply, int flags)
        {
            if (code != OverlayInputStateTransaction)
                return base.OnTransact(code, data, reply, flags);

            var applied = AndroidCurrentScreenOverlay.SetScriptInputActive(data.ReadInt() != 0);
            reply?.WriteInt(applied ? 1 : 0);
            return true;
        }
    }
}
