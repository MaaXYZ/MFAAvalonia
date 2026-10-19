using Avalonia;
using Avalonia.Controls;
using System;
using System.Linq;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

public sealed class StrategyConditionEditor : UserControl
{
    private readonly Action _changed;
    private readonly int? _ownerSlot;

    public StrategyConditionEditor(ConditionGroup root, Action changed, int? ownerSlot = null)
    {
        _changed = changed;
        _ownerSlot = ownerSlot;
        Content = BuildGroup(root, null, 0);
    }

    private Control BuildGroup(ConditionGroup group, Action? remove, int depth)
    {
        var children = new StackPanel { Spacing = 8 };
        var mode = StrategyUi.Choice(StrategyUi.Matches, group.Match, value => { group.Match = value; _changed(); });
        mode.Name = "ConditionMatch";
        var addCondition = StrategyUi.Button("添加条件", () =>
        {
            group.Children.Add(NewPredicate());
            RefreshChildren();
            _changed();
        });
        addCondition.Name = "AddCondition";
        var addGroup = StrategyUi.Button("添加条件组", () =>
        {
            group.Children.Add(new ConditionGroup { Children = [NewPredicate()] });
            RefreshChildren();
            _changed();
        });
        addGroup.Name = "AddConditionGroup";
        addGroup.IsEnabled = depth < StrategyValidator.MaxConditionDepth - 2;
        var header = StrategyUi.Row(mode, addCondition, addGroup);
        if (remove != null) header.Children.Add(StrategyUi.Button("移除组", remove));
        var body = StrategyUi.Column(header, children);
        var card = StrategyUi.Card(body, "strategy-condition");
        card.Margin = new Thickness(depth == 0 ? 0 : 8, 2, 0, 2);
        RefreshChildren();
        return card;

        void RefreshChildren()
        {
            children.Children.Clear();
            if (group.Children.Count == 0)
                children.Children.Add(StrategyUi.Text("尚未添加条件"));
            foreach (var condition in group.Children.ToArray())
            {
                void Remove()
                {
                    group.Children.Remove(condition);
                    RefreshChildren();
                    _changed();
                }
                children.Children.Add(condition is ConditionGroup nested
                    ? BuildGroup(nested, Remove, depth + 1)
                    : BuildPredicate((ConditionPredicate)condition, Remove));
            }
        }
    }

    private Control BuildPredicate(ConditionPredicate predicate, Action remove)
    {
        var host = new StackPanel { Spacing = 4 };
        Render();
        return host;

        void Render()
        {
            host.Children.Clear();
            var metric = StrategyUi.Choice(Metrics, predicate.Metric, value =>
            {
                predicate.Metric = value;
                predicate.Comparison = null;
                predicate.Value = null;
                predicate.Slots = null;
                predicate.SlotMatch = null;
                predicate.Expected = null;
                if (value is ConditionMetric.UltimateReady or ConditionMetric.UltimateReleased)
                {
                    predicate.Slots = [Enumerable.Range(1, 3).First(x => value != ConditionMetric.UltimateReady || x != _ownerSlot)];
                    predicate.SlotMatch = ConditionMatch.All;
                    predicate.Expected = true;
                }
                else if (value == ConditionMetric.FrenzyActive) predicate.Expected = true;
                else { predicate.Comparison = ComparisonOperator.GreaterOrEqual; predicate.Value = 1; }
                _changed();
                Render();
            }, 180);
            metric.Name = "ConditionMetric";
            var row = StrategyUi.Row(metric);
            if (predicate.Metric is ConditionMetric.UltimateReady or ConditionMetric.UltimateReleased)
            {
                foreach (var slot in Enumerable.Range(1, 3))
                {
                    if (predicate.Metric == ConditionMetric.UltimateReady && slot == _ownerSlot) continue;
                    row.Children.Add(StrategyUi.Check($"位置 {slot}", predicate.Slots?.Contains(slot) == true, selected =>
                    {
                        predicate.Slots ??= [];
                        if (selected && !predicate.Slots.Contains(slot)) predicate.Slots.Add(slot);
                        if (!selected) predicate.Slots.Remove(slot);
                        predicate.Slots.Sort();
                        _changed();
                    }));
                }
                row.Children.Add(StrategyUi.Choice(StrategyUi.Matches, predicate.SlotMatch ?? ConditionMatch.All, value => { predicate.SlotMatch = value; _changed(); }, 120));
                var ready = predicate.Metric == ConditionMetric.UltimateReady;
                row.Children.Add(StrategyUi.Choice(
                    new[] { new EditorChoice<bool>(true, ready ? "已就绪" : "已释放"), new EditorChoice<bool>(false, ready ? "未就绪" : "未释放") },
                    predicate.Expected ?? true, value => { predicate.Expected = value; _changed(); }, 100));
            }
            else if (predicate.Metric == ConditionMetric.FrenzyActive)
                row.Children.Add(StrategyUi.Choice(
                    new[] { new EditorChoice<bool>(true, "处于乱舞"), new EditorChoice<bool>(false, "不处于乱舞") },
                    predicate.Expected ?? true, value => { predicate.Expected = value; _changed(); }));
            else
            {
                row.Children.Add(StrategyUi.Choice(Comparisons, predicate.Comparison ?? ComparisonOperator.GreaterOrEqual, value => { predicate.Comparison = value; _changed(); }, 85));
                var percent = predicate.Metric is ConditionMetric.EnemyHpPercent or ConditionMetric.AllyHpPercent;
                var number = new NumericUpDown
                {
                    Name = "ConditionValue", Value = predicate.Value, Minimum = 0,
                    Maximum = percent ? 100 : 999999999999m, Increment = 1, Width = 150,
                    FormatString = percent ? "0.##" : "0.##"
                };
                number.PropertyChanged += (_, e) =>
                {
                    if (e.Property != NumericUpDown.ValueProperty) return;
                    predicate.Value = number.Value;
                    _changed();
                };
                row.Children.Add(number);
                if (percent) row.Children.Add(StrategyUi.Text("%"));
            }
            row.Children.Add(StrategyUi.Button("移除", remove));
            host.Children.Add(row);
        }
    }

    private static ConditionPredicate NewPredicate() => new()
    {
        Metric = ConditionMetric.StageNormalLinks,
        Comparison = ComparisonOperator.GreaterOrEqual,
        Value = 1
    };

    private static readonly EditorChoice<ConditionMetric>[] Metrics =
    [
        new(ConditionMetric.BattleNormalLinks, "本场普通连线手数"),
        new(ConditionMetric.StageNormalLinks, "当前阶段普通连线手数"),
        new(ConditionMetric.EnemyHp, "敌方血量数值"),
        new(ConditionMetric.AllyHp, "我方血量数值"),
        new(ConditionMetric.EnemyHpPercent, "敌方血量百分比"),
        new(ConditionMetric.AllyHpPercent, "我方血量百分比"),
        new(ConditionMetric.UltimateReady, "其他位置奥义就绪"),
        new(ConditionMetric.UltimateReleased, "位置奥义已释放"),
        new(ConditionMetric.FrenzyActive, "乱舞状态")
    ];

    private static readonly EditorChoice<ComparisonOperator>[] Comparisons =
    [
        new(ComparisonOperator.GreaterOrEqual, "≥"), new(ComparisonOperator.LessOrEqual, "≤"),
        new(ComparisonOperator.Greater, ">"), new(ComparisonOperator.Less, "<"),
        new(ComparisonOperator.Equal, "="), new(ComparisonOperator.NotEqual, "≠")
    ];
}
