using Android.App;
using Android.Content;
using AndroidX.Core.Content;
using System;

namespace MFAAvalonia.Android;

[BroadcastReceiver(Name = "com.fox.mfa.AndroidScheduleReceiver", Enabled = true, Exported = false)]
public sealed class AndroidScheduleReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent?.Action != context.PackageName + AndroidScheduleManager.TriggerActionSuffix)
            return;

        var timerId = intent.GetIntExtra(AndroidScheduleManager.ExtraTimerId, -1);
        var scheduledAt = intent.GetLongExtra(AndroidScheduleManager.ExtraScheduledAt, 0);
        if (timerId < 0 || scheduledAt <= 0)
            return;

        var serviceIntent = new Intent(context, typeof(AndroidScheduleExecutionService));
        serviceIntent.SetAction(intent.Action);
        serviceIntent.PutExtra(AndroidScheduleManager.ExtraTimerId, timerId);
        serviceIntent.PutExtra(AndroidScheduleManager.ExtraScheduledAt, scheduledAt);
        try
        {
            ContextCompat.StartForegroundService(context, serviceIntent);
        }
        catch (Exception ex)
        {
            // Never let one failed delivery break the alarm chain permanently.
            global::Android.Util.Log.Error("MfaSchedule",
                $"Unable to start schedule execution service: {ex}");
            AndroidScheduleManager.ScheduleNext(context, timerId, scheduledAt);
        }
    }
}
