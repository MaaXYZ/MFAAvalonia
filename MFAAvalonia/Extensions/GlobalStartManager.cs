using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Extensions;

/// <summary>
/// 使用各实例自身的启动设置批量启动所有实例。
/// </summary>
internal static class GlobalStartManager
{
    public static async Task StartAllAndRunTasks()
    {
        try
        {
            var manager = MaaProcessorManager.Instance;
            var instances = manager.GetAllInstanceIdsAndNames()
                .Where(instance => IsInstanceIncluded(manager, instance.Id))
                .ToList();
            var extraLaunchItems = DiscoverExtraLaunchItems();
            if (instances.Count == 0 && extraLaunchItems.Count == 0)
            {
                LoggerHelper.Warning("全局启动：没有已启用的启动项");
                return;
            }

            LoggerHelper.Info($"全局启动：准备启动 {instances.Count} 个实例和 {extraLaunchItems.Count} 个额外启动项");
            var tasks = instances.Select(instance => StartInstance(instance.Id, instance.Name)).ToList();
            tasks.AddRange(extraLaunchItems.Select(StartExtraLaunchItem));
            var results = await Task.WhenAll(tasks);

            var startedCount = results.Count(result => result == GlobalStartResult.Started);
            var skippedCount = results.Count(result => result == GlobalStartResult.Skipped);
            var failedCount = results.Count(result => result == GlobalStartResult.Failed);
            LoggerHelper.Info($"全局启动完成：已启动 {startedCount} 个，跳过 {skippedCount} 个，失败 {failedCount} 个");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"全局启动失败: {ex.Message}", ex);
        }
    }

    private static bool IsInstanceIncluded(MaaProcessorManager manager, string instanceId)
    {
        manager.EnsureInstanceLoaded(instanceId);
        return manager.GetViewModel(instanceId)?.Processor.InstanceConfiguration
            .GetValue(ConfigurationKeys.IncludeInGlobalStart, true) == true;
    }

    private static List<ExtraLaunchItem> DiscoverExtraLaunchItems()
    {
        var countText = GlobalConfiguration.GetValue(ConfigurationKeys.GlobalExtraLaunchCount, "0");
        var count = int.TryParse(countText, out var parsedCount) ? Math.Max(0, parsedCount) : 0;
        var items = new List<ExtraLaunchItem>();
        for (var i = 0; i < count; i++)
        {
            var enabled = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchEnabledKeyFormat, i),
                bool.TrueString) == bool.TrueString;
            if (!enabled) continue;

            var name = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchNameKeyFormat, i), $"额外启动项 {i + 1}");
            var path = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchPathKeyFormat, i), string.Empty);
            var args = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchArgsKeyFormat, i), string.Empty);
            var waitText = GlobalConfiguration.GetValue(
                string.Format(ConfigurationKeys.GlobalExtraLaunchWaitKeyFormat, i), "0");
            var wait = double.TryParse(waitText, out var parsedWait) ? Math.Max(0, parsedWait) : 0;
            items.Add(new ExtraLaunchItem(name, path, args, wait));
        }

        return items;
    }

    private static async Task<GlobalStartResult> StartExtraLaunchItem(ExtraLaunchItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            {
                LoggerHelper.Warning($"全局启动：额外启动项 {item.Name} 的路径无效，已跳过: {item.Path}");
                return GlobalStartResult.Failed;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = item.Args,
                UseShellExecute = true,
                CreateNoWindow = false
            };
            Process.Start(startInfo);
            if (item.WaitSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(item.WaitSeconds));
            return GlobalStartResult.Started;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"全局启动：额外启动项 {item.Name} 启动失败: {ex.Message}", ex);
            return GlobalStartResult.Failed;
        }
    }

    private static async Task<GlobalStartResult> StartInstance(string instanceId, string instanceName)
    {
        try
        {
            var manager = MaaProcessorManager.Instance;
            manager.EnsureInstanceLoaded(instanceId);
            var vm = manager.GetViewModel(instanceId);
            if (vm == null)
            {
                LoggerHelper.Warning($"全局启动：无法加载实例 {instanceName}");
                return GlobalStartResult.Failed;
            }

            if (vm.IsRunning)
            {
                LoggerHelper.Info($"全局启动：实例 {instanceName} 正在运行，已跳过");
                return GlobalStartResult.Skipped;
            }

            var beforeTask = vm.Processor.InstanceConfiguration.GetValue(ConfigurationKeys.BeforeTask, "None");
            if (beforeTask.Contains("StartupSoftware", StringComparison.OrdinalIgnoreCase))
            {
                LoggerHelper.Info($"全局启动：按实例配置启动 {instanceName} 的目标程序");
                await vm.Processor.StartSoftware();
            }

            await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                Instances.InstanceTabBarViewModel.SwitchToInstanceById(instanceId);
                vm.TryReadAdbDeviceFromConfig(false, false, true, false);
                vm.StartTask();
            });

            return GlobalStartResult.Started;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"全局启动：实例 {instanceName} 启动失败: {ex.Message}", ex);
            return GlobalStartResult.Failed;
        }
    }

    private enum GlobalStartResult
    {
        Started,
        Skipped,
        Failed
    }

    private sealed record ExtraLaunchItem(string Name, string Path, string Args, double WaitSeconds);
}
