using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Features.CombatStrategies;

public static class StrategyValidator
{
    public const int MaxConditionDepth = 16;

    public static void EnsureValid(StrategyDocument document)
    {
        var errors = Validate(document);
        if (errors.Count != 0)
            throw new StrategyValidationException(errors);
    }

    public static IReadOnlyList<string> Validate(StrategyDocument document)
    {
        var errors = new List<string>();
        if (document.SchemaVersion != 1) errors.Add($"不支持策略格式版本 {document.SchemaVersion}。");
        if (document.Id != "default" && !IsId(document.Id)) errors.Add("策略标识无效。");
        if (string.IsNullOrWhiteSpace(document.Name)) errors.Add("请填写策略名称。");
        if (document.Stages == null || document.Stages.Count == 0)
        {
            errors.Add("策略至少需要一个阶段。");
            return errors;
        }
        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in document.Stages)
        {
            if (stage == null || !IsId(stage.Id) || !stageIds.Add(stage.Id))
                errors.Add("阶段标识无效或重复。");
        }
        if (!stageIds.Contains(document.InitialStageId ?? "")) errors.Add("请选择有效的初始阶段。");
        var transitionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in document.Stages.Where(x => x != null))
        {
            var prefix = $"阶段“{stage.Name}”";
            if (string.IsNullOrWhiteSpace(stage.Name)) errors.Add("请填写阶段名称。");
            if (stage.Actions == null || stage.Frenzy == null || stage.Transitions == null)
            {
                errors.Add(prefix + "的配置不完整。");
                continue;
            }
            var actions = stage.Actions;
            if (!IsPermutation(actions.Priority, Enum.GetValues<StrategyAction>()))
                errors.Add(prefix + "的行动优先级必须完整包含奥义和炸弹。");
            if (actions.BombLevels == null || actions.BombLevels.Distinct().Count() != actions.BombLevels.Count || actions.BombLevels.Any(x => x < 1 || x > 3))
                errors.Add(prefix + "的炸弹等级无效或重复。");
            if (actions.NormalLink == null || actions.NormalLink.LongLinkThreshold != 7 || !IsPermutation(actions.NormalLink.ColorPriority, Enum.GetValues<GhoulColor>()))
                errors.Add(prefix + "的连线颜色配置无效。");
            if (stage.Frenzy.ReservedColor is { } color && !Enum.IsDefined(color)) errors.Add(prefix + "的乱舞保留颜色无效。");
            if (actions.Ultimates == null || actions.Ultimates.Any(x => x == null) || !IsPermutation(actions.Ultimates.Where(x => x != null).Select(x => x.Slot), new[] { 1, 2, 3 }))
                errors.Add(prefix + "必须包含三个不重复的奥义位置。");
            if (actions.Ultimates != null)
                foreach (var ultimate in actions.Ultimates.Where(x => x != null))
                    if (ultimate.Condition != null)
                    {
                        if (ultimate.Condition is not ConditionGroup) errors.Add(prefix + "的奥义前置根节点必须是条件组。");
                        ValidateCondition(ultimate.Condition, errors, prefix + $"的位置 {ultimate.Slot}", 0, ultimate.Slot, allowEmptyRoot: true);
                    }
            foreach (var transition in stage.Transitions)
            {
                if (transition == null) { errors.Add(prefix + "存在空转变规则。"); continue; }
                if (!IsId(transition.Id) || !transitionIds.Add(transition.Id)) errors.Add(prefix + "的转变规则标识无效或重复。");
                if (!stageIds.Contains(transition.TargetStageId ?? "")) errors.Add(prefix + "的转变目标不存在。");
                if (transition.Condition is not ConditionGroup) errors.Add(prefix + "的转变条件根节点必须是条件组。");
                ValidateCondition(transition.Condition, errors, prefix + "的转变条件", 0, null, false);
            }
        }
        return errors;
    }

    private static bool IsPermutation<T>(IEnumerable<T>? actual, IEnumerable<T> expected) =>
        actual != null && actual.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x));

    public static bool IsId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _);

    private static void ValidateCondition(StrategyCondition? condition, List<string> errors, string prefix, int depth, int? ownerSlot, bool allowEmptyRoot)
    {
        if (depth >= MaxConditionDepth) { errors.Add(prefix + "的条件嵌套过深。"); return; }
        if (condition is ConditionGroup group)
        {
            if (!Enum.IsDefined(group.Match)) errors.Add(prefix + "的条件组合无效。");
            if (group.Children == null || (group.Children.Count == 0 && !(depth == 0 && allowEmptyRoot)))
            { errors.Add(prefix + "存在空条件组，请添加条件或删除空组。"); return; }
            foreach (var child in group.Children)
                ValidateCondition(child, errors, prefix, depth + 1, ownerSlot, false);
            return;
        }
        if (condition is not ConditionPredicate predicate || !Enum.IsDefined(predicate.Metric))
        { errors.Add(prefix + "存在未知条件。"); return; }
        if (predicate.Metric is ConditionMetric.UltimateReady or ConditionMetric.UltimateReleased)
        {
            if (predicate.Slots == null || predicate.Slots.Count == 0 || predicate.Slots.Distinct().Count() != predicate.Slots.Count || predicate.Slots.Any(x => x < 1 || x > 3))
                errors.Add(prefix + "请选择有效的奥义位置。");
            if (predicate.SlotMatch is not { } match || !Enum.IsDefined(match) || predicate.Expected == null)
                errors.Add(prefix + "的奥义状态条件不完整。");
            if (predicate.Metric == ConditionMetric.UltimateReady && ownerSlot != null && predicate.Slots?.Contains(ownerSlot.Value) == true)
                errors.Add(prefix + "的自身就绪是内置条件，请只选择其他位置。");
            if (predicate.Value != null || predicate.Comparison != null) errors.Add(prefix + "的奥义状态不能包含数值比较。");
        }
        else if (predicate.Metric == ConditionMetric.FrenzyActive)
        {
            if (predicate.Expected == null || predicate.Value != null || predicate.Comparison != null || predicate.Slots != null || predicate.SlotMatch != null)
                errors.Add(prefix + "的乱舞状态条件无效。");
        }
        else
        {
            if (predicate.Value is not { } value || value < 0 || predicate.Comparison is not { } comparison || !Enum.IsDefined(comparison))
                errors.Add(prefix + "请填写有效的非负数值和比较方式。");
            else
            {
                if (predicate.Metric is ConditionMetric.AllyHpPercent or ConditionMetric.EnemyHpPercent && value > 100)
                    errors.Add(prefix + "的血量百分比必须在 0 到 100 之间。");
                if (predicate.Metric is ConditionMetric.BattleNormalLinks or ConditionMetric.StageNormalLinks && decimal.Truncate(value) != value)
                    errors.Add(prefix + "的普通连线手数必须是整数。");
            }
            if (predicate.Expected != null || predicate.Slots != null || predicate.SlotMatch != null)
                errors.Add(prefix + "的数值条件不能包含奥义位置或布尔状态。");
        }
    }
}
