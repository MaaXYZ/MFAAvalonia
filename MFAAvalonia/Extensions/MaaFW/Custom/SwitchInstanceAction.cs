using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

/// <summary>
/// 特殊任务「切换实例」：停止当前实例任务，切换到目标实例（可为自己），重新连接模拟器并启动目标队列。
/// 目标实例通过 custom_action_param 的 target_instance 指定（实例名或 ID）。
/// </summary>
public class SwitchInstanceAction : IMaaCustomAction
{
    /// <summary>全局单飞行标志：同一时刻只允许一次切换，避免多个实例同时触发接力。</summary>
    private static int _switching;

    /// <summary>当前 action 所属实例（运行本任务的那个实例）。</summary>
    private readonly MaaProcessor _owner;

    public string Name { get; set; } = nameof(SwitchInstanceAction);

    public SwitchInstanceAction(MaaProcessor owner) => _owner = owner;

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        var acquired = false;
        try
        {
            var json = ActionParamHelper.Parse(args.ActionParam);
            var target = (string?)json["target_instance"] ?? string.Empty;

            var targetId = MaaProcessorManager.Instance.ResolveInstanceId(target);
            if (targetId == null)
            {
                Fail($"目标实例不存在：{target}");
                return false;
            }

            // 单飞行：已在切换中则拒绝本次请求。
            if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
            {
                Fail("已有切换实例正在进行，本次请求已忽略");
                return false;
            }
            acquired = true;

            LoggerHelper.Info($"[SwitchInstanceAction] 切换实例：{_owner.InstanceId} -> {targetId}");

            // 立即请求停止当前实例（异步排队），使“切换实例”之后的本实例任务尽快终止。
            _owner.Stop(MFATask.MFATaskStatus.STOPPED);

            // 切换、重连、启动等耗时操作放到后台，避免阻塞本任务的 native 回调。
            _ = Task.Run(() => SwitchAsync(_owner, targetId));
            return true;
        }
        catch (Exception e)
        {
            if (acquired)
                Interlocked.Exchange(ref _switching, 0);
            LoggerHelper.Error($"[SwitchInstanceAction] Error: {e.Message}");
            return false;
        }
    }

    private static async Task SwitchAsync(MaaProcessor from, string targetId)
    {
        try
        {
            // 等当前实例真正停稳，避免切换后其连接/截图回调仍指向旧实例。
            if (!await WaitIdleAsync(from, TimeSpan.FromSeconds(15)))
            {
                LoggerHelper.Warning("[SwitchInstanceAction] 等待当前实例停止超时，放弃切换");
                return;
            }

            var self = string.Equals(targetId, from.InstanceId, StringComparison.OrdinalIgnoreCase);
            MaaProcessor target;
            if (self)
            {
                target = from;
            }
            else
            {
                // 目标实例可能尚未被懒加载，先确保其已加载。
                MaaProcessorManager.Instance.EnsureInstanceLoaded(targetId);
                if (!MaaProcessorManager.Instance.TryGetInstance(targetId, out var loaded))
                {
                    LoggerHelper.Warning($"[SwitchInstanceAction] 目标实例加载失败：{targetId}");
                    return;
                }
                target = loaded;

                MaaProcessorManager.Instance.SwitchCurrent(targetId);
                DispatcherHelper.PostOnMainThread(() =>
                {
                    if (Instances.TryGetResolved<InstanceTabBarViewModel>(out var tabBar) && tabBar != null)
                        tabBar.SwitchToInstanceById(targetId);
                });
            }

            // 目标实例正在运行则跳过启动，避免打断其任务。
            if (target.IsTaskRunActive)
            {
                LoggerHelper.Info($"[SwitchInstanceAction] 目标实例 {targetId} 正在运行，已跳过启动");
                return;
            }

            // 断开旧连接，保证 Start 时“重新连接”模拟器（而非复用已连接状态）。
            target.SetTasker();
            target.Start();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[SwitchInstanceAction] 切换失败：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    private static async Task<bool> WaitIdleAsync(MaaProcessor processor, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!processor.IsTaskRunActive && processor.TaskQueue.Count == 0)
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    private void Fail(string message)
    {
        LoggerHelper.Error($"[SwitchInstanceAction] {message}");
        _owner.AddLog($"[切换实例] {message}");
    }
}
