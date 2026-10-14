using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Net;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using Java.Lang;
using System;
using System.IO;
using JavaFile = Java.IO.File;
using JavaIOException = Java.IO.IOException;

namespace MFAAvalonia.Android;

/// <summary>
/// Guides the user through the system installer when a verified Shizuku APK was embedded at build time,
/// or opens the official Shizuku download page when the shell does not contain one.
/// </summary>
internal sealed class ShizukuInstallationPrompt(Activity activity) : IDisposable
{
    internal const string OfficialPackageName = "moe.shizuku.privileged.api";
    internal const string OfficialDownloadUrl = "https://shizuku.rikka.app/download/";
    private const string BundledAssetPath = "MfaDependencies/shizuku.apk";

    private Dialog? _dialog;
    private Button? _installButton;
    private TextView? _laterButton;
    private bool _checkedThisActivity;
    private bool _disposed;

    public void ShowIfNotInstalled()
    {
        if (_disposed || _checkedThisActivity || activity.IsFinishing || activity.IsDestroyed)
            return;

        _checkedThisActivity = true;
        if (IsPackageInstalled())
            return;

        _dialog = new Dialog(activity);
        _dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        _dialog.SetContentView(Resource.Layout.dialog_shizuku_install);
        _dialog.SetCanceledOnTouchOutside(false);
        _dialog.SetCancelable(true);
        _dialog.DismissEvent += OnDialogDismissed;

        _installButton = _dialog.FindViewById<Button>(Resource.Id.shizuku_install_button)
            ?? throw new InvalidOperationException("The Shizuku install button is missing from the dialog layout.");
        _laterButton = _dialog.FindViewById<TextView>(Resource.Id.shizuku_later_button)
            ?? throw new InvalidOperationException("The Shizuku later button is missing from the dialog layout.");
        _installButton.SetText(HasBundledApk()
            ? Resource.String.shizuku_install_bundled
            : Resource.String.shizuku_open_official_download);
        _installButton.Click += OnInstallClicked;
        _laterButton.Click += OnLaterClicked;

        _dialog.Show();
        ConfigureDialogWindow(_dialog.Window);
    }

    private void ConfigureDialogWindow(Window? window)
    {
        if (window == null)
            return;

        window.SetBackgroundDrawable(new ColorDrawable(global::Android.Graphics.Color.Transparent));
        window.AddFlags(WindowManagerFlags.DimBehind);
        var attributes = window.Attributes;
        if (attributes == null)
            return;

        var horizontalMargin = Dp(32);
        var maximumWidth = Dp(520);
        var screenWidth = activity.Resources?.DisplayMetrics?.WidthPixels ?? maximumWidth;
        attributes.Width = System.Math.Min(screenWidth - horizontalMargin, maximumWidth);
        attributes.Height = ViewGroup.LayoutParams.WrapContent;
        attributes.DimAmount = 0.58f;
        window.Attributes = attributes;
    }

    private int Dp(int value) =>
        (int)System.Math.Round(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f));

    private void OnInstallClicked(object? sender, EventArgs e)
    {
        InstallBundledApkOrOpenOfficialPage();
        _dialog?.Dismiss();
    }

    private void OnLaterClicked(object? sender, EventArgs e) => _dialog?.Dismiss();

    private bool HasBundledApk()
    {
        try
        {
            using var stream = activity.Assets?.Open(BundledAssetPath);
            return stream != null;
        }
        catch (JavaIOException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void InstallBundledApkOrOpenOfficialPage()
    {
        try
        {
            var assetManager = activity.Assets;
            if (assetManager == null)
            {
                OpenOfficialDownloadPage();
                return;
            }

            using var input = assetManager.Open(BundledAssetPath);
            var dependencyDirectory = new JavaFile(activity.CacheDir, "dependencies");
            Directory.CreateDirectory(dependencyDirectory.AbsolutePath);
            var apkFile = new JavaFile(dependencyDirectory, "shizuku.apk");
            using (var output = System.IO.File.Create(apkFile.AbsolutePath))
                input.CopyTo(output);

            var authority = $"{activity.PackageName}.fileprovider";
            var contentUri = FileProvider.GetUriForFile(activity, authority, apkFile);
            var intent = new Intent(Intent.ActionView);
            intent.SetDataAndType(contentUri, "application/vnd.android.package-archive");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (JavaIOException)
        {
            OpenOfficialDownloadPage();
        }
        catch (IOException)
        {
            OpenOfficialDownloadPage();
        }
        catch (ActivityNotFoundException ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"No Android package installer is available: {ex}");
            OpenOfficialDownloadPage();
        }
        catch (SecurityException ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"Unable to launch the Shizuku installer: {ex}");
            OpenOfficialDownloadPage();
        }
    }

    private bool IsPackageInstalled()
    {
        try
        {
#pragma warning disable CA1422 // The legacy overload remains available on every supported Android API level.
            var packageInfo = activity.PackageManager?.GetPackageInfo(OfficialPackageName, PackageInfoFlags.MatchAll);
#pragma warning restore CA1422
            return packageInfo?.ApplicationInfo?.Enabled == true;
        }
        catch (PackageManager.NameNotFoundException)
        {
            return false;
        }
        catch (SecurityException ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"Unable to inspect the Shizuku package: {ex}");
            return false;
        }
    }

    private void OpenOfficialDownloadPage()
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(OfficialDownloadUrl));
            intent.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (ActivityNotFoundException ex)
        {
            global::Android.Util.Log.Warn("MFAAvalonia", $"No browser can open the Shizuku download page: {ex}");
            Toast.MakeText(
                    activity,
                    $"Shizuku: {OfficialDownloadUrl}",
                    ToastLength.Long)
                ?.Show();
        }
    }

    private void OnDialogDismissed(object? sender, EventArgs e)
    {
        if (_installButton != null)
            _installButton.Click -= OnInstallClicked;
        if (_laterButton != null)
            _laterButton.Click -= OnLaterClicked;
        _installButton = null;
        _laterButton = null;
        if (_dialog != null)
            _dialog.DismissEvent -= OnDialogDismissed;
        _dialog?.Dispose();
        _dialog = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_dialog != null)
        {
            if (_installButton != null)
                _installButton.Click -= OnInstallClicked;
            if (_laterButton != null)
                _laterButton.Click -= OnLaterClicked;
            _installButton = null;
            _laterButton = null;
            _dialog.DismissEvent -= OnDialogDismissed;
            if (_dialog.IsShowing)
                _dialog.Dismiss();
            _dialog.Dispose();
            _dialog = null;
        }
    }
}
