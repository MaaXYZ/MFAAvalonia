using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Pages;
using MaaFramework.Binding;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Services;

public sealed record ScheduledTaskHandoffCandidate(string InstanceId, string? DeviceIdentity, bool IsActive);

public static class ScheduledTaskHandoffPlanner
{
    public static IReadOnlySet<string> FindConflictingInstanceIds(
        ScheduledTaskHandoffCandidate target,
        IEnumerable<ScheduledTaskHandoffCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Where(candidate => candidate.IsActive
                && (string.Equals(candidate.InstanceId, target.InstanceId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(target.DeviceIdentity)
                        && string.Equals(candidate.DeviceIdentity, target.DeviceIdentity, StringComparison.OrdinalIgnoreCase))))
            .Select(candidate => candidate.InstanceId)
            .ToHashSet(StringComparer.Ordinal);
    }
}

public static class ScheduledTaskHandoffService
{
    private static readonly TimeSpan NaturalCompletionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StopCompletionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> HandoffLocks = new(StringComparer.Ordinal);

    public static async Task ExecuteAsync(
        TaskQueueViewModel targetViewModel,
        bool forceExecution,
        Func<Task> startTarget)
    {
        ArgumentNullException.ThrowIfNull(targetViewModel);
        ArgumentNullException.ThrowIfNull(startTarget);

        try
        {
            var target = DispatcherHelper.RunOnMainThread(() => CreateCandidate(targetViewModel));
            var started = await CoordinateAsync(
                target,
                () => DispatcherHelper.RunOnMainThread(() => CaptureCandidates(targetViewModel)),
                forceExecution,
                instanceId => DispatcherHelper.RunOnMainThread(() =>
                    MaaProcessorManager.Instance.GetViewModel(instanceId)?.StopTask()),
                () => DispatcherHelper.RunOnMainThreadAsync<Task>(startTarget).Unwrap());

            if (!started)
            {
                LoggerHelper.Warning(forceExecution
                    ? $"[定时执行] 冲突任务未在 {StopCompletionTimeout.TotalSeconds:0} 秒内停止，已取消本次定时启动。"
                    : $"[定时执行] 冲突任务未在 {NaturalCompletionTimeout.TotalMinutes:0} 分钟内结束，已保留原任务并取消本次定时启动。");
            }
        }
        catch (Exception exception)
        {
            LoggerHelper.Error($"[定时执行] 任务交接失败：{exception.Message}", exception);
        }
    }

    public static async Task<bool> CoordinateAsync(
        ScheduledTaskHandoffCandidate target,
        Func<IReadOnlyCollection<ScheduledTaskHandoffCandidate>> captureCandidates,
        bool forceExecution,
        Action<string> stopInstance,
        Func<Task> startTarget,
        TimeSpan? naturalCompletionTimeout = null,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(captureCandidates);
        ArgumentNullException.ThrowIfNull(stopInstance);
        ArgumentNullException.ThrowIfNull(startTarget);

        var timeout = forceExecution ? StopCompletionTimeout : naturalCompletionTimeout ?? NaturalCompletionTimeout;
        var interval = pollInterval ?? StatePollInterval;
        var delay = delayAsync ?? (duration => Task.Delay(duration, cancellationToken));
        var locks = GetCoordinationLocks(target);
        var acquiredLocks = new List<SemaphoreSlim>(locks.Count);

        try
        {
            foreach (var handoffLock in locks)
            {
                await handoffLock.WaitAsync(cancellationToken);
                acquiredLocks.Add(handoffLock);
            }

            var stopwatch = Stopwatch.StartNew();
            var conflicts = GetActiveConflicts(target, captureCandidates);
            if (conflicts.Count == 0)
            {
                await startTarget();
                return true;
            }

            if (forceExecution)
            {
                var requestedStops = new HashSet<string>(StringComparer.Ordinal);
                foreach (var instanceId in conflicts)
                {
                    stopInstance(instanceId);
                    requestedStops.Add(instanceId);
                }

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    conflicts = GetActiveConflicts(target, captureCandidates);
                    if (conflicts.Count == 0)
                        break;

                    foreach (var instanceId in conflicts.Where(id => !requestedStops.Contains(id)))
                    {
                        stopInstance(instanceId);
                        requestedStops.Add(instanceId);
                    }

                    if (stopwatch.Elapsed >= timeout)
                        return false;

                    await delay(interval);
                }

                await startTarget();
                return true;
            }

            while (conflicts.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed >= timeout)
                    return false;

                await delay(interval);
                conflicts = GetActiveConflicts(target, captureCandidates);
            }

            await startTarget();
            return true;
        }
        finally
        {
            for (var index = acquiredLocks.Count - 1; index >= 0; index--)
                acquiredLocks[index].Release();
        }
    }

    private static IReadOnlyCollection<string> GetActiveConflicts(
        ScheduledTaskHandoffCandidate target,
        Func<IReadOnlyCollection<ScheduledTaskHandoffCandidate>> captureCandidates)
    {
        return ScheduledTaskHandoffPlanner.FindConflictingInstanceIds(target, captureCandidates());
    }

    private static List<SemaphoreSlim> GetCoordinationLocks(ScheduledTaskHandoffCandidate target)
    {
        var keys = new List<string> { $"instance:{target.InstanceId}" };
        if (!string.IsNullOrWhiteSpace(target.DeviceIdentity))
            keys.Add($"device:{target.DeviceIdentity.ToUpperInvariant()}");

        return keys
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => HandoffLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1)))
            .ToList();
    }

    private static IReadOnlyCollection<ScheduledTaskHandoffCandidate> CaptureCandidates(TaskQueueViewModel targetViewModel)
    {
        var candidates = MaaProcessorManager.Instance.Instances
            .Select(processor => processor.ViewModel)
            .OfType<TaskQueueViewModel>()
            .Append(targetViewModel)
            .DistinctBy(viewModel => viewModel.Processor.InstanceId, StringComparer.Ordinal)
            .Select(CreateCandidate)
            .ToList();

        return candidates;
    }

    private static ScheduledTaskHandoffCandidate CreateCandidate(TaskQueueViewModel viewModel)
    {
        var processor = viewModel.Processor;
        var isActive = viewModel.IsRunning || processor.IsTaskRunActive || processor.TaskQueue.Count > 0;
        return new ScheduledTaskHandoffCandidate(
            processor.InstanceId,
            GetControllerTargetIdentity(viewModel),
            isActive);
    }

    private static string? GetControllerTargetIdentity(TaskQueueViewModel viewModel)
    {
        return viewModel.CurrentController switch
        {
            MaaControllerTypes.Adb => GetAdbIdentity(viewModel),
            MaaControllerTypes.Win32 or MaaControllerTypes.Gamepad => GetDesktopWindowIdentity(viewModel),
            MaaControllerTypes.MacOS => GetMacOSWindowIdentity(viewModel),
            MaaControllerTypes.WlRoots => GetWlRootsIdentity(viewModel),
            MaaControllerTypes.PlayCover => GetPlayCoverIdentity(viewModel),
            _ => null
        };
    }

    private static string? GetAdbIdentity(TaskQueueViewModel viewModel)
    {
        var device = viewModel.CurrentDevice as AdbDeviceInfo ?? viewModel.Processor.Config.AdbDevice.Info;
        var serial = device?.AdbSerial ?? viewModel.Processor.Config.AdbDevice.AdbSerial;
        return string.IsNullOrWhiteSpace(serial) ? null : $"adb:{serial.Trim()}";
    }

    private static string? GetDesktopWindowIdentity(TaskQueueViewModel viewModel)
    {
        var handle = (viewModel.CurrentDevice as DesktopWindowInfo)?.Handle
            ?? viewModel.Processor.Config.DesktopWindow.HWnd;
        return handle == IntPtr.Zero ? null : $"desktop-window:{handle.ToInt64():X}";
    }

    private static string? GetMacOSWindowIdentity(TaskQueueViewModel viewModel)
    {
        var windowId = (viewModel.CurrentDevice as MacOSWindowInfo)?.WindowId
            ?? viewModel.Processor.Config.MacOSWindow.WindowId;
        return windowId == 0 ? null : $"macos-window:{windowId}";
    }

    private static string? GetWlRootsIdentity(TaskQueueViewModel viewModel)
    {
        var socketPath = (viewModel.CurrentDevice as WlRootsSocketInfo)?.SocketPath
            ?? viewModel.Processor.Config.WlRoots.SocketPath;
        return string.IsNullOrWhiteSpace(socketPath) ? null : $"wlroots:{socketPath.Trim()}";
    }

    private static string? GetPlayCoverIdentity(TaskQueueViewModel viewModel)
    {
        var address = viewModel.Processor.Config.PlayCover.PlayCoverAddress;
        return string.IsNullOrWhiteSpace(address) ? null : $"playcover:{address.Trim()}";
    }
}
