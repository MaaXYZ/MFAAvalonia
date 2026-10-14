using Android.App;
using Android.Content;
using Android.OS;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper.ValueType;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MFAAvalonia.Android;

/// <summary>
/// Mirrors MFA's existing timer configuration into wake-up alarms. Each timer owns
/// one PendingIntent and schedules its next occurrence only.
/// </summary>
internal static class AndroidScheduleManager
{
    internal const string TriggerActionSuffix = ".SCHEDULE_TRIGGER";
    internal const string ExtraTimerId = "mfa.schedule.timer_id";
    internal const string ExtraScheduledAt = "mfa.schedule.scheduled_at";

    private const string PreferenceName = "mfa_android_schedule";
    private const string ScheduledCountKey = "scheduled_timer_count";
    private const string PendingPrefix = "pending_trigger_";
    private const string DeliveredPrefix = "delivered_trigger_";
    private const int AlarmRequestCodeBase = 18000;
    private const int ShowRequestCode = 17999;

    public static void RescheduleAll(Context context)
    {
        context = context.ApplicationContext ?? context;
        var preferences = Preferences(context);
        var previousCount = preferences.GetInt(ScheduledCountKey, 0);
        var count = Math.Max(0, GlobalConfiguration.GetTimerCount(8));

        for (var timerId = 0; timerId < Math.Max(previousCount, count); timerId++)
            Cancel(context, timerId);

        for (var timerId = 0; timerId < count; timerId++)
            ScheduleNext(context, timerId);

        preferences.Edit()?.PutInt(ScheduledCountKey, count)?.Apply();
        global::Android.Util.Log.Info("MfaSchedule", $"Rescheduled {count} timer slots.");
    }

    public static void ScheduleNext(Context context, int timerId, long afterUnixMilliseconds = 0)
    {
        context = context.ApplicationContext ?? context;
        var rule = LoadRule(timerId);
        Cancel(context, timerId);
        if (rule is not { Enabled: true })
            return;

        var next = ComputeNextTrigger(rule, afterUnixMilliseconds);
        if (next == null)
        {
            global::Android.Util.Log.Info("MfaSchedule",
                $"Timer {timerId} has no valid next trigger.");
            return;
        }

        var triggerAt = next.Value.ToUnixTimeMilliseconds();
        var pendingIntent = BuildAlarmIntent(context, timerId, triggerAt);
        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        if (alarmManager == null || pendingIntent == null)
            return;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.S && !alarmManager.CanScheduleExactAlarms())
        {
            // The same fallback used by MaaFwApp: setAlarmClock remains exact, exits Doze,
            // and permits the receiver to start the short execution foreground service.
            alarmManager.SetAlarmClock(
                new AlarmManager.AlarmClockInfo(triggerAt, BuildShowIntent(context)),
                pendingIntent);
        }
        else
        {
            alarmManager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, triggerAt, pendingIntent);
        }

        global::Android.Util.Log.Info("MfaSchedule",
            $"Timer {timerId} next trigger: {next.Value:O}.");
    }

    public static void Cancel(Context context, int timerId)
    {
        var alarmManager = (AlarmManager?)context.GetSystemService(Context.AlarmService);
        var pendingIntent = BuildAlarmIntent(context, timerId, 0);
        if (alarmManager == null || pendingIntent == null)
            return;

        alarmManager.Cancel(pendingIntent);
        pendingIntent.Cancel();
    }

    public static void StorePendingTrigger(Context context, int timerId, long scheduledAt)
    {
        Preferences(context).Edit()?.PutLong(PendingPrefix + timerId, scheduledAt)?.Commit();
    }

    public static void ClearPendingTrigger(Context context, int timerId)
    {
        Preferences(context).Edit()?.Remove(PendingPrefix + timerId)?.Apply();
    }

    public static bool TryMarkDelivered(Context context, int timerId, long scheduledAt)
    {
        var preferences = Preferences(context);
        var key = DeliveredPrefix + timerId;
        if (preferences.GetLong(key, 0) == scheduledAt)
            return false;

        return preferences.Edit()?.PutLong(key, scheduledAt)?.Commit() == true;
    }

    public static IReadOnlyList<(int TimerId, long ScheduledAt)> ConsumePendingTriggers(Context context)
    {
        var preferences = Preferences(context);
        var result = new List<(int TimerId, long ScheduledAt)>();
        var keys = preferences.All.Keys
            .Where(key => key.StartsWith(PendingPrefix, StringComparison.Ordinal))
            .ToList();
        var editor = preferences.Edit();

        foreach (var key in keys)
        {
            if (!int.TryParse(key.AsSpan(PendingPrefix.Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var timerId))
                continue;

            var scheduledAt = preferences.GetLong(key, 0);
            if (scheduledAt > 0)
                result.Add((timerId, scheduledAt));
            editor?.Remove(key);
        }

        editor?.Commit();
        return result.OrderBy(item => item.ScheduledAt).ToList();
    }

    private static PendingIntent? BuildAlarmIntent(Context context, int timerId, long scheduledAt)
    {
        var intent = new Intent(context, typeof(AndroidScheduleReceiver));
        intent.SetAction(context.PackageName + TriggerActionSuffix);
        intent.PutExtra(ExtraTimerId, timerId);
        intent.PutExtra(ExtraScheduledAt, scheduledAt);
        return PendingIntent.GetBroadcast(context, AlarmRequestCodeBase + timerId, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
    }

    private static PendingIntent BuildShowIntent(Context context)
    {
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty)
                     ?? new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        return PendingIntent.GetActivity(context, ShowRequestCode, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    private static ISharedPreferences Preferences(Context context) =>
        context.GetSharedPreferences(PreferenceName, FileCreationMode.Private)!;

    private static TimerRule? LoadRule(int timerId)
    {
        var enabled = string.Equals(
            GlobalConfiguration.GetTimer(timerId, bool.FalseString),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);
        var rawTime = GlobalConfiguration.GetTimerTime(timerId, $"{timerId * 3 % 24}:0");
        if (!TimeSpan.TryParse(rawTime, CultureInfo.InvariantCulture, out var time))
            return null;

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            return null;

        var schedule = new TimerScheduleConfig(
            GlobalConfiguration.GetTimerSchedule(timerId, string.Empty));
        return new TimerRule(enabled, time, schedule);
    }

    private static DateTimeOffset? ComputeNextTrigger(TimerRule rule, long afterUnixMilliseconds)
    {
        var now = DateTime.Now;
        var baseline = now;
        if (afterUnixMilliseconds > 0)
        {
            var previous = DateTimeOffset.FromUnixTimeMilliseconds(afterUnixMilliseconds).LocalDateTime;
            if (previous > baseline)
                baseline = previous;
        }

        // A 400-day scan covers daily, weekly and every valid monthly date, including
        // February/leap-year transitions and rules such as the 31st of a month.
        for (var dayOffset = 0; dayOffset <= 400; dayOffset++)
        {
            var candidate = baseline.Date.AddDays(dayOffset).Add(rule.Time);
            if (candidate <= baseline || !rule.Schedule.ShouldTrigger(candidate))
                continue;

            return new DateTimeOffset(candidate);
        }

        return null;
    }

    private sealed record TimerRule(bool Enabled, TimeSpan Time, TimerScheduleConfig Schedule);
}
