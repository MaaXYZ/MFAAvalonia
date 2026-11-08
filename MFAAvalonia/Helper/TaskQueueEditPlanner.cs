using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>Creates the detached state for one user edit before committing it to disk.</summary>
public static class TaskQueueEditPlanner
{
    public static TaskQueueState Build(TaskQueueState saved,
        IEnumerable<MaaInterface.MaaInterfaceTask> candidate, string? deletedName = null,
        bool resetHistory = false, bool setPreset = false, string? presetKey = null)
    {
        var next = saved.Clone();
        next.Tasks = candidate.Select(task => task.Clone()).ToList();
        var previousNames = saved.Tasks.Where(task => !IsSpecialTask(task)).Select(task => task.Name).Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.Ordinal);
        if (resetHistory)
        {
            next.CurrentTasks.Clear();
            next.DeletedTaskNames.Clear();
            next.MissingTaskSessions.Clear();
        }
        foreach (var task in next.Tasks.Where(task => !IsSpecialTask(task)))
        {
            if (string.IsNullOrEmpty(task.Name)) continue;
            var key = TaskQueueReconciler.HistoryKey(task);
            if (!next.CurrentTasks.Contains(key)) next.CurrentTasks.Add(key);
            if (resetHistory || !previousNames.Contains(task.Name))
            {
                next.DeletedTaskNames.Remove(task.Name);
                next.MissingTaskSessions.Remove(task.Name);
            }
        }
        if (!string.IsNullOrEmpty(deletedName)
            && saved.Tasks.Any(task => task.Name == deletedName && !IsSpecialTask(task))
            && !next.Tasks.Any(task => task.Name == deletedName && !IsSpecialTask(task)))
        {
            next.DeletedTaskNames.Add(deletedName);
            next.MissingTaskSessions.Remove(deletedName);
        }
        if (resetHistory || deletedName != null || next.Tasks.Count > 0)
            next.KeepEmpty = next.Tasks.Count == 0;
        next.IsInitialized = true;
        next.HasExplicitDeletionState = true;
        if (setPreset)
        {
            next.HasPresetKey = true;
            next.PresetKey = string.IsNullOrWhiteSpace(presetKey) ? null : presetKey;
        }
        return next;
    }

    private static bool IsSpecialTask(MaaInterface.MaaInterfaceTask task) =>
        !string.IsNullOrEmpty(task.Entry) && AddTaskDialogViewModel.SpecialActionNames.Contains(task.Entry);
}
