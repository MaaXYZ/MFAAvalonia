using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 队列占位任务（queue hold）。
/// MaaFramework 在「任务结束且队列为空」时自动 inactive，Win32 下会撤销伪最小化；MFAA 逐个投递任务，
/// 于是每个任务边界都会露出窗口。每投一个真任务就紧跟一个占位任务，占位阻塞到 MFAA 投好下一个真任务后才放行，
/// 内核在任务之间看到的队列始终不空，只在整批结束时 inactive 一次。
/// </summary>
internal static class QueueHold
{
    public const string NodeName = "MFAA_QueueHold";
    public const string ActionName = "MFAA_QueueHoldAction";

    /// <summary>占位最长阻塞时间。正常间隙只有毫秒级，超过说明调度出了问题，退回原行为。</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>占位检查停止与释放状态的间隔，决定停止响应延迟的上限。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>放行最后一个占位后等待内核收尾（含 inactive）的上限。</summary>
    public static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool IsHoldNode(string? name) => string.Equals(name, NodeName, StringComparison.Ordinal);

    /// <summary>
    /// 生成占位任务的 pipeline_override。占位沿用前一个真任务的覆盖（数组形式，后者覆盖前者），
    /// 让资源侧按节点读取的选项（如 attach）在占位期间与真任务一致；占位节点本身显式写满等待相关字段，
    /// 不继承资源 default_pipeline 里的延迟与 on_error。
    /// </summary>
    public static string BuildOverride(string? taskOverride, long seq)
    {
        var node = new JsonObject
        {
            ["recognition"] = "DirectHit",
            ["action"] = "Custom",
            ["custom_action"] = ActionName,
            ["custom_action_param"] = new JsonObject
            {
                ["seq"] = seq
            },
            ["enabled"] = true,
            ["timeout"] = 0,
            ["rate_limit"] = 0,
            ["pre_delay"] = 0,
            ["post_delay"] = 0,
            ["pre_wait_freezes"] = 0,
            ["post_wait_freezes"] = 0,
            ["next"] = new JsonArray(),
            ["on_error"] = new JsonArray()
        };
        var hold = new JsonObject
        {
            [NodeName] = node
        }.ToJsonString(RelaxedJson);

        var inherited = taskOverride?.Trim();
        if (string.IsNullOrEmpty(inherited) || inherited == "{}" || inherited[0] != '{')
            return hold;
        return $"[{inherited},{hold}]";
    }

    public static bool TryParseSeq(string? actionParam, out long seq)
    {
        seq = 0;
        if (string.IsNullOrWhiteSpace(actionParam))
            return false;

        try
        {
            var node = JsonNode.Parse(actionParam);
            // MaaFramework 可能把对象参数再编码成字符串传入。
            if (node is JsonValue value && value.TryGetValue<string>(out var inner))
                node = JsonNode.Parse(inner);
            return node is JsonObject obj
                && obj["seq"] is JsonValue seqValue
                && seqValue.TryGetValue(out seq);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal enum QueueHoldExit
{
    Released,
    Aborted,
    Closed,
    TimedOut
}

/// <summary>
/// 占位登记表：MFAA 投递占位前先登记序号，放行时撤销登记；占位 action 只认登记表。
/// 查不到的序号一律当作已放行，因此登记表出错只会退回原行为，不会把队列卡住。
/// </summary>
internal sealed class QueueHoldRegistry
{
    private sealed class Entry
    {
        public bool Released;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private bool _closed;

    public bool IsClosed
    {
        get
        {
            lock (_gate)
                return _closed;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool IsRegistered(long seq)
    {
        lock (_gate)
            return _entries.ContainsKey(seq);
    }

    public bool Register(long seq)
    {
        lock (_gate)
        {
            if (_closed || _entries.ContainsKey(seq))
                return false;
            _entries[seq] = new Entry();
            return true;
        }
    }

    public bool Release(long seq)
    {
        lock (_gate)
        {
            if (!_entries.Remove(seq, out var entry))
                return false;
            entry.Released = true;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public int ReleaseAll()
    {
        lock (_gate)
        {
            var count = _entries.Count;
            foreach (var entry in _entries.Values)
                entry.Released = true;
            _entries.Clear();
            Monitor.PulseAll(_gate);
            return count;
        }
    }

    /// <summary>永久关闭：放行全部占位，之后的登记一律失败。</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            foreach (var entry in _entries.Values)
                entry.Released = true;
            _entries.Clear();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// 占位 action 的阻塞主体。放行立即唤醒；停止、释放等外部条件由 <paramref name="shouldAbort"/> 轮询。
    /// 无论因何退出，都撤销该序号的登记。
    /// </summary>
    public QueueHoldExit WaitForRelease(long seq, Func<bool> shouldAbort, TimeSpan timeout, TimeSpan poll)
    {
        Entry? entry;
        lock (_gate)
        {
            if (_closed)
                return QueueHoldExit.Closed;
            if (!_entries.TryGetValue(seq, out entry))
                return QueueHoldExit.Released;
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (entry.Released)
                    return _closed ? QueueHoldExit.Closed : QueueHoldExit.Released;
                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _entries.Remove(seq);
                    return QueueHoldExit.TimedOut;
                }
                Monitor.Wait(_gate, remaining < poll ? remaining : poll);
                if (entry.Released)
                    return _closed ? QueueHoldExit.Closed : QueueHoldExit.Released;
            }

            // 原生查询放在锁外，避免放行方被阻塞。
            if (shouldAbort())
            {
                lock (_gate)
                    _entries.Remove(seq);
                return QueueHoldExit.Aborted;
            }
        }
    }
}

internal sealed class QueueHoldTicket
{
    public QueueHoldTicket(long seq, long jobId, Func<bool> isTaskerBusy)
    {
        Seq = seq;
        JobId = jobId;
        IsTaskerBusy = isTaskerBusy;
    }

    public long Seq { get; }
    public long JobId { get; }

    /// <summary>占位所在 Tasker 是否仍在运行。只做非阻塞查询，不在原生等待里长时间停留。</summary>
    public Func<bool> IsTaskerBusy { get; }
}

/// <summary>
/// 管理「当前占位」：同一实例同一时刻最多一个占位在守边界。
/// 顺序约定：先投下一个真任务，再投它的占位，最后放行上一个占位。
/// </summary>
internal sealed class QueueHoldCoordinator
{
    private readonly QueueHoldRegistry _registry = new();
    private readonly Lock _lock = new();
    private QueueHoldTicket? _outstanding;
    private long _nextSeq;

    public QueueHoldRegistry Registry => _registry;

    public QueueHoldTicket? Outstanding
    {
        get
        {
            lock (_lock)
                return _outstanding;
        }
    }

    /// <summary>
    /// 登记后投递占位；先登记保证占位 action 开始时一定查得到自己。投递失败（返回无效 id 或抛异常）时撤销登记并返回 null。
    /// </summary>
    public QueueHoldTicket? TryPost(Func<string, string, long> append, Func<bool> isTaskerBusy, string? taskOverride, out string? failure)
    {
        failure = null;
        var seq = Interlocked.Increment(ref _nextSeq);
        if (!_registry.Register(seq))
        {
            failure = "占位登记表已关闭";
            return null;
        }

        long jobId;
        try
        {
            jobId = append(QueueHold.NodeName, QueueHold.BuildOverride(taskOverride, seq));
        }
        catch (Exception ex)
        {
            _registry.Release(seq);
            failure = ex.Message;
            return null;
        }

        if (jobId == 0)
        {
            _registry.Release(seq);
            failure = "内核返回无效任务 id";
            return null;
        }

        return new QueueHoldTicket(seq, jobId, isTaskerBusy);
    }

    /// <summary>新占位接班并放行旧占位。旧占位不必等待：下一个真任务已经排在它后面。</summary>
    public void Replace(QueueHoldTicket? next)
    {
        QueueHoldTicket? previous;
        lock (_lock)
        {
            previous = _outstanding;
            _outstanding = next;
        }
        if (previous != null)
            _registry.Release(previous.Seq);
    }

    /// <summary>取下并放行当前占位，返回它以便调用方等待内核收尾。</summary>
    public QueueHoldTicket? ReleaseOutstanding()
    {
        QueueHoldTicket? previous;
        lock (_lock)
        {
            previous = _outstanding;
            _outstanding = null;
        }
        if (previous != null)
            _registry.Release(previous.Seq);
        return previous;
    }

    /// <summary>放行全部登记（Tasker 被替换时用，登记可能属于旧 Tasker）。</summary>
    public void ReleaseAll()
    {
        lock (_lock)
            _outstanding = null;
        _registry.ReleaseAll();
    }

    public void Close()
    {
        lock (_lock)
            _outstanding = null;
        _registry.Close();
    }

    /// <summary>
    /// 等占位所在 Tasker 空闲。内核在任务结束、发现队列为空时同步执行 inactive，之后才把 Tasker 置为空闲，
    /// 所以这里返回 true 时窗口收尾已经完成。取消时立即返回 false：停止流程自己会等内核停稳。
    /// </summary>
    public static async Task<bool> WaitForExitAsync(QueueHoldTicket? ticket, TimeSpan timeout, TimeSpan poll,
        CancellationToken cancellation = default)
    {
        if (ticket == null)
            return true;

        var stopwatch = Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested)
        {
            bool busy;
            try
            {
                busy = ticket.IsTaskerBusy();
            }
            catch (ObjectDisposedException)
            {
                return true;
            }

            if (!busy)
                return true;
            if (stopwatch.Elapsed >= timeout)
                return false;
            await Task.Delay(poll).ConfigureAwait(false);
        }

        return false;
    }
}

/// <summary>
/// 占位任务的 custom action：阻塞到被放行、Tasker 停止或被释放、登记表关闭或安全超时，始终返回成功，
/// 以免触发 on_error、错误截图与失败统计。
/// </summary>
internal sealed class QueueHoldAction : IMaaCustomAction
{
    private readonly QueueHoldRegistry _registry;
    private readonly Action<string>? _warn;

    public QueueHoldAction(QueueHoldRegistry registry, Action<string>? warn = null)
    {
        _registry = registry;
        _warn = warn;
    }

    public string Name { get; set; } = QueueHold.ActionName;

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        try
        {
            if (!QueueHold.TryParseSeq(args.ActionParam, out var seq))
            {
                _warn?.Invoke($"队列占位参数无效，立即放行：{args.ActionParam}");
                return true;
            }

            var tasker = context.Tasker;
            var exit = _registry.WaitForRelease(seq,
                () => tasker.IsInvalid || tasker.IsStopping,
                QueueHold.SafetyTimeout,
                QueueHold.PollInterval);
            if (exit == QueueHoldExit.TimedOut)
                _warn?.Invoke($"队列占位等待超过 {QueueHold.SafetyTimeout.TotalSeconds:F0} 秒仍未放行，已自行结束：seq={seq}");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"队列占位异常，立即放行：{ex.Message}");
        }

        return true;
    }
}
