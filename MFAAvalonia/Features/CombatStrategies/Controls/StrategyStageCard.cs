using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

public sealed class StrategyStageCard : UserControl
{
    private readonly StrategyEditorSession _session;
    private readonly StrategyStage _stage;
    private readonly StackPanel _body = new() { Spacing = 8 };
    private readonly Button _title;
    private readonly HashSet<int> _openSlots = [];
    private readonly HashSet<string> _openRules = [];
    public Control DragHandle { get; }
    public StrategyStage Stage => _stage;
    public bool IsExpanded
    {
        get => _body.IsVisible;
        set { _body.IsVisible = value; _title.Content = (_session.Current.InitialStageId == _stage.Id ? "初始阶段  " : "") + _stage.Name + (value ? "  ▾" : "  ▸"); }
    }

    public StrategyStageCard(StrategyEditorSession session, StrategyStage stage, Action<string> showError, Func<string, string, Task<string?>> rename)
    {
        _session = session; _stage = stage;
        DragHandle = StrategyUi.DragHandle();
        DragHandle.IsEnabled = !session.IsReadOnly;
        _title = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 15 };
        _title.Click += (_, _) => IsExpanded = !IsExpanded;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        header.Children.Add(DragHandle); Grid.SetColumn(_title, 1); header.Children.Add(_title);
        var renameItem = new MenuItem { Header = "重命名", IsEnabled = !session.IsReadOnly };
        renameItem.Click += async (_, _) => { var name = await rename("重命名阶段", stage.Name); if (name != null) { stage.Name = name; session.StructuralChange(); } };
        var copy = new MenuItem { Header = "复制阶段", IsEnabled = !session.IsReadOnly };
        copy.Click += (_, _) => session.CopyStage(stage);
        var initial = new MenuItem { Header = "设为初始阶段", IsEnabled = !session.IsReadOnly && session.Current.InitialStageId != stage.Id };
        initial.Click += (_, _) => session.SetInitialStage(stage);
        var delete = new MenuItem { Header = "删除阶段", IsEnabled = !session.IsReadOnly };
        delete.Click += (_, _) => { try { session.DeleteStage(stage); } catch (InvalidOperationException ex) { showError(ex.Message); } };
        header.ContextMenu = new ContextMenu { Items = { renameItem, copy, initial, delete } };
        _body.IsEnabled = !session.IsReadOnly;
        _body.Margin = new Thickness(8, 2, 8, 6);
        AddSection("行动优先级", BuildPriority());
        _body.Children.Add(StrategyUi.Text("连线固定最后；普通操作与乱舞共用此顺序。"));
        var bombs = StrategyUi.Row();
        foreach (var level in new[] { 1, 2, 3 })
            bombs.Children.Add(StrategyUi.Check($"{level} 级", stage.Actions.BombLevels.Contains(level), value =>
            {
                if (value && !stage.Actions.BombLevels.Contains(level)) stage.Actions.BombLevels.Add(level);
                if (!value) stage.Actions.BombLevels.Remove(level);
                stage.Actions.BombLevels.Sort(); session.MarkDirty();
            }));
        AddSection("炸弹", bombs);
        AddSection("奥义", BuildUltimates());
        AddSection("普通连线", BuildNormalLinks());
        AddSection("乱舞", BuildFrenzy());
        AddSection("阶段转变", BuildTransitions());
        Content = StrategyUi.Card(StrategyUi.Column(header, _body));
        IsExpanded = true;
    }

    private void AddSection(string name, Control content) { _body.Children.Add(StrategyUi.Section(name)); _body.Children.Add(content); }

    private Control BuildPriority()
    {
        var list = new StackPanel { Spacing = 4 };
        Render(); return list;
        void Render()
        {
            list.Children.Clear();
            foreach (var action in _stage.Actions.Priority)
            {
                var handle = StrategyUi.DragHandle();
                var row = StrategyUi.Row(handle, StrategyUi.Text(action == StrategyAction.Ultimate ? "使用奥义" : "使用炸弹"));
                list.Children.Add(row);
                StrategyReorder.Attach(handle, row, list, _stage.Actions.Priority, action, () => { _session.MarkDirty(); Render(); });
            }
        }
    }

    private Control BuildUltimates()
    {
        var list = new StackPanel { Spacing = 8 };
        Render(); return list;
        void Render()
        {
            list.Children.Clear();
            foreach (var ultimate in _stage.Actions.Ultimates)
            {
                var handle = StrategyUi.DragHandle();
                var details = new StackPanel { IsVisible = _openSlots.Contains(ultimate.Slot) };
                var root = ultimate.Condition as ConditionGroup ?? new ConditionGroup();
                details.Children.Add(new StrategyConditionEditor(root, () => { ultimate.Condition = root.Children.Count == 0 ? null : root; _session.MarkDirty(); }, ultimate.Slot));
                var toggle = StrategyUi.Button("前置条件", () => { details.IsVisible = !details.IsVisible; if (details.IsVisible) _openSlots.Add(ultimate.Slot); else _openSlots.Remove(ultimate.Slot); });
                var row = StrategyUi.Column(StrategyUi.Row(handle, StrategyUi.Check($"位置 {ultimate.Slot}", ultimate.Enabled, value => { ultimate.Enabled = value; _session.MarkDirty(); }), toggle), details);
                list.Children.Add(row);
                StrategyReorder.Attach(handle, row, list, _stage.Actions.Ultimates, ultimate, () => { _session.MarkDirty(); Render(); });
            }
        }
    }

    private Control BuildNormalLinks()
    {
        var colors = new StackPanel { Spacing = 4, IsVisible = _stage.Actions.NormalLink.PrioritizeColors };
        Render();
        return StrategyUi.Column(StrategyUi.Check("设置目标颜色优先级", _stage.Actions.NormalLink.PrioritizeColors, value => { _stage.Actions.NormalLink.PrioritizeColors = value; colors.IsVisible = value; _session.MarkDirty(); }), colors);
        void Render()
        {
            colors.Children.Clear();
            foreach (var color in _stage.Actions.NormalLink.ColorPriority)
            {
                var handle = StrategyUi.DragHandle();
                var row = StrategyUi.Row(handle, StrategyUi.Text(StrategyUi.ColorName(color)));
                colors.Children.Add(row);
                StrategyReorder.Attach(handle, row, colors, _stage.Actions.NormalLink.ColorPriority, color, () => { _session.MarkDirty(); Render(); });
            }
        }
    }

    private Control BuildFrenzy()
    {
        var selected = _stage.Frenzy.ReservedColor ?? GhoulColor.Red;
        var color = StrategyUi.Choice(StrategyUi.Colors, selected, value => { selected = value; if (_stage.Frenzy.ReservedColor != null) { _stage.Frenzy.ReservedColor = value; _session.MarkDirty(); } });
        color.IsVisible = _stage.Frenzy.ReservedColor != null;
        return StrategyUi.Column(
            StrategyUi.Check("乱舞期间使用奥义", _stage.Frenzy.AllowUltimates, value => { _stage.Frenzy.AllowUltimates = value; _session.MarkDirty(); }),
            StrategyUi.Check("乱舞期间使用炸弹", _stage.Frenzy.AllowBombs, value => { _stage.Frenzy.AllowBombs = value; _session.MarkDirty(); }),
            StrategyUi.Check("保留一种颜色", _stage.Frenzy.ReservedColor != null, value => { _stage.Frenzy.ReservedColor = value ? selected : null; color.IsVisible = value; _session.MarkDirty(); }), color);
    }

    private Control BuildTransitions()
    {
        var list = new StackPanel { Spacing = 10 };
        var add = StrategyUi.Button("添加转变规则", () => { var rule = new StageTransition(); _stage.Transitions.Add(rule); _openRules.Add(rule.Id); _session.MarkDirty(); Render(); });
        Render(); return StrategyUi.Column(list, add);
        void Render()
        {
            list.Children.Clear();
            foreach (var rule in _stage.Transitions)
            {
                var handle = StrategyUi.DragHandle();
                var target = StrategyUi.Choice(_session.Current.Stages.Select(x => new EditorChoice<string>(x.Id, x.Name)), rule.TargetStageId, value => { rule.TargetStageId = value; _session.MarkDirty(); });
                target.PlaceholderText = "选择目标阶段";
                var details = new StackPanel { IsVisible = _openRules.Contains(rule.Id) };
                details.Children.Add(new StrategyConditionEditor((ConditionGroup)rule.Condition, _session.MarkDirty));
                var edit = StrategyUi.Button("转变条件", () => { details.IsVisible = !details.IsVisible; if (details.IsVisible) _openRules.Add(rule.Id); else _openRules.Remove(rule.Id); });
                var remove = StrategyUi.Button("删除规则", () => { _stage.Transitions.Remove(rule); _session.MarkDirty(); Render(); });
                var row = StrategyUi.Card(StrategyUi.Column(StrategyUi.Row(handle, StrategyUi.Text("转到"), target, edit, remove), details), "strategy-condition");
                list.Children.Add(row);
                StrategyReorder.Attach(handle, row, list, _stage.Transitions, rule, () => { _session.MarkDirty(); Render(); });
            }
        }
    }
}
