using MFAAvalonia.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>Reconciles a detached user queue with a complete, validated task catalogue.</summary>
public static class TaskQueueReconciler
{
    public const string HistorySeparator = "<|||>";

    /// <param name="FollowingDefaultCount">已存值中等于当前默认、改为跟随默认的个数。</param>
    public sealed record Result(TaskQueueState State, HashSet<string> MissingNames,
        List<string> RemovedNames, bool CircuitBroken, int FollowingDefaultCount = 0);

    public static string HistoryKey(MaaInterface.MaaInterfaceTask task)
        => $"{task.Name}{HistorySeparator}{task.Entry}";

    public static string NameFromHistory(string key)
    {
        var split = key.IndexOf(HistorySeparator, StringComparison.Ordinal);
        if (split > 0 && split == key.LastIndexOf(HistorySeparator, StringComparison.Ordinal)) return key[..split];
        if (split >= 0) return string.Empty;
        split = key.IndexOf(':');
        return split > 0 && split == key.LastIndexOf(':') ? key[..split] : string.Empty;
    }

    public static Result Reconcile(TaskQueueState saved, MaaInterface catalogue,
        string launchId, bool allowAutoRemoval, IReadOnlySet<string> specialEntries)
    {
        if (catalogue.Task == null)
            throw new ArgumentException("The complete task catalogue has not loaded.", nameof(catalogue));
        if (catalogue.Task.Any(task => string.IsNullOrWhiteSpace(task.Name) || string.IsNullOrWhiteSpace(task.Entry))
            || catalogue.Task.GroupBy(task => task.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ArgumentException("The task catalogue contains missing entries, missing names or duplicate names.", nameof(catalogue));

        var state = saved.Clone();
        var definitions = catalogue.Task.ToDictionary(task => task.Name!, StringComparer.Ordinal);
        bool IsSpecial(MaaInterface.MaaInterfaceTask task)
            => task.Entry != null && specialEntries.Contains(task.Entry);
        bool IsSpecialHistory(string key) => specialEntries.Any(entry =>
            key.EndsWith(HistorySeparator + entry, StringComparison.Ordinal)
            || key.EndsWith(":" + entry, StringComparison.Ordinal));
        var savedNames = state.Tasks.Where(task => !IsSpecial(task) && task.Name != null).Select(task => task.Name!).ToHashSet(StringComparer.Ordinal);
        var seen = state.CurrentTasks.Where(key => !IsSpecialHistory(key)).Select(NameFromHistory)
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToHashSet(StringComparer.Ordinal);

        // This import runs before catalogue matching can remove or rename anything.
        if (!state.HasExplicitDeletionState)
        {
            state.DeletedTaskNames.UnionWith(seen.Where(name => !savedNames.Contains(name)));
            state.HasExplicitDeletionState = true;
        }

        var resourceRows = state.Tasks.Where(task => !IsSpecial(task)).ToList();
        var missingRows = resourceRows.Where(task => task.Name == null || !definitions.ContainsKey(task.Name)).ToList();
        var missing = missingRows.Where(task => task.Name != null).Select(task => task.Name!).ToHashSet(StringComparer.Ordinal);
        var circuitBroken = missing.Count >= 3 && missingRows.Count * 2 >= resourceRows.Count;
        var removed = new List<string>();
        var followingDefault = 0;

        foreach (var name in state.MissingTaskSessions.Keys.ToList())
            if (definitions.ContainsKey(name) || !savedNames.Contains(name)) state.MissingTaskSessions.Remove(name);

        var retained = new List<MaaInterface.MaaInterfaceTask>();
        foreach (var task in state.Tasks)
        {
            if (IsSpecial(task))
            {
                retained.Add(task);
                continue;
            }
            var name = task.Name ?? string.Empty;
            if (definitions.TryGetValue(name, out var definition))
            {
                retained.Add(MergeDefinition(task, definition, catalogue, ref followingDefault));
                continue;
            }
            if (!circuitBroken)
            {
                if (allowAutoRemoval && state.MissingTaskSessions.TryGetValue(name, out var markedLaunch)
                    && markedLaunch != launchId)
                {
                    removed.Add(name);
                    continue;
                }
                state.MissingTaskSessions.TryAdd(name, launchId);
            }
            retained.Add(task);
        }

        foreach (var name in removed.Distinct())
        {
            state.MissingTaskSessions.Remove(name);
            state.CurrentTasks.RemoveAll(key => NameFromHistory(key) == name);
            seen.Remove(name);
        }
        if (removed.Count > 0 && retained.Count == 0) state.KeepEmpty = false;
        state.Tasks = retained;

        var presetOrder = string.IsNullOrWhiteSpace(state.PresetKey) ? null
            : catalogue.Preset?.FirstOrDefault(preset => preset.Name == state.PresetKey)?.Task?
                .Where(task => task.Name != null && definitions.ContainsKey(task.Name))
                .Select(task => task.Name!).ToList();
        var allowed = string.IsNullOrWhiteSpace(state.PresetKey) ? null
            : presetOrder?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var catalogueOrder = catalogue.Task.Select(task => task.Name!).ToList();

        var present = state.Tasks.Where(task => !IsSpecial(task) && task.Name != null).Select(task => task.Name!).ToHashSet(StringComparer.Ordinal);
        if (!state.KeepEmpty || !state.IsInitialized || state.Tasks.Count > 0)
        {
            for (var definitionIndex = 0; definitionIndex < catalogue.Task.Count; definitionIndex++)
            {
                var definition = catalogue.Task[definitionIndex];
                var name = definition.Name!;
                if (present.Contains(name) || seen.Contains(name) || state.DeletedTaskNames.Contains(name)
                    || (allowed != null && !allowed.Contains(name))) continue;
                var newTask = MergeDefinition(null, definition, catalogue, ref followingDefault);
                // Automatic seeding still adds one row per name, at its first preset occurrence.
                var presetIndex = presetOrder?.IndexOf(name) ?? -1;
                InsertBesideNeighbour(state.Tasks, newTask,
                    presetIndex >= 0 ? presetOrder! : catalogueOrder,
                    presetIndex >= 0 ? presetIndex : definitionIndex, specialEntries, presetIndex >= 0);
                present.Add(name);
                seen.Add(name);
            }
        }

        foreach (var task in state.Tasks)
        {
            var key = HistoryKey(task);
            if (!state.CurrentTasks.Contains(key)) state.CurrentTasks.Add(key);
        }
        state.IsInitialized = true;
        foreach (var value in state.ExtraValues.Values)
        {
            if (value is IEnumerable<MaaInterface.MaaInterfaceSelectOption> options)
                foreach (var option in options) followingDefault += TaskLoader.PrepareOption(catalogue, option);
            else if (value is IDictionary<string, List<MaaInterface.MaaInterfaceSelectOption>> buckets)
                foreach (var option in buckets.Values.SelectMany(items => items))
                    followingDefault += TaskLoader.PrepareOption(catalogue, option);
        }
        missing.ExceptWith(removed);
        return new Result(state, missing, removed, circuitBroken, followingDefault);
    }

    private static void InsertBesideNeighbour(List<MaaInterface.MaaInterfaceTask> queue,
        MaaInterface.MaaInterfaceTask task, IReadOnlyList<string> order, int index,
        IReadOnlySet<string> specialEntries, bool usePresetOccurrences)
    {
        int FindNeighbour(int referenceIndex, bool preceding)
        {
            var name = order[referenceIndex];
            bool Matches(MaaInterface.MaaInterfaceTask item) => item.Name == name
                && (item.Entry == null || !specialEntries.Contains(item.Entry));
            if (usePresetOccurrences && order.Count(candidate => candidate == name) > 1)
            {
                // Repeated preset entries are distinct positions; do not collapse them by name.
                var occurrence = order.Take(referenceIndex + 1).Count(candidate => candidate == name);
                for (var position = 0; position < queue.Count; position++)
                    if (Matches(queue[position]) && --occurrence == 0) return position;
                return -1;
            }
            return preceding ? queue.FindLastIndex(Matches) : queue.FindIndex(Matches);
        }

        for (var previous = index - 1; previous >= 0; previous--)
        {
            var position = FindNeighbour(previous, true);
            if (position < 0) continue;
            queue.Insert(position + 1, task);
            return;
        }
        for (var next = index + 1; next < order.Count; next++)
        {
            var position = FindNeighbour(next, false);
            if (position < 0) continue;
            queue.Insert(position, task);
            return;
        }
        queue.Add(task);
    }

    public static MaaInterface.MaaInterfaceTask MergeDefinition(MaaInterface.MaaInterfaceTask? saved,
        MaaInterface.MaaInterfaceTask definition, MaaInterface catalogue)
    {
        var followingDefault = 0;
        return MergeDefinition(saved, definition, catalogue, ref followingDefault);
    }

    private static MaaInterface.MaaInterfaceTask MergeDefinition(MaaInterface.MaaInterfaceTask? saved,
        MaaInterface.MaaInterfaceTask definition, MaaInterface catalogue, ref int followingDefault)
    {
        var task = definition.Clone();
        if (saved != null)
        {
            task.Check = saved.Check;
            task.RepeatCount = saved.RepeatCount;
            task.DisplayNameOverride = saved.DisplayNameOverride;
            task.Remark = saved.Remark;
            if (task.Advanced != null)
                foreach (var option in task.Advanced)
                {
                    var previous = saved.Advanced?.FirstOrDefault(item => item.Name == option.Name);
                    if (previous != null) option.Data = new(previous.Data);
                }
        }
        if (task.Option != null)
        {
            var previous = saved?.Clone().Option;
            task.Option = task.Option.Select(option => previous?.FirstOrDefault(item => item.Name == option.Name)
                ?? new MaaInterface.MaaInterfaceSelectOption { Name = option.Name }).ToList();
            foreach (var option in task.Option) followingDefault += TaskLoader.PrepareOption(catalogue, option);
        }
        return task;
    }
}
