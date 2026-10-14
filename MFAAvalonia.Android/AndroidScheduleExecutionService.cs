using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using MFAAvalonia.Helper;
using System;
using System.Threading;

namespace MFAAvalonia.Android;

[Service(Name = "com.fox.mfa.AndroidScheduleExecutionService", Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class AndroidScheduleExecutionService : Service
{
    private const string ChannelId = "mfa_schedule_execution";
    private const int NotificationId = 1002;
    private const int HandoffTimeoutMilliseconds = 15_000;
    private int _inFlight;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        EnsureChannel();
        StartForegroundCompat(BuildNotification());

        var timerId = intent?.GetIntExtra(AndroidScheduleManager.ExtraTimerId, -1) ?? -1;
        var scheduledAt = intent?.GetLongExtra(AndroidScheduleManager.ExtraScheduledAt, 0) ?? 0;
        if (intent?.Action != PackageName + AndroidScheduleManager.TriggerActionSuffix
            || timerId < 0 || scheduledAt <= 0)
        {
            FinishImmediately();
            return StartCommandResult.NotSticky;
        }

        Interlocked.Increment(ref _inFlight);
        try
        {
            // Re-arm before dispatching. Even if app startup fails, the recurring rule survives.
            AndroidScheduleManager.ScheduleNext(this, timerId, scheduledAt);
            AndroidScheduleManager.StorePendingTrigger(this, timerId, scheduledAt);

            if (PlatformTimerScheduler.TryTrigger(timerId, scheduledAt))
            {
                AndroidScheduleManager.ClearPendingTrigger(this, timerId);
            }
            else
            {
                LaunchMfa(timerId, scheduledAt);
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("MfaSchedule",
                $"Scheduled timer {timerId} dispatch failed: {ex}");
        }
        new Handler(Looper.MainLooper!).PostDelayed(FinishHandoff, HandoffTimeoutMilliseconds);

        return StartCommandResult.NotSticky;
    }

    private void LaunchMfa(int timerId, long scheduledAt)
    {
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty)
                     ?? new Intent(this, typeof(MainActivity));
        launch.SetAction(PackageName + ".SCHEDULE_EXECUTE");
        launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        launch.PutExtra(AndroidScheduleManager.ExtraTimerId, timerId);
        launch.PutExtra(AndroidScheduleManager.ExtraScheduledAt, scheduledAt);
        StartActivity(launch);
    }

    private void FinishHandoff()
    {
        if (Interlocked.Decrement(ref _inFlight) > 0)
            return;

        FinishImmediately();
    }

    private void FinishImmediately()
    {
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId, "MFA scheduled tasks", NotificationImportance.Low)
        {
            Description = "Wakes MFA when an Android scheduled task is due."
        });
    }

    private Notification BuildNotification()
    {
        var launch = PackageManager?.GetLaunchIntentForPackage(PackageName ?? string.Empty)
                     ?? new Intent(this, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        var contentIntent = PendingIntent.GetActivity(this, 0, launch,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentTitle("MFA")
            .SetContentText("Scheduled task is starting")
            .SetContentIntent(contentIntent)
            .SetOngoing(true)
            .SetSilent(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }

    private void StartForegroundCompat(Notification notification)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }
}
