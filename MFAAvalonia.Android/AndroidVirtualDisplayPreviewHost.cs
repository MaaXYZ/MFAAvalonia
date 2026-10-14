using Android.App;
using Android.Graphics;
using Android.Views;
using Avalonia.Android;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MFAAvalonia.Android;

internal sealed class AndroidVirtualDisplayPreviewHost : NativeControlHost
{
    private readonly Activity _activity;
    private readonly AndroidVirtualDisplayBackend _backend;
    private SurfaceCallback? _callback;
    private SurfaceView? _surfaceView;

    internal AndroidVirtualDisplayPreviewHost(Activity activity, AndroidVirtualDisplayBackend backend)
    {
        _activity = activity;
        _backend = backend;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _surfaceView = new SurfaceView(_activity);
        _surfaceView.Holder?.SetFormat(Format.Rgba8888);
        _surfaceView.SetZOrderOnTop(false);
        _surfaceView.SetZOrderMediaOverlay(false);
        _callback = new SurfaceCallback(_backend);
        _surfaceView.Holder?.AddCallback(_callback);
        return new AndroidViewControlHandle(_surfaceView);
    }

    // Avalonia.Android 11.3.x does not special-case NativeControlHost's
    // InteropAutomationPeer. Android accessibility therefore walks into that peer and
    // calls methods which intentionally throw NotImplementedException. This can happen
    // when the preview is first shown or when navigating away from the task page.
    // Keep the SurfaceView out of Avalonia's virtual accessibility tree; Android owns
    // the native view and the preview itself has no interactive accessibility content.
    protected override AutomationPeer OnCreateAutomationPeer() => new PreviewHostAutomationPeer(this);

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        NativeCaptureInterop.SetPreviewSurface(null);
        if (_surfaceView?.Holder != null && _callback != null)
            _surfaceView.Holder.RemoveCallback(_callback);
        _callback?.Dispose();
        _callback = null;
        _surfaceView = null;
        base.DestroyNativeControlCore(control);
    }

    private sealed class SurfaceCallback(AndroidVirtualDisplayBackend backend) : Java.Lang.Object,
        ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder)
        {
            if (backend.Width > 0 && backend.Height > 0)
                holder.SetFixedSize(backend.Width, backend.Height);
            NativeCaptureInterop.SetPreviewSurface(holder.Surface);
        }

        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) =>
            NativeCaptureInterop.SetPreviewSurface(holder.Surface);

        public void SurfaceDestroyed(ISurfaceHolder holder) =>
            NativeCaptureInterop.SetPreviewSurface(null);
    }

    private sealed class PreviewHostAutomationPeer(AndroidVirtualDisplayPreviewHost owner)
        : ControlAutomationPeer(owner)
    {
        protected override System.Collections.Generic.IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;

        protected override bool IsContentElementCore() => false;

        protected override bool IsControlElementCore() => false;
    }
}
