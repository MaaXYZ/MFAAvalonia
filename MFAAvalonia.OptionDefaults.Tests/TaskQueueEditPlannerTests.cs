using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class TaskQueueEditPlannerTests
{
    [Fact]
    public void RuntimeStateTransferRetainsResumeMarkersAndElapsedDetails()
    {
        var previous = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        var replacement = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        previous.RunState = TaskRunState.Succeeded;
        previous.RunElapsed = TimeSpan.FromSeconds(35);
        previous.CompletedRunCount = 2;
        previous.TotalRunCount = 3;
        previous.RunErrorMessage = "previous detail";
        previous.RunStartedAt = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        previous.RunCompletedAt = previous.RunStartedAt.Value.AddSeconds(35);
        previous.RunId = 17;

        replacement.CopyRuntimeStateFrom(previous);

        Assert.Equal(TaskRunState.Succeeded, replacement.RunState);
        Assert.Equal(previous.RunElapsed, replacement.RunElapsed);
        Assert.Equal(2, replacement.CompletedRunCount);
        Assert.Equal(3, replacement.TotalRunCount);
        Assert.Equal(previous.RunErrorMessage, replacement.RunErrorMessage);
        Assert.Equal(previous.RunStartedAt, replacement.RunStartedAt);
        Assert.Equal(previous.RunCompletedAt, replacement.RunCompletedAt);
        Assert.Equal(17, replacement.RunId);
    }

    [Fact]
    public void DeletingOneDuplicateKeepsRemainingCopyAndOnlyLastRemovalSuppressesName()
    {
        var saved = State(Task("A", "one"), Task("A", "two"));
        var once = TaskQueueEditPlanner.Build(saved, [saved.Tasks[1]], deletedName: "A");
        Assert.Empty(once.DeletedTaskNames);
        Assert.Equal("two", Assert.Single(once.Tasks).Remark);
        var empty = TaskQueueEditPlanner.Build(once, [], deletedName: "A");
        Assert.Contains("A", empty.DeletedTaskNames);
        Assert.True(empty.KeepEmpty);
        Assert.Equal(2, saved.Tasks.Count);
    }

    [Fact]
    public void AddingTaskClearsItsDeletionAndMissingMarkerWithoutTouchingOtherNames()
    {
        var saved = State(Task("B"));
        saved.DeletedTaskNames.UnionWith(["A", "C"]);
        saved.MissingTaskSessions["A"] = "launch-one";
        saved.MissingTaskSessions["B"] = "launch-one";
        var next = TaskQueueEditPlanner.Build(saved, [saved.Tasks[0], Task("A")]);
        Assert.DoesNotContain("A", next.DeletedTaskNames);
        Assert.Contains("C", next.DeletedTaskNames);
        Assert.False(next.MissingTaskSessions.ContainsKey("A"));
        Assert.Equal("launch-one", next.MissingTaskSessions["B"]);
        Assert.Contains(TaskQueueReconciler.HistoryKey(Task("A")), next.CurrentTasks);
        Assert.Contains("A", saved.DeletedTaskNames);
    }

    [Fact]
    public void OrdinarySaveDoesNotInferDeletionOrTurnAutomaticallyEmptyQueueIntoUserEmpty()
    {
        var saved = State(Task("A"));
        saved.CurrentTasks.Add(TaskQueueReconciler.HistoryKey(saved.Tasks[0]));
        var next = TaskQueueEditPlanner.Build(saved, []);
        Assert.Empty(next.DeletedTaskNames);
        Assert.False(next.KeepEmpty);
        Assert.Single(next.CurrentTasks);
        saved.KeepEmpty = true;
        Assert.True(TaskQueueEditPlanner.Build(saved, []).KeepEmpty);
    }

    [Fact]
    public void PresetResetRebuildsHistoryAndPresetTogetherAndPreservesDuplicateParameters()
    {
        var saved = State(Task("Old"));
        saved.CurrentTasks.Add("Old<|||>old-entry");
        saved.DeletedTaskNames.Add("A");
        saved.MissingTaskSessions["Old"] = "launch-one";
        var next = TaskQueueEditPlanner.Build(saved, [Task("A", "first"), Task("A", "second")],
            resetHistory: true, setPreset: true, presetKey: "daily");
        Assert.Equal(new[] { "first", "second" }, next.Tasks.Select(task => task.Remark));
        Assert.Equal(new[] { "A<|||>EntryA" }, next.CurrentTasks);
        Assert.Empty(next.DeletedTaskNames);
        Assert.Empty(next.MissingTaskSessions);
        Assert.Equal("daily", next.PresetKey);
        Assert.True(next.HasPresetKey);
        var reset = TaskQueueEditPlanner.Build(next, [Task("A")], resetHistory: true, setPreset: true);
        Assert.True(reset.HasPresetKey);
        Assert.Null(reset.PresetKey);
    }

    [Fact]
    public void PlannerDetachesSelectionsAndDoesNotPublishEditsIntoSavedState()
    {
        var task = Task("A");
        task.Option = [new() { Name = "mode", Index = 1, SelectedCase = "B" }];
        var saved = State(task);
        var next = TaskQueueEditPlanner.Build(saved, [task]);
        next.Tasks[0].Option![0].SelectedCase = "C";
        next.Tasks[0].Remark = "edited";
        Assert.Equal("B", saved.Tasks[0].Option![0].SelectedCase);
        Assert.Null(saved.Tasks[0].Remark);
    }

    [Fact]
    public void BuiltInTaskNamesDoNotCreateOrBlockResourceDeletionIntent()
    {
        var builtIn = Task("A");
        builtIn.Entry = "CountdownAction";
        var saved = State(builtIn, Task("A"));
        var next = TaskQueueEditPlanner.Build(saved, [builtIn], deletedName: "A");
        Assert.Contains("A", next.DeletedTaskNames);
        var onlyBuiltIn = TaskQueueEditPlanner.Build(State(builtIn), [], deletedName: "A");
        Assert.Empty(onlyBuiltIn.DeletedTaskNames);
        var addedResource = TaskQueueEditPlanner.Build(next, [builtIn, Task("A")]);
        Assert.Empty(addedResource.DeletedTaskNames);
    }

    private static MaaInterface.MaaInterfaceTask Task(string name, string? remark = null) =>
        new() { Name = name, Entry = "Entry" + name, Remark = remark };

    private static TaskQueueState State(params MaaInterface.MaaInterfaceTask[] tasks) =>
        new() { IsInitialized = true, HasExplicitDeletionState = true, Tasks = tasks.ToList() };
}
