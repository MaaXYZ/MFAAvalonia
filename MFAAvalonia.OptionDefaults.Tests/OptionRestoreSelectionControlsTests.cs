using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public partial class OptionControlsTests
{
    [Fact]
    public Task SelectRestoreClearsTheChoiceAndShowsTheDefaultWithOneSave() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("select", "B");
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "A", Index = 0 };
        var panel = CreateSelectionControl(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var combo = panel.GetVisualDescendants().OfType<ComboBox>().Single();
        var reset = FindReset(panel, "RestoreResourceDefault:choice");
        Assert.True(reset.IsEnabled);

        Click(reset);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(option.SelectedCase);
        Assert.Null(option.Index);
        Assert.Equal(1, combo.SelectedIndex);
        Assert.Equal(1, context.Saves);
        Assert.False(reset.IsEnabled);

        combo.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("A", option.SelectedCase);
        Assert.Equal(2, context.Saves);
        Assert.True(reset.IsEnabled);
    });

    [Fact]
    public Task SwitchRestoreFollowsTheFirstCaseWhenNoDefaultIsDeclared() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("switch", null, "yes", "no");
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "no", Index = 1 };
        var panel = CreateSelectionControl(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var toggle = panel.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        Assert.False(toggle.IsChecked);

        Click(FindReset(panel, "RestoreResourceDefault:choice"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(toggle.IsChecked);
        Assert.Null(option.SelectedCase);
        Assert.Null(option.Index);
        Assert.Equal(1, context.Saves);
    });

    [Fact]
    public Task InvalidSavedChoiceCanBeRestoredInOneClick() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("select", "B");
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "removed", Index = 1 };
        var panel = CreateSelectionControl(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var status = panel.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Foreground == Avalonia.Media.Brushes.OrangeRed);
        Assert.True(status.IsVisible);

        Click(FindReset(panel, "RestoreResourceDefault:choice"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(status.IsVisible);
        Assert.Null(option.SelectionError);
        Assert.Null(option.SelectedCase);
        Assert.Equal(1, panel.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex);
        Assert.Equal(1, context.Saves);
    });

    [Fact]
    public Task InvalidResourceDefaultOffersNoRestore() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("select", "missing");
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "A", Index = 0 };
        var panel = CreateSelectionControl(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var reset = FindReset(panel, "RestoreResourceDefault:choice");

        Assert.False(reset.IsEnabled);
        Click(reset);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("A", option.SelectedCase);
        Assert.Equal(0, context.Saves);
    });

    [Fact]
    public Task RestoreButtonsNameTheirFieldWithoutClaimingAModification() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("select", "B");
        definition.Label = "Arena multiplier";
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "A" };
        var panel = CreateSelectionControl(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();

        var name = AutomationProperties.GetName(FindReset(panel, "RestoreResourceDefault:choice"));
        Assert.Contains("Arena multiplier", name);
        Assert.Contains(LangKeys.RestoreInputResourceDefault.ToLocalization(), name);
        Assert.DoesNotContain("已修改", name);
    });

    [Fact]
    public Task NarrowSwitchRowMovesTheValueToASecondRow() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = SelectDefinition("switch", null, "yes", "no");
        definition.Label = "A switch label that is long enough to collide with the value group";
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice" };
        var panel = CreateSelectionControl(context, option, definition);
        panel.Width = 220;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var toggle = panel.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        var valueGroup = (Control)toggle.Parent!;

        Assert.Equal(1, Grid.GetRow(valueGroup));

        panel.Width = 880;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, Grid.GetRow(valueGroup));
        Assert.Equal(2, Grid.GetColumn(valueGroup));
        Assert.Equal(0, context.Saves);
    });

    private static MaaInterface.MaaInterfaceOption SelectDefinition(string type, string? defaultCase, params string[] cases)
        => new()
        {
            Type = type, Name = "choice",
            DefaultCases = defaultCase == null ? null : [defaultCase],
            Cases = (cases.Length == 0 ? new[] { "A", "B" } : cases).Select(name => new MaaInterface.MaaInterfaceOptionCase { Name = name }).ToList()
        };

    private static Control CreateSelectionControl(OptionControlsTestContext context,
        MaaInterface.MaaInterfaceSelectOption option, MaaInterface.MaaInterfaceOption definition)
        => definition.IsSwitch
            ? (Control)typeof(TaskOptionGenerator)
                .GetMethod("CreateToggleControl", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(context.Generator, [option, 0, 1, definition, null])!
            : (Control)typeof(TaskOptionGenerator)
                .GetMethod("CreateComboBoxControl", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(context.Generator, [option, definition, null])!;
}
