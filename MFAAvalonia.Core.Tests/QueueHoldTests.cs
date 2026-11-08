using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class QueueHoldTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void OverrideWithoutTaskOverrideDefinesOnlyTheHoldNode()
    {
        var root = JsonNode.Parse(QueueHold.BuildOverride("{}", 7))!.AsObject();

        var node = root[QueueHold.NodeName]!.AsObject();
        Assert.Single(root);
        Assert.Equal("DirectHit", node["recognition"]!.GetValue<string>());
        Assert.Equal("Custom", node["action"]!.GetValue<string>());
        Assert.Equal(QueueHold.ActionName, node["custom_action"]!.GetValue<string>());
        Assert.Equal(7, node["custom_action_param"]!["seq"]!.GetValue<long>());
        Assert.Empty(node["next"]!.AsArray());
        Assert.Empty(node["on_error"]!.AsArray());
        foreach (var key in new[] { "pre_delay", "post_delay", "pre_wait_freezes", "post_wait_freezes", "timeout", "rate_limit" })
            Assert.Equal(0, node[key]!.GetValue<int>());
    }

    [Fact]
    public void OverrideInheritsTheTaskOverrideBeforeTheHoldNode()
    {
        const string task = "{\"StartGame_PCWindowOptions\":{\"attach\":{\"resolution\":\"1080p\",\"minimize\":true}}}";

        var root = JsonNode.Parse(QueueHold.BuildOverride(task, 3))!.AsArray();

        Assert.Equal(2, root.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(task), root[0]));
        Assert.Equal(3, root[1]![QueueHold.NodeName]!["custom_action_param"]!["seq"]!.GetValue<long>());
    }

    [Fact]
    public void OverrideSplicesTheTaskOverrideArrayBeforeTheHoldNode()
    {
        // MaaProcessor 交给 AppendTask 的参数是 MaaToken.ToString()：带缩进的 List<Dictionary> 数组。
        const string task = "[\r\n  {\r\n    \"StartGame_PCWindowOptions\": {\r\n      \"attach\": {\r\n        \"resolution\": \"1080p\"\r\n      }\r\n    }\r\n  },\r\n  {\r\n    \"B\": {}\r\n  }\r\n]";

        var root = JsonNode.Parse(QueueHold.BuildOverride(task, 5))!.AsArray();

        Assert.Equal(3, root.Count);
        Assert.Equal("1080p", root[0]!["StartGame_PCWindowOptions"]!["attach"]!["resolution"]!.GetValue<string>());
        Assert.True(root[1]!.AsObject().ContainsKey("B"));
        Assert.Equal(5, root[2]![QueueHold.NodeName]!["custom_action_param"]!["seq"]!.GetValue<long>());
    }

    [Fact]
    public void OverrideKeepsAnArrayWithOnlyAnEmptyObject()
    {
        var root = JsonNode.Parse(QueueHold.BuildOverride("[{}]", 2))!.AsArray();

        Assert.Equal(2, root.Count);
        Assert.Empty(root[0]!.AsObject());
        Assert.True(root[1]!.AsObject().ContainsKey(QueueHold.NodeName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[ ]")]
    [InlineData("not json")]
    public void OverrideFallsBackToTheHoldNodeWhenThereIsNothingToInherit(string? task)
    {
        var root = JsonNode.Parse(QueueHold.BuildOverride(task, 1))!.AsObject();

        Assert.True(root.ContainsKey(QueueHold.NodeName));
    }

    [Theory]
    [InlineData("{\"seq\":42}", true, 42)]
    [InlineData("\"{\\\"seq\\\":42}\"", true, 42)]
    [InlineData("{}", false, 0)]
    [InlineData("not json", false, 0)]
    [InlineData("", false, 0)]
    public void SeqIsReadFromPlainOrDoubleEncodedParameters(string param, bool expected, long seq)
    {
        Assert.Equal(expected, QueueHold.TryParseSeq(param, out var actual));
        Assert.Equal(seq, actual);
    }

    [Fact]
    public void OnlyTheHoldNodeNameIsRecognized()
    {
        Assert.True(QueueHold.IsHoldNode(QueueHold.NodeName));
        Assert.False(QueueHold.IsHoldNode("mfaa_queuehold"));
        Assert.False(QueueHold.IsHoldNode(null));
    }

    [Fact]
    public void UnknownSeqIsTreatedAsReleased()
    {
        var registry = new QueueHoldRegistry();

        Assert.Equal(QueueHoldExit.Released, registry.WaitForRelease(9, () => false, TimeSpan.FromSeconds(5), Poll));
    }

    [Fact]
    public void ReleaseFromAnotherThreadWakesTheHold()
    {
        var registry = new QueueHoldRegistry();
        Assert.True(registry.Register(1));

        var hold = Task.Run(() => registry.WaitForRelease(1, () => false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        Thread.Sleep(50);
        Assert.False(hold.IsCompleted);
        var stopwatch = Stopwatch.StartNew();
        Assert.True(registry.Release(1));

        Assert.Equal(QueueHoldExit.Released, hold.Result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "放行应立即唤醒，不等轮询周期");
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void StoppingTaskerAbortsTheHoldAndDropsItsEntry()
    {
        var registry = new QueueHoldRegistry();
        registry.Register(1);
        var stopping = false;

        var hold = Task.Run(() => registry.WaitForRelease(1, () => Volatile.Read(ref stopping), TimeSpan.FromSeconds(5), Poll));
        Thread.Sleep(50);
        Volatile.Write(ref stopping, true);

        Assert.Equal(QueueHoldExit.Aborted, hold.Result);
        Assert.False(registry.IsRegistered(1));
    }

    [Fact]
    public void SafetyTimeoutEndsTheHoldAndDropsItsEntry()
    {
        var registry = new QueueHoldRegistry();
        registry.Register(1);

        Assert.Equal(QueueHoldExit.TimedOut, registry.WaitForRelease(1, () => false, TimeSpan.FromMilliseconds(80), Poll));
        Assert.False(registry.IsRegistered(1));
    }

    [Fact]
    public void ClosingReleasesWaitersAndRejectsLaterHolds()
    {
        var registry = new QueueHoldRegistry();
        registry.Register(1);

        var hold = Task.Run(() => registry.WaitForRelease(1, () => false, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        Thread.Sleep(50);
        registry.Close();

        Assert.Equal(QueueHoldExit.Closed, hold.Result);
        Assert.False(registry.Register(2));
        Assert.Equal(QueueHoldExit.Closed, registry.WaitForRelease(2, () => false, TimeSpan.FromSeconds(5), Poll));
    }

    [Fact]
    public void HoldIsRegisteredBeforeItIsPosted()
    {
        var coordinator = new QueueHoldCoordinator();
        var registeredAtPost = false;

        var ticket = coordinator.TryPost((entry, pipelineOverride) =>
        {
            Assert.Equal(QueueHold.NodeName, entry);
            Assert.True(QueueHold.TryParseSeq(
                JsonNode.Parse(pipelineOverride)![QueueHold.NodeName]!["custom_action_param"]!.ToJsonString(), out var seq));
            registeredAtPost = coordinator.Registry.IsRegistered(seq);
            return 100;
        }, () => false, "{}", out var failure);

        Assert.NotNull(ticket);
        Assert.Null(failure);
        Assert.True(registeredAtPost);
        Assert.Equal(100, ticket.JobId);
    }

    [Fact]
    public void FailedPostUnregistersTheHold()
    {
        var coordinator = new QueueHoldCoordinator();

        Assert.Null(coordinator.TryPost((_, _) => 0, () => false, "{}", out var invalid));
        Assert.NotNull(invalid);
        Assert.Null(coordinator.TryPost((_, _) => throw new InvalidOperationException("boom"), () => false, "{}", out var thrown));
        Assert.Equal("boom", thrown);
        Assert.Equal(0, coordinator.Registry.Count);
    }

    [Fact]
    public void ReplacingReleasesThePreviousHoldOnlyAfterTheNextIsRegistered()
    {
        var coordinator = new QueueHoldCoordinator();
        var id = 0L;
        long Append(string entry, string pipelineOverride) => ++id;

        var first = coordinator.TryPost(Append, () => false, "{}", out _)!;
        coordinator.Replace(first);
        var second = coordinator.TryPost(Append, () => false, "{}", out _)!;
        Assert.True(coordinator.Registry.IsRegistered(first.Seq));
        Assert.True(coordinator.Registry.IsRegistered(second.Seq));

        coordinator.Replace(second);

        Assert.False(coordinator.Registry.IsRegistered(first.Seq));
        Assert.True(coordinator.Registry.IsRegistered(second.Seq));
        Assert.Same(second, coordinator.Outstanding);
        Assert.Same(second, coordinator.ReleaseOutstanding());
        Assert.Null(coordinator.ReleaseOutstanding());
        Assert.Equal(0, coordinator.Registry.Count);
    }

    [Fact]
    public async Task WaitingForExitFollowsTheTaskerBusyState()
    {
        var busy = true;
        var ticket = new QueueHoldTicket(1, 1, () => Volatile.Read(ref busy));

        Assert.False(await QueueHoldCoordinator.WaitForExitAsync(ticket, TimeSpan.FromMilliseconds(60), Poll));
        _ = Task.Delay(50).ContinueWith(_ => Volatile.Write(ref busy, false));
        Assert.True(await QueueHoldCoordinator.WaitForExitAsync(ticket, TimeSpan.FromSeconds(5), Poll));
        Assert.True(await QueueHoldCoordinator.WaitForExitAsync(null, TimeSpan.Zero, Poll));
        Assert.True(await QueueHoldCoordinator.WaitForExitAsync(
            new QueueHoldTicket(2, 2, () => throw new ObjectDisposedException("tasker")), TimeSpan.Zero, Poll));
    }

    [Fact]
    public async Task CancelledWaitReturnsWithoutWaitingForTheTasker()
    {
        using var cancellation = new CancellationTokenSource();
        var ticket = new QueueHoldTicket(1, 1, () => true);

        cancellation.CancelAfter(50);
        var stopwatch = Stopwatch.StartNew();

        Assert.False(await QueueHoldCoordinator.WaitForExitAsync(ticket, TimeSpan.FromSeconds(5), Poll, cancellation.Token));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HoldOrderKeepsTheQueueBusyUntilTheBatchEnds()
    {
        using var kernel = new FakeKernel();
        var coordinator = new QueueHoldCoordinator();
        kernel.Registry = coordinator.Registry;

        for (var i = 0; i < 4; i++)
        {
            var job = kernel.Append("Task" + i, "{}");
            coordinator.Replace(coordinator.TryPost(kernel.Append, () => kernel.Running, "{}", out _));
            kernel.Wait(job);
        }

        Assert.Equal(0, kernel.InactiveCount);
        Assert.True(await QueueHoldCoordinator.WaitForExitAsync(coordinator.ReleaseOutstanding(), TimeSpan.FromSeconds(5), Poll));
        Assert.Equal(1, kernel.InactiveCount);
        Assert.Equal(new[] { "Task0", "Task1", "Task2", "Task3" }, kernel.RealTasks);
    }

    [Fact]
    public async Task ReleasingBeforePostingTheNextTaskEmptiesTheQueueAtEveryBoundary()
    {
        using var kernel = new FakeKernel();
        var coordinator = new QueueHoldCoordinator();
        kernel.Registry = coordinator.Registry;

        for (var i = 0; i < 3; i++)
        {
            await QueueHoldCoordinator.WaitForExitAsync(coordinator.ReleaseOutstanding(), TimeSpan.FromSeconds(5), Poll);
            var job = kernel.Append("Task" + i, "{}");
            coordinator.Replace(coordinator.TryPost(kernel.Append, () => kernel.Running, "{}", out _));
            kernel.Wait(job);
        }
        await QueueHoldCoordinator.WaitForExitAsync(coordinator.ReleaseOutstanding(), TimeSpan.FromSeconds(5), Poll);

        Assert.Equal(3, kernel.InactiveCount);
    }

    [Fact]
    public async Task StoppingDuringAGapEndsTheRunningHold()
    {
        using var kernel = new FakeKernel();
        var coordinator = new QueueHoldCoordinator();
        kernel.Registry = coordinator.Registry;

        kernel.Wait(kernel.Append("Task0", "{}"));
        var job = kernel.Append("Task1", "{}");
        coordinator.Replace(coordinator.TryPost(kernel.Append, () => kernel.Running, "{}", out _));
        kernel.Wait(job);
        Assert.True(kernel.Running);

        var stopwatch = Stopwatch.StartNew();
        kernel.Stop();
        Assert.True(await QueueHoldCoordinator.WaitForExitAsync(coordinator.ReleaseOutstanding(), TimeSpan.FromSeconds(5), Poll));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(0, coordinator.Registry.Count);
    }

    /// <summary>
    /// 只模拟与占位相关的内核契约：单线程顺序执行；任务结束后队列为空才 inactive；
    /// 停止清空排队任务并让正在执行的任务看到 stopping。
    /// </summary>
    private sealed class FakeKernel : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<(long Id, string Entry, string Override)> _queue = new();
        private readonly Thread _worker;
        private long _nextId;
        private long _completedId;
        private bool _running;
        private bool _stopping;
        private bool _exit;

        public FakeKernel()
        {
            _worker = new Thread(Work) { IsBackground = true };
            _worker.Start();
        }

        public QueueHoldRegistry Registry { get; set; } = new();
        public int InactiveCount { get; private set; }
        public List<string> RealTasks { get; } = [];

        public bool Running
        {
            get
            {
                lock (_gate)
                    return _running;
            }
        }

        public long Append(string entry, string pipelineOverride)
        {
            lock (_gate)
            {
                if (_stopping && _running)
                    return 0;
                _stopping = false;
                var id = ++_nextId;
                _queue.Enqueue((id, entry, pipelineOverride));
                _running = true;
                Monitor.PulseAll(_gate);
                return id;
            }
        }

        public void Wait(long id)
        {
            lock (_gate)
            {
                while (_completedId < id && !_exit)
                    Monitor.Wait(_gate);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopping = true;
                _queue.Clear();
                _completedId = _nextId;
                Monitor.PulseAll(_gate);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _exit = true;
                Monitor.PulseAll(_gate);
            }
            Registry.Close();
            _worker.Join(TimeSpan.FromSeconds(5));
        }

        private void Work()
        {
            while (true)
            {
                (long Id, string Entry, string Override) item;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_exit)
                    {
                        _running = false;
                        Monitor.PulseAll(_gate);
                        Monitor.Wait(_gate);
                    }
                    if (_exit)
                        return;
                    item = _queue.Dequeue();
                }

                if (item.Entry == QueueHold.NodeName)
                {
                    var param = JsonNode.Parse(item.Override)![QueueHold.NodeName]!["custom_action_param"]!.ToJsonString();
                    QueueHold.TryParseSeq(param, out var seq);
                    Registry.WaitForRelease(seq, () => { lock (_gate) return _stopping; }, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5));
                }
                else
                {
                    lock (_gate)
                        RealTasks.Add(item.Entry);
                    Thread.Sleep(20);
                }

                lock (_gate)
                {
                    if (_queue.Count == 0)
                        InactiveCount++;
                    _completedId = Math.Max(_completedId, item.Id);
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
