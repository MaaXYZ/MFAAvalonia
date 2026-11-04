using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MFAAvalonia.ViewModels.Pages;

public partial class TaskQueueViewModel
{
    private sealed record TaskStateEditingContext(
        Func<InstanceConfiguration> Configuration,
        Func<MaaInterface?> Interface,
        Func<bool> IsSwitching,
        Action<DragItemViewModel> RefreshSupport,
        Action<string?> ReportError,
        Func<MaaInterface.MaaInterfaceTask, DragItemViewModel> CreateRow,
        Action<DragItemViewModel, MaaInterface.MaaInterfaceTask> UpdateRow);

    private TaskStateEditingContext? _taskStateEditingContext;
    private TaskStateEditingContext TaskEditing => _taskStateEditingContext ??= new(
        () => Processor.InstanceConfiguration,
        () => MaaProcessor.Interface,
        () => ConfigurationManager.IsSwitching,
        RefreshTaskSupportForCurrentContext,
        ReportTaskSaveFailure,
        task => new DragItemViewModel(task),
        (item, task) => item.InterfaceItem = task);

    private int _taskStateSaveSuppressionDepth;
    public bool IsPublishingTaskState => _taskStateSaveSuppressionDepth > 0;
    public bool IsTaskConfigurationValid { get; set; } = true;

    public IDisposable SuppressTaskStateSaving()
    {
        _taskStateSaveSuppressionDepth++;
        return new TaskStateSaveScope(this);
    }

    private sealed class TaskStateSaveScope(TaskQueueViewModel owner) : IDisposable
    {
        private TaskQueueViewModel? _owner = owner;
        public void Dispose()
        {
            if (_owner == null) return;
            _owner._taskStateSaveSuppressionDepth--;
            _owner = null;
        }
    }

    public bool TryApplyTaskEdit(IEnumerable<DragItemViewModel> candidate,
        string? deletedName = null, bool resetHistory = false, bool setPreset = false,
        string? presetKey = null)
    {
        if (IsPublishingTaskState || TaskEditing.IsSwitching() || !IsTaskConfigurationValid)
            return false;

        var items = candidate.ToList();
        if (!TryCommitTaskItems(items, deletedName, resetHistory, setPreset, presetKey))
            return false;

        using (SuppressTaskStateSaving())
        {
            foreach (var item in items)
            {
                item.OwnerViewModel = this;
                TaskEditing.RefreshSupport(item);
            }
            TaskItemViewModels = new ObservableCollection<DragItemViewModel>(items);
        }
        return true;
    }

    public bool TrySaveTaskItems()
    {
        if (IsPublishingTaskState || TaskEditing.IsSwitching())
            return true;
        if (!IsTaskConfigurationValid)
            return false;
        return TryCommitTaskItems(TaskItemViewModels.ToList(), restoreOnFailure: true);
    }

    private void ApplyTaskCheckState(bool isChecked)
    {
        var candidate = TaskItemViewModels.Select(item =>
        {
            if (!item.CanToggleCheck) return item;
            var clone = item.Clone();
            clone.OwnerViewModel = null;
            clone.CopyRuntimeStateFrom(item);
            clone.IsChecked = isChecked;
            return clone;
        }).ToList();
        TryApplyTaskEdit(candidate);
    }

    private bool TryCommitTaskItems(List<DragItemViewModel> items, string? deletedName = null,
        bool resetHistory = false, bool setPreset = false, string? presetKey = null,
        bool restoreOnFailure = false)
    {
        var configuration = TaskEditing.Configuration();
        if (!configuration.TryReadTaskState(out var saved, out var error))
        {
            IsTaskConfigurationValid = false;
            TaskEditing.ReportError(error);
            return false;
        }

        try
        {
            var next = TaskQueueEditPlanner.Build(saved,
                items.Where(item => !item.IsResourceOptionItem && item.InterfaceItem != null)
                    .Select(item => item.InterfaceItem!), deletedName, resetHistory, setPreset, presetKey);
            CaptureOptionState(next, items);
            NormalizeOptionState(next, TaskEditing.Interface());
            if (configuration.TryCommitTaskState(next, out error))
                return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (restoreOnFailure) RestoreTaskState(saved);
        TaskEditing.ReportError(error);
        return false;
    }

    private static List<MaaInterface.MaaInterfaceSelectOption> CloneOptions(
        List<MaaInterface.MaaInterfaceSelectOption> options) =>
        JsonConvert.DeserializeObject<List<MaaInterface.MaaInterfaceSelectOption>>(
            JsonConvert.SerializeObject(options)) ?? [];

    private static void CaptureOptionState(TaskQueueState state, IEnumerable<DragItemViewModel> items)
    {
        foreach (var item in items.Where(item => item.IsResourceOptionItem && item.ResourceItem?.SelectOptions != null))
        {
            var resource = item.ResourceItem!;
            var selections = CloneOptions(resource.SelectOptions!);
            if (resource.Name == "__GlobalOption__")
            {
                state.ExtraValues[ConfigurationKeys.GlobalOptionItems] = selections;
                continue;
            }
            const string controllerPrefix = "__ControllerOption__";
            var isController = resource.Name?.StartsWith(controllerPrefix, StringComparison.Ordinal) == true;
            var key = isController ? ConfigurationKeys.ControllerOptionItems : ConfigurationKeys.ResourceOptionItems;
            if (!state.ExtraValues.TryGetValue(key, out var value)
                || value is not Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>> buckets)
            {
                buckets = new(StringComparer.Ordinal);
                state.ExtraValues[key] = buckets;
            }
            buckets[isController ? resource.Name![controllerPrefix.Length..] : resource.Name ?? string.Empty] = selections;
        }
    }

    /// <summary>
    /// 写盘前把快照里等于当前默认的值改为跟随默认。快照已与界面对象分离，这里不改动界面持有的选项。
    /// 定义未加载时原样保存。
    /// </summary>
    private static void NormalizeOptionState(TaskQueueState state, MaaInterface? catalogue)
    {
        if (catalogue?.Option == null) return;
        foreach (var option in state.Tasks.SelectMany(task => task.Option ?? []))
            OptionValues.Normalize(catalogue.Option, option);
        foreach (var value in state.ExtraValues.Values)
        {
            if (value is IEnumerable<MaaInterface.MaaInterfaceSelectOption> options)
                foreach (var option in options) OptionValues.Normalize(catalogue.Option, option);
            else if (value is IDictionary<string, List<MaaInterface.MaaInterfaceSelectOption>> buckets)
                foreach (var option in buckets.Values.SelectMany(items => items))
                    OptionValues.Normalize(catalogue.Option, option);
        }
    }

    private void RestoreTaskState(TaskQueueState saved)
    {
        using var publication = SuppressTaskStateSaving();
        var current = TaskItemViewModels.Where(item => !item.IsResourceOptionItem).ToList();
        var restored = TaskItemViewModels.Where(item => item.IsResourceOptionItem).ToList();
        foreach (var item in restored) RestoreOptionState(saved, item, TaskEditing.Interface());
        for (var index = 0; index < saved.Tasks.Count; index++)
        {
            var task = saved.Tasks[index].Clone();
            var item = index < current.Count && current[index].InterfaceItem?.Name == task.Name
                ? current[index] : TaskEditing.CreateRow(task);
            TaskEditing.UpdateRow(item, task);
            item.OwnerViewModel = this;
            restored.Add(item);
        }
        TaskItemViewModels = new ObservableCollection<DragItemViewModel>(restored);
    }

    private static void RestoreOptionState(TaskQueueState saved, DragItemViewModel item, MaaInterface? catalogue)
    {
        var resource = item.ResourceItem;
        if (resource == null) return;
        const string controllerPrefix = "__ControllerOption__";
        List<MaaInterface.MaaInterfaceSelectOption>? selections = null;
        if (resource.Name == "__GlobalOption__")
            selections = saved.ExtraValues.GetValueOrDefault(ConfigurationKeys.GlobalOptionItems)
                as List<MaaInterface.MaaInterfaceSelectOption>;
        else
        {
            var isController = resource.Name?.StartsWith(controllerPrefix, StringComparison.Ordinal) == true;
            var key = isController ? ConfigurationKeys.ControllerOptionItems : ConfigurationKeys.ResourceOptionItems;
            if (saved.ExtraValues.GetValueOrDefault(key) is Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>> buckets)
                selections = buckets.GetValueOrDefault(isController ? resource.Name![controllerPrefix.Length..] : resource.Name ?? string.Empty);
        }
        var names = resource.Option ?? resource.SelectOptions?.Where(option => option.Name != null)
            .Select(option => option.Name!).ToList();
        resource.SelectOptions = selections != null ? CloneOptions(selections)
            : names?.Select(name => new MaaInterface.MaaInterfaceSelectOption { Name = name }).ToList();
        foreach (var option in resource.SelectOptions ?? [])
            TaskLoader.PrepareOption(catalogue, option);
        if (resource.Name == "__GlobalOption__" && catalogue != null)
            catalogue.GlobalSelectOptions = resource.SelectOptions;
        else if (resource.Name?.StartsWith(controllerPrefix, StringComparison.Ordinal) == true)
        {
            var controller = catalogue?.Controller?.FirstOrDefault(
                controller => controller.Name == resource.Name[controllerPrefix.Length..]);
            if (controller != null) controller.SelectOptions = resource.SelectOptions;
        }
        else
        {
            var definition = catalogue?.Resources.Values.FirstOrDefault(
                definition => definition.Name == resource.Name);
            if (definition != null) definition.SelectOptions = resource.SelectOptions;
        }
    }

    private void ReportTaskSaveFailure(string? error)
    {
        LoggerHelper.Warning($"任务配置保存失败：{error}");
        ToastHelper.Warn(LangKeys.TaskConfigurationTitle.ToLocalization(),
            TaskConfigurationFeedback.GetSaveFailureKey(TaskEditing.Configuration()).ToLocalization());
    }
}
