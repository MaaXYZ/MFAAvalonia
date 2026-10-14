using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using AndroidX.Core.Graphics.Drawable;
using MFAAvalonia.Helper;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace MFAAvalonia.Android;

[Service(Name = "com.fox.mfa.AndroidRunForegroundService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class AndroidRunForegroundService : Service
{
    private const string ChannelId = "mfa_run_execution";
    private const int NotificationId = 1001;
    private const int ProgressMax = 100;
    private const string CurrentScreenExtra = "mfa.current_screen";
    private static readonly object SnapshotLock = new();
    private static readonly Dictionary<string, RunProgressSnapshot> Snapshots = new();
    private Timer? _updateTimer;
    private Timer? _overlayTimer;
    private Handler? _mainHandler;
    private long _lastUpdateTicks;
    private int _overlayRefreshActive;
    private bool _destroyed;

    public static void Publish(Context context, RunProgressSnapshot snapshot)
    {
        lock (SnapshotLock)
            Snapshots[snapshot.InstanceId] = snapshot;
        var intent = new Intent(context, typeof(AndroidRunForegroundService));
        intent.PutExtra(CurrentScreenExtra, MobileRunConfiguration.Mode == MobileRunMode.CurrentScreen);
        ContextCompat.StartForegroundService(context, intent);
    }

    public static void Finish(Context context, string instanceId)
    {
        lock (SnapshotLock)
        {
            Snapshots.Remove(instanceId);
            if (Snapshots.Count > 0)
                return;
        }
        context.StopService(new Intent(context, typeof(AndroidRunForegroundService)));
        if (MobileRunConfiguration.Mode == MobileRunMode.VirtualDisplay)
            MobileRunConfiguration.StopBackgroundGameKeepAlive?.Invoke();
        AndroidCurrentScreenOverlay.Hide();
    }

    public override void OnCreate()
    {
        base.OnCreate();
        _destroyed = false;
        EnsureChannel();
        var snapshot = GetSnapshot();
        StartForegroundCompat(BuildNotification(snapshot));
        _lastUpdateTicks = System.Environment.TickCount64;
        _mainHandler = new Handler(Looper.MainLooper!);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var snapshot = GetSnapshot();
        StartForegroundCompat(BuildNotification(snapshot));
        var currentScreen = intent?.GetBooleanExtra(CurrentScreenExtra, false) == true;
        global::Android.Util.Log.Info("MfaCurrentScreenOverlay",
            $"Foreground service update: currentScreen={currentScreen}, overlayPermission=" +
            Settings.CanDrawOverlays(this));
        if (currentScreen)
        {
            RefreshOverlay(snapshot);
            _overlayTimer ??= new Timer(_ => RefreshOverlay(), null, 400, 400);
        }
        else
        {
            _overlayTimer?.Dispose();
            _overlayTimer = null;
            AndroidCurrentScreenOverlay.Hide();
        }
        ScheduleThrottledUpdate();
        return StartCommandResult.NotSticky;
    }

    public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        _destroyed = true;
        _updateTimer?.Dispose();
        _updateTimer = null;
        _overlayTimer?.Dispose();
        _overlayTimer = null;
        _mainHandler?.Dispose();
        _mainHandler = null;
        AndroidCurrentScreenOverlay.Hide();
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private void RefreshOverlay(RunProgressSnapshot? snapshot = null)
    {
        if (_destroyed || Interlocked.Exchange(ref _overlayRefreshActive, 1) != 0)
            return;
        try
        {
            var resolver = MobileRunConfiguration.ResolveFocusedDisplay;
            var target = resolver?.Invoke()
                         ?? new MobileDisplayTarget(MobileRunConfiguration.ActiveDisplayId, null);
            var focusedOnMfa = string.Equals(target.PackageName, PackageName,
                StringComparison.OrdinalIgnoreCase);
            var gameDisplayId = MobileRunConfiguration.ActiveDisplayId;
            var mfaDisplayId = MobileRunConfiguration.MfaDisplayId;
            var usesSeparateGameDisplay = gameDisplayId >= 0
                                          && mfaDisplayId >= 0
                                          && gameDisplayId != mfaDisplayId;

            // MuMu renders application tabs on separate Android Displays, but changing the
            // visible host tab does not reliably update mTopFocusedDisplayId. Keep the overlay
            // attached to the game Display while Android still reports MFA as focused; it is
            // naturally invisible on MFA's different Display and is already waiting when the
            // user switches back to the game tab. On a phone both apps share one Display, so
            // the package check still hides the overlay while MFA itself is in front.
            var overlayDisplayId = focusedOnMfa && usesSeparateGameDisplay
                ? gameDisplayId
                : target.DisplayId;
            var hideOnMfa = focusedOnMfa && !usesSeparateGameDisplay;
            var latest = snapshot ?? GetSnapshot();
            _mainHandler?.Post(() =>
            {
                if (!_destroyed)
                    AndroidCurrentScreenOverlay.Update(
                        this, latest, overlayDisplayId, hideOnMfa, target.PackageName);
            });
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("MfaCurrentScreenOverlay",
                $"Unable to refresh focused display: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _overlayRefreshActive, 0);
        }
    }

    private void ScheduleThrottledUpdate()
    {
        var elapsed = System.Environment.TickCount64 - Interlocked.Read(ref _lastUpdateTicks);
        if (elapsed >= 1000)
        {
            PostLatest();
            return;
        }
        _updateTimer?.Dispose();
        _updateTimer = new Timer(_ => PostLatest(), null, Math.Max(1, 1000 - elapsed), Timeout.Infinite);
    }

    private void PostLatest()
    {
        lock (SnapshotLock)
        {
            if (Snapshots.Count == 0)
                return;
        }
        Interlocked.Exchange(ref _lastUpdateTicks, System.Environment.TickCount64);
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, BuildNotification(GetSnapshot()));
    }

    private static RunProgressSnapshot GetSnapshot()
    {
        lock (SnapshotLock)
        {
            if (Snapshots.Count == 0)
                return new RunProgressSnapshot(string.Empty, "MFA", "正在启动", string.Empty, 0, 0, true);
            var values = Snapshots.Values.ToList();
            if (values.Count == 1)
                return values[0];
            return new RunProgressSnapshot(
                "multiple", values[0].AppName, "多个实例正在执行",
                values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value.CurrentTask)).CurrentTask,
                values.Sum(value => value.Completed), values.Sum(value => value.Total),
                values.Any(value => value.Indeterminate));
        }
    }

    private Notification BuildNotification(RunProgressSnapshot snapshot)
    {
        var total = Math.Max(0, snapshot.Total);
        var completed = Math.Clamp(snapshot.Completed, 0, total);
        var indeterminate = snapshot.Indeterminate || total == 0;
        var progress = indeterminate ? 0 : (int)Math.Round(completed * 100d / total);
        var progressText = total > 0 ? $"{completed}/{total}" : string.Empty;
        var content = string.Join(" · ", new[] { progressText, snapshot.CurrentTask, snapshot.State }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        var style = new NotificationCompat.ProgressStyle()
            .SetStyledByProgress(true)
            .SetProgressIndeterminate(indeterminate)
            .SetProgressTrackerIcon(IconCompat.CreateWithResource(this, global::Android.Resource.Drawable.IcMediaPlay));
        style.AddProgressSegment(new NotificationCompat.ProgressStyle.Segment(ProgressMax).SetColor(unchecked((int)0xff168bd2)));
        if (!indeterminate)
            style.SetProgress(progress);

        var notification = new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(string.IsNullOrWhiteSpace(snapshot.State) ? "正在执行" : snapshot.State)
            .SetContentText(content)
            .SetStyle(style)
            .SetProgress(ProgressMax, progress, indeterminate)
            .SetContentIntent(ContentIntent())
            .SetOngoing(true)
            .SetSilent(true)
            .SetOnlyAlertOnce(true)
            .SetCategory(NotificationCompat.CategoryProgress)
            .SetRequestPromotedOngoing(CanPostPromotedNotifications());
        if (!string.IsNullOrWhiteSpace(progressText))
            notification.SetShortCriticalText(progressText);
        return notification.Build();
    }

    private bool CanPostPromotedNotifications()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Baklava)
            return false;
        try
        {
            return ((NotificationManager)GetSystemService(NotificationService)!).CanPostPromotedNotifications();
        }
        catch
        {
            return false;
        }
    }

    private void StartForegroundCompat(Notification notification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private PendingIntent ContentIntent()
    {
        var intent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty)
                     ?? new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        var channel = new NotificationChannel(ChannelId, "任务运行状态", NotificationImportance.Low)
        {
            Description = "显示当前任务和执行进度"
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }
}
