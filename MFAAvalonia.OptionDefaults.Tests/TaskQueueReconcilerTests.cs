using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public class TaskQueueReconcilerTests
{
    private static readonly HashSet<string> Specials = new() { "CountdownAction" };
    private static MaaInterface.MaaInterfaceTask Task(string name, string? entry = null, string? remark = null)
        => new() { Name = name, Entry = entry ?? name, Remark = remark, Check = false };
    private static MaaInterface Catalogue(params string[] names) => new() { Task = names.Select(name => Task(name)).ToList() };
    private static TaskQueueState Saved(params string[] names) => new()
    {
        IsInitialized = true, HasExplicitDeletionState = true,
        Tasks = names.Select(name => Task(name)).ToList(),
        CurrentTasks = names.Select(name => TaskQueueReconciler.HistoryKey(Task(name))).ToList()
    };
    private static TaskQueueReconciler.Result Reconcile(TaskQueueState state, MaaInterface catalog,
        string launch = "first", bool cleanup = true)
        => TaskQueueReconciler.Reconcile(state, catalog, launch, cleanup, Specials);

    [Theory]
    [InlineData("A,B", "A,N1,N2,B", "A,N1,N2,B")]
    [InlineData("C,A,B", "A,N1,N2,B,C", "C,A,N1,N2,B")]
    [InlineData("A,B,A,C", "A,N,B,C", "A,B,A,N,C")]
    [InlineData("B,A,B", "N,B,A", "N,B,A,B")]
    [InlineData("A,B", "B,N,A", "A,B,N")]
    public void InsertsBesideCurrentNeighboursWithoutReorderingExistingOccurrences(string before, string definitions, string after)
    {
        var result = Reconcile(Saved(before.Split(',')), Catalogue(definitions.Split(',')));
        Assert.Equal(after.Split(','), result.State.Tasks.Select(task => task.Name));
    }

    [Theory]
    [InlineData("A,B", "A,B,N", "A,N,B", "A,N,B")]
    [InlineData("B,A,C", "A,B,C,N", "A,N,B,C", "B,A,N,C")]
    [InlineData("A,A,B", "A,B,N", "A,N,A,B", "A,N,A,B")]
    [InlineData("A,A,B", "A,B,N", "A,A,N,B", "A,A,N,B")]
    [InlineData("A,A,B", "A,B,N", "N,A,A,B", "N,A,A,B")]
    [InlineData("A,B", "A,B,N", "A,A,N,B", "A,N,B")]
    [InlineData("A,X,B", "A,X,B,N", "A,X,A,N,B", "A,X,N,B")]
    [InlineData("A,B,A", "A,B,N", "A,N,B", "A,B,A,N")]
    [InlineData("A,B", "A,B,N", "A,N,B,N", "A,N,B")]
    [InlineData("A,B", "A,B,N2,N1", "A,N1,N2,B", "A,N1,N2,B")]
    [InlineData("A,B", "A,N,B", "N", "A,B,N")]
    public void InsertsBesidePresetOccurrencesWithoutReorderingSavedRows(
        string before, string definitions, string presetOrder, string after)
    {
        var state = Saved(before.Split(','));
        for (var index = 0; index < state.Tasks.Count; index++) state.Tasks[index].Remark = $"saved-{index}";
        state.PresetKey = "seed";
        var catalog = Catalogue(definitions.Split(','));
        catalog.Preset = [new()
        {
            Name = "seed",
            Task = presetOrder.Split(',').Select(name => new MaaInterface.MaaInterfacePresetTask { Name = name }).ToList()
        }];

        var result = Reconcile(state, catalog);

        Assert.Equal(after.Split(','), result.State.Tasks.Select(task => task.Name));
        Assert.Equal(state.Tasks.Select(task => task.Remark),
            result.State.Tasks.Where(task => task.Remark != null).Select(task => task.Remark));
        Assert.Equal(after.Split(','), Reconcile(result.State, catalog).State.Tasks.Select(task => task.Name));
    }

    [Fact]
    public void Mfabd2ToolPresetInsertsLastCatalogueTaskBetweenItsRealPresetNeighbours()
    {
        // Task/preset order from sunyink/MFABD2 assets/interface.json at 637c4c26 (2026-11-03).
        var catalog = Catalogue(
            "多存档", "[全局]启动脚本(置顶)", "[全局]启动脚本(置顶)PC", "[执行]快速狩猎扫荡",
            "[执行]地图采集[单章]", "[执行]装备制作&炼制日常", "[执行]白嫖抽卡", "[执行]竞技场",
            "[执行]赛季活动快速战斗", "[执行]肉鸽塔每日快速战斗", "[执行]周常任务", "[领取]日常任务",
            "[领取]任务页(每日/每周)", "[领取]活动页奖励", "[领取]通行证奖励", "[领取]领取邮件",
            "[执行]地图采集[完整]", "[执行]商店套利", "[半自动]自动钓鱼", "[全局]结束游戏",
            "[手动]生活技能刷级", "[特殊]半自动操作");
        string[] presetOrder = ["多存档", "[半自动]自动钓鱼", "[特殊]半自动操作", "[手动]生活技能刷级"];
        const string added = "[特殊]半自动操作";
        catalog.Preset = [new()
        {
            Name = "小工具",
            Task = presetOrder.Select(name => new MaaInterface.MaaInterfacePresetTask { Name = name }).ToList()
        }];
        var state = Saved(presetOrder.Where(name => name != added).ToArray());
        state.PresetKey = "小工具";

        var result = Reconcile(state, catalog);

        Assert.Equal(added, catalog.Task![^1].Name);
        Assert.Equal(presetOrder, result.State.Tasks.Select(task => task.Name));
    }

    [Fact]
    public void PresetOccurrenceCountingExcludesSpecialRowsAndMissingDefinitions()
    {
        var state = Saved("A", "X", "A", "B");
        state.Tasks.Insert(0, Task("A", "CountdownAction"));
        state.PresetKey = "seed";
        var catalog = Catalogue("A", "B", "N");
        catalog.Preset = [new()
        {
            Name = "seed",
            Task = [new() { Name = "A" }, new() { Name = "X" }, new() { Name = "N" }, new() { Name = "A" }, new() { Name = "B" }]
        }];

        var result = Reconcile(state, catalog);

        Assert.Equal(new[] { "A", "A", "N", "X", "A", "B" }, result.State.Tasks.Select(task => task.Name));
        Assert.Equal("CountdownAction", result.State.Tasks[0].Entry);
        Assert.Equal(new[] { "X" }, result.MissingNames);
    }

    [Fact]
    public void ChangedEntryPreservesBothCopiesAndTheirOwnSettingsWithoutMutatingInput()
    {
        var saved = Saved("B", "A", "A", "C");
        saved.Tasks[1].Remark = "one";
        saved.Tasks[2].Remark = "two";
        saved.Tasks[2].Check = true;
        var catalog = Catalogue("A", "B", "C");
        catalog.Task![0].Entry = "new-entry";
        var result = Reconcile(saved, catalog);
        Assert.Equal(new[] { "B", "A", "A", "C" }, result.State.Tasks.Select(task => task.Name));
        Assert.Equal("one", result.State.Tasks[1].Remark);
        Assert.Equal("two", result.State.Tasks[2].Remark);
        Assert.True(result.State.Tasks[2].Check);
        Assert.Equal("new-entry", result.State.Tasks[1].Entry);
        Assert.Equal("A", saved.Tasks[1].Entry);
    }

    [Fact]
    public void SharedEntryDoesNotTurnMissingTaskIntoAnotherDefinition()
    {
        var saved = Saved("X", "Y", "Z");
        saved.Tasks[0].Entry = saved.Tasks[1].Entry = "shared";
        var catalog = Catalogue("Y", "Z");
        catalog.Task![0].Entry = "shared";
        var result = Reconcile(saved, catalog);
        Assert.Equal(new[] { "X", "Y", "Z" }, result.State.Tasks.Select(task => task.Name));
        Assert.Equal(new[] { "X" }, result.MissingNames);
        Assert.Empty(result.State.DeletedTaskNames);
    }

    [Fact]
    public void MarkedTaskOnlyExpiresOnALaterStartupAndReturnsAsNewWithDefaults()
    {
        var saved = Saved("A", "B", "C");
        saved.Tasks[1].Remark = "old value";
        var marked = Reconcile(saved, Catalogue("A", "C"));
        Assert.Equal("first", marked.State.MissingTaskSessions["B"]);
        var refresh = Reconcile(marked.State, Catalogue("A", "C"), "second", false);
        Assert.Equal(3, refresh.State.Tasks.Count);
        var removed = Reconcile(marked.State, Catalogue("A", "C"), "second");
        Assert.Equal(new[] { "B" }, removed.RemovedNames);
        Assert.DoesNotContain(removed.State.CurrentTasks, key => TaskQueueReconciler.NameFromHistory(key) == "B");
        Assert.Empty(removed.State.DeletedTaskNames);
        var returned = Reconcile(removed.State, Catalogue("A", "B", "C"), "second", false);
        Assert.Equal(new[] { "A", "B", "C" }, returned.State.Tasks.Select(task => task.Name));
        Assert.Null(returned.State.Tasks[1].Remark);
    }

    [Fact]
    public void RestoringBeforeRemovalClearsMarkerAndRetainsParameter()
    {
        var saved = Saved("A", "B");
        saved.Tasks[1].Remark = "keep";
        var marked = Reconcile(saved, Catalogue("A"));
        var restored = Reconcile(marked.State, Catalogue("A", "B"), "second");
        Assert.Empty(restored.State.MissingTaskSessions);
        Assert.Equal("keep", restored.State.Tasks[1].Remark);
    }

    [Fact]
    public void CircuitBreakerDoesNotRecordNewMarkersOrConsumeExistingMarkers()
    {
        var saved = Saved("A", "B", "C", "D", "E", "F");
        saved.MissingTaskSessions["A"] = "previous";
        var result = Reconcile(saved, Catalogue("D", "E", "F"));
        Assert.True(result.CircuitBroken);
        Assert.Equal(6, result.State.Tasks.Count);
        Assert.Single(result.State.MissingTaskSessions);
        Assert.Equal("previous", result.State.MissingTaskSessions["A"]);
        Assert.Empty(result.RemovedNames);
    }

    [Fact]
    public void SmallQueuesAndCopiesOfOneMissingNameDoNotTripBreaker()
    {
        Assert.False(Reconcile(Saved("A", "B", "C"), Catalogue("C")).CircuitBroken);
        Assert.False(Reconcile(Saved("A", "A", "A", "A", "B"), Catalogue("B")).CircuitBroken);
    }

    [Fact]
    public void PresetFiltersNewSeedsAndUnsupportedRowsRemainInsertionAnchors()
    {
        var state = Saved("A", "C");
        state.PresetKey = "seed";
        var catalog = Catalogue("A", "B", "C", "D");
        catalog.Task![0].Controller = ["OtherController"];
        catalog.Preset = [new() { Name = "seed", Task = [new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" }] }];
        var result = Reconcile(state, catalog);
        Assert.Equal(new[] { "A", "B", "C" }, result.State.Tasks.Select(task => task.Name));
    }

    [Fact]
    public void MissingPresetStillSuppressesNewSeeds()
    {
        var state = Saved("A", "B");
        state.PresetKey = "missing";
        Assert.Equal(new[] { "A", "B" }, Reconcile(state, Catalogue("A", "N", "B")).State.Tasks.Select(task => task.Name));
    }

    [Fact]
    public void ExplicitEmptyAndDeletedNamesStaySuppressedButNewInstallationSeeds()
    {
        var empty = Saved();
        empty.KeepEmpty = true;
        Assert.Empty(Reconcile(empty, Catalogue("A")).State.Tasks);
        var deleted = Saved("A");
        deleted.DeletedTaskNames.Add("B");
        Assert.Single(Reconcile(deleted, Catalogue("A", "B")).State.Tasks);
        Assert.Equal(2, Reconcile(new(), Catalogue("A", "B")).State.Tasks.Count);
    }

    [Fact]
    public void LegacyDeletionImportPrecedesMissingCatalogueReconciliation()
    {
        var state = Saved("A", "B");
        state.HasExplicitDeletionState = false;
        state.CurrentTasks.Add("C<|||>C");
        var result = Reconcile(state, Catalogue("A", "C"));
        Assert.Equal(new[] { "C" }, result.State.DeletedTaskNames);
        Assert.Contains(result.State.Tasks, task => task.Name == "B");
    }

    [Fact]
    public void InvalidCatalogueIsRejectedBeforeInputCanChange()
    {
        var state = Saved("A");
        Assert.Throws<ArgumentException>(() => Reconcile(state, new()));
        Assert.Throws<ArgumentException>(() => Reconcile(state, Catalogue("A", "A")));
        Assert.Throws<ArgumentException>(() => Reconcile(state, new() { Task = [new() { Name = "A" }] }));
        Assert.Empty(state.MissingTaskSessions);
        Assert.Single(state.Tasks);
    }
}
