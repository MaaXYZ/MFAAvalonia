using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MFAAvalonia.Features.CombatStrategies;
using Xunit;

namespace MFAAvalonia.Tests;

public sealed class CombatStrategyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "map-strategy-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveLoad_PreservesNestedConditionsAndStageTargets()
    {
        var store = new StrategyStore(_directory);
        var strategy = StrategyDefaults.CreateEditable("循环策略");
        var first = strategy.Stages[0];
        var second = new StrategyStage { Name = "乱舞" };
        strategy.Stages.Add(second);
        first.Actions.Ultimates = [new() { Slot = 2 }, new() { Slot = 1 }, new() { Slot = 3 }];
        first.Actions.Ultimates[2].Condition = new ConditionGroup
        {
            Children =
            [
                new ConditionPredicate { Metric = ConditionMetric.StageNormalLinks, Comparison = ComparisonOperator.GreaterOrEqual, Value = 2 },
                new ConditionGroup
                {
                    Match = ConditionMatch.Any,
                    Children =
                    [
                        new ConditionPredicate { Metric = ConditionMetric.UltimateReleased, Slots = [1], SlotMatch = ConditionMatch.All, Expected = true },
                        new ConditionPredicate { Metric = ConditionMetric.UltimateReleased, Slots = [2], SlotMatch = ConditionMatch.All, Expected = true }
                    ]
                }
            ]
        };
        first.Transitions.Add(new StageTransition
        {
            TargetStageId = second.Id,
            Condition = new ConditionGroup
            {
                Children = [new ConditionPredicate { Metric = ConditionMetric.FrenzyActive, Expected = true }]
            }
        });
        store.Save(strategy);
        var saved = File.ReadAllText(Path.Combine(_directory, strategy.Id + ".json"));
        using var json = JsonDocument.Parse(saved);
        Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("循环策略", store.Load(strategy.Id).Name);
        var loaded = store.Load(strategy.Id);
        Assert.Equal([2, 1, 3], loaded.Stages[0].Actions.Ultimates.Select(x => x.Slot));
        Assert.Equal(second.Id, loaded.Stages[0].Transitions[0].TargetStageId);
        var condition = Assert.IsType<ConditionGroup>(loaded.Stages[0].Actions.Ultimates[2].Condition);
        Assert.Equal(2, Assert.IsType<ConditionPredicate>(condition.Children[0]).Value);
        Assert.Equal(ConditionMatch.Any, Assert.IsType<ConditionGroup>(condition.Children[1]).Match);
    }

    [Fact]
    public void Save_InvalidReferenceDoesNotOverwriteExistingFile()
    {
        var store = new StrategyStore(_directory);
        var strategy = StrategyDefaults.CreateEditable("原策略");
        store.Save(strategy);
        var original = File.ReadAllText(Path.Combine(_directory, strategy.Id + ".json"));
        strategy.Stages[0].Transitions.Add(new StageTransition
        {
            TargetStageId = Guid.NewGuid().ToString("N"),
            Condition = new ConditionGroup { Children = [new ConditionPredicate { Metric = ConditionMetric.BattleNormalLinks, Comparison = ComparisonOperator.GreaterOrEqual, Value = 8 }] }
        });
        Assert.Throws<StrategyValidationException>(() => store.Save(strategy));
        Assert.Equal(original, File.ReadAllText(Path.Combine(_directory, strategy.Id + ".json")));
    }

    [Fact]
    public void List_ReportsCorruptAndFutureFilesWithoutOverwritingThem()
    {
        Directory.CreateDirectory(_directory);
        var corruptId = Guid.NewGuid().ToString("N");
        var future = StrategyDefaults.CreateEditable("未来策略");
        File.WriteAllText(Path.Combine(_directory, corruptId + ".json"), "{破损");
        var json = StrategyJson.Serialize(future).Replace("\"schema_version\": 1", "\"schema_version\": 99");
        File.WriteAllText(Path.Combine(_directory, future.Id + ".json"), json);
        var catalog = new StrategyStore(_directory).List();
        Assert.Single(catalog.Strategies);
        Assert.Equal("default", catalog.Strategies[0].Id);
        Assert.Equal(2, catalog.Errors.Count);
        Assert.Equal("{破损", File.ReadAllText(Path.Combine(_directory, corruptId + ".json")));
        Assert.Throws<StrategyValidationException>(() => new StrategyStore(_directory).Load(future.Id));
    }

    [Fact]
    public void Store_ProtectsDefaultAndRejectsPathTraversal()
    {
        var store = new StrategyStore(_directory);
        Assert.Equal("default", store.Load("default").Id);
        Assert.Throws<InvalidOperationException>(() => store.Save(StrategyDefaults.Create()));
        Assert.Throws<InvalidOperationException>(() => store.Delete("default"));
        Assert.Throws<ArgumentException>(() => store.Load("../outside"));
        Assert.Throws<ArgumentException>(() => store.Delete("../outside"));
    }

    [Fact]
    public void DocumentationExample_LoadsNestedSlotDependencies()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "docs", "zh"))) root = root.Parent;
        Assert.NotNull(root);
        var document = StrategyJson.Deserialize(File.ReadAllText(Path.Combine(root.FullName, "docs", "examples", "combat-strategy-v1.json")));
        Assert.Equal(new[] { 2, 1, 3 }, document.Stages[0].Actions.Ultimates.Select(x => x.Slot));
        var condition = Assert.IsType<ConditionGroup>(document.Stages[0].Actions.Ultimates[2].Condition);
        Assert.Equal(2, Assert.IsType<ConditionPredicate>(condition.Children[0]).Value);
        Assert.Equal(ConditionMatch.Any, Assert.IsType<ConditionGroup>(condition.Children[1]).Match);
    }

    [Theory]
    [InlineData("actions")]
    [InlineData("frenzy")]
    [InlineData("transitions")]
    public void Deserialize_RejectsMissingRequiredStageConfiguration(string field)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(StrategyJson.Serialize(StrategyDefaults.CreateEditable("完整性")))!;
        node["stages"]![0]!.AsObject().Remove(field);
        Assert.Throws<StrategyValidationException>(() => StrategyJson.Deserialize(node.ToJsonString()));
    }

    [Theory]
    [InlineData(ConditionMetric.EnemyHpPercent, -1)]
    [InlineData(ConditionMetric.AllyHpPercent, 101)]
    [InlineData(ConditionMetric.StageNormalLinks, 1.5)]
    public void Validate_RejectsInvalidNumericConditions(ConditionMetric metric, double value)
    {
        var strategy = StrategyDefaults.CreateEditable("数值测试");
        strategy.Stages[0].Actions.Ultimates[0].Condition = new ConditionGroup
        {
            Children = [new ConditionPredicate { Metric = metric, Comparison = ComparisonOperator.LessOrEqual, Value = (decimal)value }]
        };
        Assert.NotEmpty(StrategyValidator.Validate(strategy));
    }

    [Fact]
    public void Validate_RejectsEmptyTransitionAndOwnReadinessCondition()
    {
        var strategy = StrategyDefaults.CreateEditable("条件测试");
        strategy.Stages[0].Actions.Ultimates[0].Condition = new ConditionGroup
        {
            Children = [new ConditionPredicate { Metric = ConditionMetric.UltimateReady, Slots = [1], SlotMatch = ConditionMatch.All, Expected = true }]
        };
        strategy.Stages[0].Transitions.Add(new StageTransition { TargetStageId = strategy.Stages[0].Id });
        Assert.True(StrategyValidator.Validate(strategy).Count >= 2);
    }

    [Fact]
    public void Clone_DoesNotShareMutableConditions()
    {
        var original = StrategyDefaults.CreateEditable("复制来源");
        original.Stages[0].Actions.Ultimates[0].Condition = new ConditionGroup
        {
            Children = [new ConditionPredicate { Metric = ConditionMetric.EnemyHp, Comparison = ComparisonOperator.LessOrEqual, Value = 10000 }]
        };
        var clone = StrategyJson.Clone(original);
        var clonedGroup = Assert.IsType<ConditionGroup>(clone.Stages[0].Actions.Ultimates[0].Condition);
        Assert.IsType<ConditionPredicate>(clonedGroup.Children[0]).Value = 2000;
        var originalGroup = Assert.IsType<ConditionGroup>(original.Stages[0].Actions.Ultimates[0].Condition);
        Assert.Equal(10000, Assert.IsType<ConditionPredicate>(originalGroup.Children[0]).Value);
    }

    [Fact]
    public void Validate_RejectsLeafAsRootCondition()
    {
        var strategy = StrategyDefaults.CreateEditable("错误根节点");
        strategy.Stages[0].Actions.Ultimates[0].Condition = new ConditionPredicate
        {
            Metric = ConditionMetric.EnemyHp, Comparison = ComparisonOperator.LessOrEqual, Value = 10000
        };
        Assert.NotEmpty(StrategyValidator.Validate(strategy));
    }

    [Fact]
    public void Validate_RejectsExtraNullUltimate()
    {
        var strategy = StrategyDefaults.CreateEditable("空位置");
        strategy.Stages[0].Actions.Ultimates.Add(null!);
        Assert.NotEmpty(StrategyValidator.Validate(strategy));
    }

    [Fact]
    public void List_ReportsFileWithReservedDefaultName()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "default.json"), "{}");
        var catalog = new StrategyStore(_directory).List();
        Assert.Single(catalog.Strategies);
        Assert.Single(catalog.Errors);
    }

    [Fact]
    public void Save_LockedDestinationKeepsOldFileAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new StrategyStore(_directory);
        var strategy = StrategyDefaults.CreateEditable("原文件");
        store.Save(strategy);
        var path = Path.Combine(_directory, strategy.Id + ".json");
        var original = File.ReadAllText(path);
        strategy.Name = "不应写入";
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => store.Save(strategy));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }
}
