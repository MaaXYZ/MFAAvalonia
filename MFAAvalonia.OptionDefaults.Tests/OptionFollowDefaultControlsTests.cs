using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public Task UntouchedInputsShowDefaultsWithoutWritingOrSaving() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(
            new MaaInterface.MaaInterfaceOptionInput { Name = "text", Default = "abc" },
            new MaaInterface.MaaInterfaceOptionInput { Name = "flag", Default = "true", PipelineType = "bool" });
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "settings" };

        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(option.Data);
        Assert.Equal(0, context.Saves);
        Assert.Equal("abc", panel.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Assert.True(panel.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked);
        Assert.False(FindReset(panel, "RestoreResourceDefault:text").IsEnabled);
        Assert.Contains("abc", option.PipelineOverride);

        panel.GetVisualDescendants().OfType<TextBox>().Single().Text = "mine";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("mine", option.Data!["text"]);
        Assert.False(option.Data.ContainsKey("flag"));
        Assert.Equal(1, context.Saves);
    });

    [Fact]
    public Task UntouchedCheckboxShowsDefaultsAndStartsFromThemOnFirstClick() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "checkbox", Name = "flags", DefaultCases = ["x"], Cases = [new() { Name = "x" }, new() { Name = "y" }]
        };
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "flags" };
        var panel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateCheckboxControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, definition, null])!;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var buttons = panel.GetVisualDescendants().OfType<ToggleButton>().Where(button => button is not ToggleSwitch).ToList();

        Assert.Null(option.SelectedCases);
        Assert.Equal(0, context.Saves);
        Assert.Equal([true, false], buttons.Select(button => button.IsChecked == true));

        buttons[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["x", "y"], option.SelectedCases);
        Assert.Equal(1, context.Saves);
    });

    [Fact]
    public Task UntouchedSelectAndSwitchShowDefaultsWithoutWriting() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var select = new MaaInterface.MaaInterfaceOption
        {
            Type = "select", Name = "choice", DefaultCases = ["B"], Cases = [new() { Name = "A" }, new() { Name = "B" }]
        };
        var toggle = new MaaInterface.MaaInterfaceOption
        {
            Type = "switch", Name = "enabled", Cases = [new() { Name = "yes" }, new() { Name = "no" }]
        };
        var selectOption = new MaaInterface.MaaInterfaceSelectOption { Name = "choice" };
        var toggleOption = new MaaInterface.MaaInterfaceSelectOption { Name = "enabled" };
        var selectPanel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateComboBoxControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [selectOption, select, null])!;
        var togglePanel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateToggleControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [toggleOption, 0, 1, toggle, null])!;
        context.Show(new StackPanel { Children = { selectPanel, togglePanel } });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, selectPanel.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex);
        Assert.True(togglePanel.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked);
        Assert.Null(selectOption.SelectedCase);
        Assert.Null(selectOption.Index);
        Assert.Null(toggleOption.SelectedCase);
        Assert.Null(toggleOption.Index);
        Assert.Equal(0, context.Saves);
    });
}
