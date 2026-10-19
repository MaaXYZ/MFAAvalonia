using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Input;
using MFAAvalonia.Features.CombatStrategies;
using MFAAvalonia.Features.CombatStrategies.Controls;
using System.Linq;
using System;
using System.IO;
using System.Text.Json;
using MFAAvalonia.Views.Pages;
using Xunit;

namespace MFAAvalonia.Tests;

public sealed class CombatStrategyControlsTests
{
    [Theory]
    [InlineData(0, 60, 0)]
    [InlineData(0, 150, 0)]
    [InlineData(0, 190, 1)]
    [InlineData(0, 350, 2)]
    [InlineData(2, 20, 0)]
    [InlineData(2, 140, 1)]
    public void DragDrop_AccountsForRemovedSourceRow(int from, double y, int expected)
    {
        var reorder = typeof(StrategyStageCard).Assembly.GetType("MFAAvalonia.Features.CombatStrategies.Controls.StrategyReorder")!;
        var calculate = reorder.GetMethod("CalculateDropIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Avalonia.Rect[] rows = [new(0, 0, 200, 100), new(0, 110, 200, 100), new(0, 220, 200, 100)];
        Assert.Equal(expected, calculate.Invoke(null, [rows, from, y]));
    }

    [Fact]
    public void PageControls_SaveStageAndUltimateEditsToRealJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "map-page-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var session = new StrategyEditorSession(new StrategyStore(directory));
            session.Create("界面策略");
            var view = new CombatStrategyView(session);
            view.FindControl<Button>("AddStage")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, session.Current.Stages.Count);
            var cards = view.FindControl<StackPanel>("StageCards")!;
            var firstCard = cards.Children.OfType<StrategyStageCard>().First();
            firstCard.DragHandle.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Key.Down });
            Assert.Equal(session.Current.InitialStageId, session.Current.Stages[1].Id);
            var initialCard = cards.Children.OfType<StrategyStageCard>().Last();
            var slot = initialCard.GetLogicalDescendants().OfType<CheckBox>().First(x => Equals(x.Content, "位置 2"));
            slot.IsChecked = false;
            view.FindControl<Button>("SaveStrategy")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var restored = new StrategyStore(directory).Load(session.Current.Id);
            Assert.Equal(2, restored.Stages.Count);
            Assert.False(restored.Stages[1].Actions.Ultimates.Single(x => x.Slot == 2).Enabled);
            Assert.False(session.HasUnsavedChanges);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConditionControls_WriteNumericValuesAndNestedGroupsToDraft()
    {
        var changed = 0;
        var root = new ConditionGroup
        {
            Children = [new ConditionPredicate { Metric = ConditionMetric.StageNormalLinks, Comparison = ComparisonOperator.GreaterOrEqual, Value = 1 }]
        };
        var editor = new StrategyConditionEditor(root, () => changed++, ownerSlot: 3);
        var number = editor.GetLogicalDescendants().OfType<NumericUpDown>().Single();
        number.Value = 2;
        Assert.Equal(2, Assert.IsType<ConditionPredicate>(root.Children[0]).Value);
        var addGroup = editor.GetLogicalDescendants().OfType<Button>().First(x => x.Name == "AddConditionGroup");
        addGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, root.Children.Count);
        Assert.IsType<ConditionGroup>(root.Children[1]);
        Assert.True(changed >= 2);
    }

    [Fact]
    public void RootCondition_ContainsExplicitTypeForPythonConsumer()
    {
        var document = StrategyDefaults.CreateEditable("条件契约");
        document.Stages[0].Actions.Ultimates[0].Condition = new ConditionGroup
        {
            Children = [new ConditionPredicate { Metric = ConditionMetric.EnemyHp, Comparison = ComparisonOperator.LessOrEqual, Value = 10000 }]
        };
        using var json = JsonDocument.Parse(StrategyJson.Serialize(document));
        var condition = json.RootElement.GetProperty("stages")[0].GetProperty("actions").GetProperty("ultimates")[0].GetProperty("condition");
        Assert.Equal("group", condition.GetProperty("type").GetString());
    }
}
