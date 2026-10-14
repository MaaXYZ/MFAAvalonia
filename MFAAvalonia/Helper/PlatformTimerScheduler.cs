using System;

namespace MFAAvalonia.Helper;

/// <summary>
/// Connects the shared timer model to a platform scheduler which can wake the app.
/// Desktop keeps using the in-process dispatcher timer; Android installs both callbacks.
/// </summary>
public static class PlatformTimerScheduler
{
    public static Action? RescheduleAll { get; set; }

    public static Action<int, long>? Trigger { get; set; }

    public static void RequestReschedule()
    {
        try
        {
            RescheduleAll?.Invoke();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("Platform timer rescheduling failed.", ex);
        }
    }

    public static bool TryTrigger(int timerId, long scheduledAtUnixMilliseconds)
    {
        var trigger = Trigger;
        if (trigger == null)
            return false;

        trigger(timerId, scheduledAtUnixMilliseconds);
        return true;
    }
}
