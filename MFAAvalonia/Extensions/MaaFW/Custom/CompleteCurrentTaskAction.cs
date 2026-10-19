using MaaFramework.Binding;
using MaaFramework.Binding.Custom;
using MFAAvalonia.Helper;
using System;

namespace MFAAvalonia.Extensions.MaaFW.Custom;

/// <summary>
/// 请求当前队列项在本轮结束后停止重复执行。
/// </summary>
public sealed class CompleteCurrentTaskAction : IMaaCustomAction
{
    private readonly MaaProcessor _owner;

    public string Name { get; set; } = nameof(CompleteCurrentTaskAction);

    public CompleteCurrentTaskAction(MaaProcessor owner)
    {
        _owner = owner;
    }

    public bool Run<T>(T context, in RunArgs args, in RunResults results) where T : IMaaContext
    {
        try
        {
            var parameters = ActionParamHelper.Parse(args.ActionParam);
            var reason = (string?)parameters["reason"];
            if (string.IsNullOrWhiteSpace(reason))
                reason = "提前结束当前任务";

            if (!_owner.RequestEarlyCompletionForActiveTask(reason))
            {
                LoggerHelper.Warning("[提前结束任务] 当前没有可结束的队列项");
                _owner.AddLog("[提前结束任务] 当前没有可结束的队列项");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[提前结束任务] 处理请求失败：{ex.Message}");
            _owner.AddLog($"[提前结束任务] 处理请求失败：{ex.Message}");
            return false;
        }
    }
}
