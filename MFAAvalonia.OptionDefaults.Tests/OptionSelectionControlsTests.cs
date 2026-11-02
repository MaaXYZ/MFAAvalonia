using Avalonia.Controls;
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
    public Task InvalidSavedCaseIsNotDisplayedAsAnotherComboChoiceAndCanBeReselected() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "select", Name = "choice", Cases = [new() { Name = "A" }, new() { Name = "B" }]
        };
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", SelectedCase = "removed", Index = 1 };
        var panel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateComboBoxControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, definition, null])!;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var combo = panel.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.Equal(-1, combo.SelectedIndex);
        Assert.Equal("removed", option.SelectedCase);
        Assert.NotNull(option.SelectionError);
        Assert.Equal(0, context.Saves);

        combo.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("A", option.SelectedCase);
        Assert.Equal(0, option.Index);
        Assert.Null(option.SelectionError);
        Assert.Equal(1, context.Saves);
    });

    [Fact]
    public Task InvalidSavedSwitchIsIndeterminateUntilUserSelectsAndWritesCaseName() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "switch", Name = "enabled", Cases = [new() { Name = "yes" }, new() { Name = "no" }]
        };
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "enabled", SelectedCase = "removed", Index = 1 };
        var panel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateToggleControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, 0, 1, definition, null])!;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var toggle = panel.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        Assert.Null(toggle.IsChecked);
        Assert.Equal("removed", option.SelectedCase);
        Assert.Equal(0, context.Saves);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("yes", option.SelectedCase);
        Assert.Equal(0, option.Index);
        Assert.Null(option.SelectionError);
        Assert.Equal(1, context.Saves);
    });
}
