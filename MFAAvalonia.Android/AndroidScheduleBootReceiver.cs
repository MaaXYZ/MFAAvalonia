using Android.App;
using Android.Content;

namespace MFAAvalonia.Android;

[BroadcastReceiver(Name = "com.fox.mfa.AndroidScheduleBootReceiver", Enabled = true, Exported = false)]
public sealed class AndroidScheduleBootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || (intent?.Action != Intent.ActionBootCompleted
                                && intent?.Action != Intent.ActionMyPackageReplaced))
            return;

        try
        {
            AndroidScheduleManager.RescheduleAll(context);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error("MfaSchedule",
                $"Unable to restore alarms after {intent.Action}: {ex}");
        }
    }
}
