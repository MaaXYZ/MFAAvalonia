using CommunityToolkit.Mvvm.ComponentModel;
using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Windows;
using Avalonia.Media;
using MFAAvalonia.Helper;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper.ValueType;

public partial class MFATask : ObservableObject
{
    public enum MFATaskType
    {
        MFA,
        MAAFW
    }

    public enum MFATaskStatus
    {
        NOT_STARTED,
        STOPPING,
        STOPPED,
        SUCCEEDED,
        FAILED
    }

    public readonly record struct RunResult(MFATaskStatus Status, bool ContinueQueue = false);

    [ObservableProperty] private string? _name = string.Empty;
    [ObservableProperty] private MFATaskType _type = MFATaskType.MFA;
    [ObservableProperty] private int _count = 1;
    [ObservableProperty] private Func<Task> _action;
    public Func<Task<MaaJobStatus>>? MaaAction { get; set; }
    // [ObservableProperty] private Dictionary<string, MaaNode> _tasks = new();
    [ObservableProperty] private bool _isUpdateRelated;

    public TaskQueueViewModel? OwnerViewModel { get; set; }
    public DragItemViewModel? SourceItem { get; set; }
    public long RunId { get; set; }
    public bool ContinueOnError { get; set; }

    public async Task<RunResult> Run(CancellationToken token)
    {
        var instanceId = OwnerViewModel?.Processor.InstanceId;
        if (instanceId != null)
            TelemetryService.StartTask(instanceId, this);

        RunResult Complete(MFATaskStatus status, bool continueQueue = false)
        {
            if (instanceId != null)
                TelemetryService.FinishTask(instanceId, this, status, status == MFATaskStatus.FAILED);
            return new RunResult(status, continueQueue);
        }

        void MarkFailed(string? detail = null)
        {
            var taskName = LanguageHelper.GetLocalizedString(Name);
            OwnerViewModel?.AddLogByKey(
                LangKeys.TaskFailedWithName,
                Brushes.OrangeRed,
                changeColor: false,
                transformKey: true,
                taskName);
            OwnerViewModel?.MarkTaskFailed(SourceItem, RunId, detail);
        }

        try
        {
            if (Count < 0)
                Count = int.MaxValue;
            OwnerViewModel?.MarkTaskRunning(SourceItem, RunId);
            var hasFailed = false;
            string? failureMessage = null;
            for (int i = 0; i < Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (Type == MFATaskType.MAAFW)
                {
                    OwnerViewModel?.AddLogByKey(LangKeys.TaskStart, (Avalonia.Media.IBrush?)null, true, true, LanguageHelper.GetLocalizedString(Name));
                    OwnerViewModel?.SetCurrentTaskName(LanguageHelper.GetLocalizedString(Name));
                }
                if (MaaAction != null)
                {
                    var jobStatus = await MaaAction();
                    token.ThrowIfCancellationRequested();
                    if (jobStatus != MaaJobStatus.Succeeded)
                    {
                        hasFailed = true;
                        failureMessage = jobStatus.ToString();
                        if (!ContinueOnError)
                        {
                            MarkFailed(failureMessage);
                            return Complete(MFATaskStatus.FAILED);
                        }
                    }
                }
                else
                {
                    await Action();
                    token.ThrowIfCancellationRequested();
                }
                OwnerViewModel?.MarkTaskIterationCompleted(SourceItem, RunId);
            }
            if (hasFailed)
            {
                MarkFailed(failureMessage);
                return Complete(MFATaskStatus.FAILED, ContinueOnError);
            }
            else
                OwnerViewModel?.MarkTaskSucceeded(SourceItem, RunId);
            return Complete(MFATaskStatus.SUCCEEDED);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            OwnerViewModel?.MarkTaskStopped(SourceItem, RunId);
            return Complete(MFATaskStatus.STOPPED);
        }
        catch (MaaJobStatusException ex)
        {
            MarkFailed(ex.Message);
            LoggerHelper.Error($"任务执行失败：{LanguageHelper.GetLocalizedString(Name)}");
            return Complete(MFATaskStatus.FAILED, ContinueOnError);
        }
        catch (OperationCanceledException)
        {
            OwnerViewModel?.MarkTaskStopped(SourceItem, RunId);
            return Complete(MFATaskStatus.STOPPED);
        }
        catch (InvalidOperationException ex) when (
            string.Equals(ex.Message, MaaProcessor.ConnectionFailedAfterAllRetriesMessage, StringComparison.Ordinal))
        {
            MarkFailed(ex.Message);
            LoggerHelper.Warning($"连接任务已在重试耗尽后结束：任务={LanguageHelper.GetLocalizedString(Name)}");
            return Complete(MFATaskStatus.FAILED, ContinueOnError);
        }
        catch (Exception ex)
        {
            MarkFailed(ex.Message);
            LoggerHelper.Error($"任务执行异常：任务={LanguageHelper.GetLocalizedString(Name)}，原因={ex.Message}", ex);
            return Complete(MFATaskStatus.FAILED, ContinueOnError);
        }
    }
}
