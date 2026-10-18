using System;

namespace MFAAvalonia.Helper;

/// <summary>检测任务回调静默、长时间无点击和重复点击循环。</summary>
public sealed class TaskRecoveryMonitor
{
    /// <summary>用于区分模拟器无响应的回调静默原因。</summary>
    public const string EmulatorUnresponsiveReasonPrefix = "模拟器无响应";

    private readonly object _gate = new();
    private readonly LoopDetector _loopDetector = new();
    private TimeSpan _lastCallback;
    private TimeSpan _lastClick;
    private bool _active;
    private string? _loopReason;

    public void Start(TimeSpan now)
    {
        lock (_gate)
        {
            _active = true;
            _lastCallback = now;
            _lastClick = now;
            _loopReason = null;
            _loopDetector.Reset();
        }
    }

    public void Stop()
    {
        lock (_gate)
            _active = false;
    }

    public void RecordCallback(TimeSpan now)
    {
        lock (_gate)
        {
            if (_active)
                _lastCallback = now;
        }
    }

    public void FeedAction(string name, string action, int x, int y, TimeSpan now)
    {
        lock (_gate)
        {
            if (!_active)
                return;

            if (action == "Click")
                _lastClick = now;

            if (action == "Click" && !string.IsNullOrWhiteSpace(name)
                && _loopDetector.Feed(name, action, x, y))
                _loopReason = $"画面冻结：重复点击 {name}，坐标 ({x},{y})";
        }
    }

    public void RecordActivity(TimeSpan now)
    {
        lock (_gate)
        {
            if (_active)
                _lastClick = now;
        }
    }

    public string? GetReason(TimeSpan now, TimeSpan timeout, bool enabled, bool waiting)
    {
        lock (_gate)
        {
            if (!_active)
                return null;

            if (!enabled || waiting)
            {
                _lastCallback = now;
                _lastClick = now;
                _loopReason = null;
                _loopDetector.Reset();
                return null;
            }

            if (_loopReason != null)
                return _loopReason;
            if (now - _lastCallback >= timeout)
                return $"{EmulatorUnresponsiveReasonPrefix}：超过 {timeout.TotalSeconds:0} 秒没有任务回调";
            if (now - _lastClick >= timeout)
                return $"任务无点击：超过 {timeout.TotalSeconds:0} 秒没有成功点击";

            return null;
        }
    }

    /// <summary>判断原因是否代表模拟器整体无响应。</summary>
    public static bool IsEmulatorUnresponsiveReason(string? reason) =>
        reason != null && reason.StartsWith(EmulatorUnresponsiveReasonPrefix, StringComparison.Ordinal);

    public static bool ShouldStartEmulator(bool retryLaunch, bool restartAdb, bool hardRestartAdb) =>
        retryLaunch || restartAdb || hardRestartAdb;

    public static string GetDisplayReason(string reason)
    {
        if (reason.StartsWith("动作循环卡死", StringComparison.Ordinal))
            return $"动作循环{reason["动作循环卡死".Length..]}";
        if (reason.StartsWith("画面冻结", StringComparison.Ordinal))
            return $"动作循环{reason["画面冻结".Length..]}";
        return reason;
    }
}
