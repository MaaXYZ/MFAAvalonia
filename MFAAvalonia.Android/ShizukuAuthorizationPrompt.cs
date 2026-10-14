using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using System;

namespace MFAAvalonia.Android;

internal sealed class ShizukuAuthorizationPrompt : IDisposable
{
    private readonly Activity _activity;
    private readonly ShizukuConnectionManager _shizuku;
    private Dialog? _dialog;
    private Button? _actionButton;
    private TextView? _laterButton;
    private bool _disposed;

    public ShizukuAuthorizationPrompt(Activity activity, ShizukuConnectionManager shizuku)
    {
        _activity = activity;
        _shizuku = shizuku;
        _shizuku.StateChanged += OnStateChanged;
    }

    public void ShowIfNeeded() => _activity.RunOnUiThread(ShowIfNeededOnUiThread);

    private void ShowIfNeededOnUiThread()
    {
        if (_disposed || _activity.IsFinishing || _activity.IsDestroyed || !IsInstalled())
            return;
        if (_shizuku.IsUserServiceReady)
        {
            _dialog?.Dismiss();
            return;
        }
        if (_dialog?.IsShowing == true)
        {
            UpdateContent();
            return;
        }

        _dialog = new Dialog(_activity);
        _dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        _dialog.SetContentView(Resource.Layout.dialog_shizuku_install);
        _dialog.SetCanceledOnTouchOutside(false);
        _dialog.DismissEvent += OnDismissed;
        _actionButton = _dialog.FindViewById<Button>(Resource.Id.shizuku_install_button)
            ?? throw new InvalidOperationException("Shizuku action button is missing.");
        _laterButton = _dialog.FindViewById<TextView>(Resource.Id.shizuku_later_button)
            ?? throw new InvalidOperationException("Shizuku later button is missing.");
        _actionButton.Click += OnActionClicked;
        _laterButton.Click += OnLaterClicked;
        UpdateContent();
        _dialog.Show();
        ConfigureWindow(_dialog.Window);
    }

    private void UpdateContent()
    {
        if (_dialog == null || _actionButton == null)
            return;
        var title = _dialog.FindViewById<TextView>(Resource.Id.shizuku_dialog_title);
        var message = _dialog.FindViewById<TextView>(Resource.Id.shizuku_dialog_message);
        if (!_shizuku.IsServiceAvailable)
        {
            title?.SetText(Resource.String.shizuku_not_running_title);
            message?.SetText(Resource.String.shizuku_not_running_message);
            _actionButton.SetText(Resource.String.shizuku_open_app);
            _actionButton.Enabled = true;
        }
        else if (!_shizuku.HasPermission)
        {
            title?.SetText(Resource.String.shizuku_not_authorized_title);
            message?.SetText(Resource.String.shizuku_not_authorized_message);
            _actionButton.SetText(_shizuku.IsPermissionRequestPending
                ? Resource.String.shizuku_authorizing
                : Resource.String.shizuku_authorize_now);
            _actionButton.Enabled = !_shizuku.IsPermissionRequestPending;
        }
        else
        {
            title?.SetText(Resource.String.shizuku_service_connecting_title);
            message?.SetText(Resource.String.shizuku_service_connecting_message);
            _actionButton.SetText(Resource.String.shizuku_retry_connection);
            _actionButton.Enabled = true;
        }
    }

    private void OnActionClicked(object? sender, EventArgs e)
    {
        if (!_shizuku.IsServiceAvailable)
        {
            try
            {
                var intent = _activity.PackageManager?.GetLaunchIntentForPackage(ShizukuInstallationPrompt.OfficialPackageName);
                if (intent != null)
                    _activity.StartActivity(intent);
            }
            catch (Exception ex) { global::Android.Util.Log.Warn("MFAAvalonia", $"Unable to open Shizuku: {ex}"); }
            _dialog?.Dismiss();
        }
        else if (!_shizuku.HasPermission)
        {
            _shizuku.RequestPermission();
        }
        else
        {
            _shizuku.RetryUserService();
            UpdateContent();
        }
    }

    private void OnLaterClicked(object? sender, EventArgs e) => _dialog?.Dismiss();

    private bool IsInstalled()
    {
        try
        {
#pragma warning disable CA1422
            return _activity.PackageManager?.GetPackageInfo(ShizukuInstallationPrompt.OfficialPackageName, PackageInfoFlags.MatchAll)?.ApplicationInfo?.Enabled == true;
#pragma warning restore CA1422
        }
        catch (PackageManager.NameNotFoundException) { return false; }
        catch (Java.Lang.SecurityException) { return false; }
    }

    private void ConfigureWindow(Window? window)
    {
        if (window == null) return;
        window.SetBackgroundDrawable(new ColorDrawable(global::Android.Graphics.Color.Transparent));
        window.AddFlags(WindowManagerFlags.DimBehind);
        var a = window.Attributes;
        if (a == null) return;
        var density = _activity.Resources?.DisplayMetrics?.Density ?? 1f;
        var margin = (int)Math.Round(32 * density);
        var max = (int)Math.Round(520 * density);
        var width = _activity.Resources?.DisplayMetrics?.WidthPixels ?? max;
        a.Width = Math.Min(width - margin, max);
        a.Height = ViewGroup.LayoutParams.WrapContent;
        a.DimAmount = .58f;
        window.Attributes = a;
    }

    private void OnStateChanged()
    {
        if (_disposed) return;
        _activity.RunOnUiThread(() =>
        {
            if (_shizuku.IsUserServiceReady) _dialog?.Dismiss();
            else if (_dialog?.IsShowing == true) UpdateContent();
        });
    }

    private void OnDismissed(object? sender, EventArgs e)
    {
        if (_actionButton != null) _actionButton.Click -= OnActionClicked;
        if (_laterButton != null) _laterButton.Click -= OnLaterClicked;
        if (_dialog != null) _dialog.DismissEvent -= OnDismissed;
        _actionButton = null;
        _laterButton = null;
        _dialog = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shizuku.StateChanged -= OnStateChanged;
        _dialog?.Dismiss();
    }
}
