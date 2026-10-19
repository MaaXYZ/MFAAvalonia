using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MFAAvalonia.Features.CombatStrategies;

public enum StrategyAction { Ultimate, Bomb }
public enum GhoulColor { Red, Blue, Green, Yellow }
public enum ConditionMatch { All, Any }
public enum ConditionMetric { BattleNormalLinks, StageNormalLinks, EnemyHp, AllyHp, EnemyHpPercent, AllyHpPercent, UltimateReady, UltimateReleased, FrenzyActive }
public enum ComparisonOperator { GreaterOrEqual, LessOrEqual, Greater, Less, Equal, NotEqual }

public sealed class StrategyDocument
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Name { get; set; } = "未命名策略";
    [JsonRequired] public string InitialStageId { get; set; } = "";
    [JsonRequired] public List<StrategyStage> Stages { get; set; } = [];
}

public sealed class StrategyStage
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Name { get; set; } = "新阶段";
    [JsonRequired] public StageActions Actions { get; set; } = new();
    [JsonRequired] public FrenzySettings Frenzy { get; set; } = new();
    [JsonRequired] public List<StageTransition> Transitions { get; set; } = [];
}

public sealed class StageActions
{
    [JsonRequired] public List<StrategyAction> Priority { get; set; } = [StrategyAction.Ultimate, StrategyAction.Bomb];
    [JsonRequired] public List<int> BombLevels { get; set; } = [3];
    [JsonRequired] public List<UltimateRule> Ultimates { get; set; } = [new() { Slot = 1 }, new() { Slot = 2 }, new() { Slot = 3 }];
    [JsonRequired] public NormalLinkSettings NormalLink { get; set; } = new();
}

public sealed class UltimateRule
{
    [JsonRequired] public int Slot { get; set; }
    [JsonRequired] public bool Enabled { get; set; } = true;
    public StrategyCondition? Condition { get; set; }
}

public sealed class NormalLinkSettings
{
    [JsonRequired] public bool PrioritizeColors { get; set; }
    [JsonRequired] public int LongLinkThreshold { get; set; } = 7;
    [JsonRequired] public List<GhoulColor> ColorPriority { get; set; } = [GhoulColor.Red, GhoulColor.Blue, GhoulColor.Green, GhoulColor.Yellow];
}

public sealed class FrenzySettings
{
    [JsonRequired] public bool AllowUltimates { get; set; } = true;
    [JsonRequired] public bool AllowBombs { get; set; } = true;
    public GhoulColor? ReservedColor { get; set; }
}

public sealed class StageTransition
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string TargetStageId { get; set; } = "";
    [JsonRequired] public StrategyCondition Condition { get; set; } = new ConditionGroup();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ConditionPredicate), "predicate")]
public abstract class StrategyCondition { }

public sealed class ConditionGroup : StrategyCondition
{
    [JsonRequired] public ConditionMatch Match { get; set; } = ConditionMatch.All;
    [JsonRequired] public List<StrategyCondition> Children { get; set; } = [];
}

public sealed class ConditionPredicate : StrategyCondition
{
    [JsonRequired] public ConditionMetric Metric { get; set; } = ConditionMetric.StageNormalLinks;
    public ComparisonOperator? Comparison { get; set; }
    public decimal? Value { get; set; }
    public List<int>? Slots { get; set; }
    public ConditionMatch? SlotMatch { get; set; }
    public bool? Expected { get; set; }
}

public static class StrategyDefaults
{
    public static StrategyDocument Create()
    {
        var result = CreateEditable("默认策略");
        result.Id = "default";
        return result;
    }

    public static StrategyDocument CreateEditable(string name)
    {
        var stage = new StrategyStage { Name = "全程作战" };
        return new StrategyDocument { Name = name, InitialStageId = stage.Id, Stages = [stage] };
    }
}

public sealed record StrategySummary(string Id, string Name, bool IsBuiltIn = false)
{
    public override string ToString() => Name;
}

public sealed record StrategyCatalog(IReadOnlyList<StrategySummary> Strategies, IReadOnlyList<string> Errors);

public sealed class StrategyValidationException(IReadOnlyList<string> errors) : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
