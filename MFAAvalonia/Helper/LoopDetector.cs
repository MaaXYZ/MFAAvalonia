using System;

namespace MFAAvalonia.Helper;

/// <summary>统计连续重复的点击动作，并用二次确认排除短暂的正常重复。</summary>
public sealed class LoopDetector
{
    /// <summary>连续相同点击达到此次数后进入二次确认。</summary>
    public const int LoopThreshold = 80;

    /// <summary>二次确认需要额外观察的相同点击次数。</summary>
    public const int ConfirmCount = 20;

    private readonly object _gate = new();
    private string? _lastKey;
    private int _count;
    private int _confirmCount;

    public bool IsTriggered { get; private set; }

    public bool Feed(string nodeName, string action, int x, int y)
    {
        lock (_gate)
        {
            if (IsTriggered)
                return false;

            var key = $"{nodeName}|{action}";
            if (key != _lastKey)
            {
                _lastKey = key;
                _count = 1;
                _confirmCount = 0;
                return false;
            }

            _count++;
            if (_count < LoopThreshold)
                return false;

            _confirmCount++;
            if (_confirmCount < ConfirmCount)
                return false;

            IsTriggered = true;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastKey = null;
            _count = 0;
            _confirmCount = 0;
            IsTriggered = false;
        }
    }
}
