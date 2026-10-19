using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MFAAvalonia.Features.CombatStrategies;
using Xunit;

namespace MFAAvalonia.Tests;

public sealed class CombatStrategyEditorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "map-editor-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Cancel_KeepsCurrentDraftAndUnsavedChanges()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("未保存");
        var id = session.Current.Id;
        Assert.False(await session.TrySwitchAsync("default", () => Task.FromResult(UnsavedDecision.Cancel)));
        Assert.Equal(id, session.Current.Id);
        Assert.True(session.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveBeforeSwitch_ReloadsEditedDataFromDisk()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("保存策略");
        var id = session.Current.Id;
        session.Current.Stages[0].Name = "准备";
        session.MarkDirty();
        Assert.True(await session.TrySwitchAsync("default", () => Task.FromResult(UnsavedDecision.Save)));
        Assert.False(session.HasUnsavedChanges);
        session.Select(id);
        Assert.Equal("准备", session.Current.Stages[0].Name);
    }

    [Fact]
    public async Task Discard_RestoresSavedDocumentAndRemovesNewDraft()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("策略");
        session.Save();
        session.Current.Name = "不应保留";
        session.MarkDirty();
        Assert.True(await session.TryLeaveAsync(() => Task.FromResult(UnsavedDecision.Discard)));
        Assert.Equal("策略", session.Current.Name);
        session.Create("临时策略");
        var unsavedId = session.Current.Id;
        Assert.True(await session.TryLeaveAsync(() => Task.FromResult(UnsavedDecision.Discard)));
        Assert.DoesNotContain(session.Catalog.Strategies, x => x.Id == unsavedId);
    }

    [Fact]
    public async Task SaveFailure_DoesNotSwitchOrClearDirtyState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "阻止创建策略目录");
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("保存失败");
        var id = session.Current.Id;
        Assert.False(await session.TrySwitchAsync("default", () => Task.FromResult(UnsavedDecision.Save)));
        Assert.Equal(id, session.Current.Id);
        Assert.True(session.HasUnsavedChanges);
        Assert.False(string.IsNullOrWhiteSpace(session.Error));
    }

    [Fact]
    public void CopyStrategy_RemapTargetsAndKeepOriginalIndependent()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("来源");
        var first = session.Current.Stages[0];
        var second = session.AddStage();
        first.Transitions.Add(new StageTransition
        {
            TargetStageId = second.Id,
            Condition = new ConditionGroup { Children = [new ConditionPredicate { Metric = ConditionMetric.FrenzyActive, Expected = true }] }
        });
        session.Save();
        var sourceId = session.Current.Id;
        var oldStageIds = session.Current.Stages.Select(x => x.Id).ToArray();
        session.Duplicate();
        Assert.NotEqual(sourceId, session.Current.Id);
        Assert.DoesNotContain(session.Current.Stages[0].Id, oldStageIds);
        Assert.Equal(session.Current.Stages[1].Id, session.Current.Stages[0].Transitions[0].TargetStageId);
        Assert.Equal(session.Current.Stages[0].Id, session.Current.InitialStageId);
        session.Current.Stages[0].Name = "修改副本";
        Assert.NotEqual("修改副本", new StrategyStore(_directory).Load(sourceId).Stages[0].Name);
    }

    [Fact]
    public void StageDelete_ProtectsReferencesAndInitialStage()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        session.Create("引用");
        var first = session.Current.Stages[0];
        var second = session.AddStage();
        first.Transitions.Add(new StageTransition { TargetStageId = second.Id });
        Assert.Throws<InvalidOperationException>(() => session.DeleteStage(first));
        Assert.Throws<InvalidOperationException>(() => session.DeleteStage(second));
        first.Transitions.Clear();
        session.DeleteStage(second);
        Assert.Single(session.Current.Stages);
    }

    [Fact]
    public void DefaultCannotBeEditedButCanBeCopied()
    {
        var session = new StrategyEditorSession(new StrategyStore(_directory));
        Assert.True(session.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => session.AddStage());
        session.Duplicate();
        Assert.False(session.IsReadOnly);
        Assert.True(session.HasUnsavedChanges);
        session.Current.Stages[0].Name = "自定义";
        Assert.Equal("全程作战", StrategyDefaults.Create().Stages[0].Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        if (File.Exists(_directory)) File.Delete(_directory);
    }
}
