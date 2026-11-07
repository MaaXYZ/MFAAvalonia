using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class OptionControlsTestSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } =
        HeadlessUnitTestSession.StartNew(typeof(OptionControlsTestApplication));

    public void Dispose() => Session.Dispose();
}

public class OptionControlsTests(OptionControlsTestSession fixture) : IClassFixture<OptionControlsTestSession>
{
    [Fact]
    public Task SingleFieldRestoresLiteralNullWithoutDelayedSaves() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput { Name = "text", Default = "null" });
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings",
            Data = new() { ["text"] = MaaInterface.MaaInterfaceOption.ExplicitNullMarker }
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        var reset = FindReset(panel, "RestoreResourceDefault:text");

        Click(reset);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("null", option.Data["text"]);
        Assert.Equal("null", panel.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Assert.Equal(1, context.Saves);
        Assert.False(reset.IsEnabled);
        var output = JObject.Parse(option.PipelineOverride);
        Assert.Equal(JTokenType.String, output["Test"]!["text"]!.Type);
        Assert.Equal("null", output["Test"]!["text"]!.Value<string>());
    });

    [Fact]
    public Task FiftyTwoFieldGroupCancelsWithoutSavingAndConfirmsWithOneSave() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var fields = Enumerable.Range(0, 52).Select(index => new MaaInterface.MaaInterfaceOptionInput
        {
            Name = $"material{index}", Default = index.ToString(), PipelineType = "int"
        }).ToArray();
        var definition = InputDefinition(fields);
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", Data = fields.ToDictionary(field => field.Name!, _ => (string?)"999")
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        var reset = FindReset(panel, "RestoreResourceDefaults:settings");

        Click(reset);
        Assert.NotNull(context.Dialog);
        Assert.Equal(0, context.Saves);
        Click(context.Dialog.ActionButtons.OfType<Button>().First());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        Assert.All(option.Data.Values, value => Assert.Equal("999", value));

        Click(reset);
        Assert.NotNull(context.Dialog);
        Click(context.Dialog.ActionButtons.OfType<Button>().Last());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, context.Saves);
        foreach (var field in fields)
            Assert.Equal(field.Default, option.Data[field.Name!]);
        Assert.False(reset.IsEnabled);
        Assert.All(panel.GetVisualDescendants().OfType<TextBox>(), textBox => Assert.NotEqual("999", textBox.Text));
        var output = JObject.Parse(option.PipelineOverride);
        Assert.Equal(52, output["Test"]!.Children().Count());
        Assert.All(output["Test"]!.Children<JProperty>(), property => Assert.Equal(JTokenType.Integer, property.Value.Type));
    });

    [Fact]
    public Task ResetEnabledStateKeepsBothIdleBindingsAndRejectsInputWhileRunning() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput { Name = "text", Default = "default" });
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "settings", Data = new() { ["text"] = "custom" } };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var reset = FindReset(panel, "RestoreResourceDefault:text");
        Assert.True(reset.IsEnabled);
        reset.Focus();

        context.Root.Idle = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(reset.IsEnabled);
        var center = reset.TranslatePoint(new Point(reset.Bounds.Width / 2, reset.Bounds.Height / 2), context.Window)!.Value;
        context.Window.MouseDown(center, MouseButton.Left);
        context.Window.MouseUp(center, MouseButton.Left);
        context.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        context.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        // Also verify the handler's independent guard against a stale queued Click.
        Click(reset);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("custom", option.Data["text"]);
        Assert.Equal(0, context.Saves);

        context.Root.Idle = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(reset.IsEnabled);
        context.TaskQueue.IsRunning = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(reset.IsEnabled);
        Click(reset);
        Assert.Equal(0, context.Saves);
        context.TaskQueue.IsRunning = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(reset.IsEnabled);
        Click(reset);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("default", option.Data["text"]);
        Assert.Equal(1, context.Saves);

        panel.GetVisualDescendants().OfType<TextBox>().Single().Text = "edited again";
        Dispatcher.UIThread.RunJobs();
        Assert.True(reset.IsEnabled);
        context.Root.Idle = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(reset.IsEnabled);
    });

    [Fact]
    public Task GroupConfirmationRechecksIdleBeforeChangingData() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new() { Name = "first", Default = "one" }, new() { Name = "second", Default = "two" });
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", Data = new() { ["first"] = "custom", ["second"] = "custom" }
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        Click(FindReset(panel, "RestoreResourceDefaults:settings"));
        Assert.NotNull(context.Dialog);

        context.Root.Idle = false;
        Click(context.Dialog.ActionButtons.OfType<Button>().Last());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, context.Saves);
        Assert.All(option.Data.Values, value => Assert.Equal("custom", value));
    });

    [Fact]
    public Task ManualIntegerCleaningUpdatesDataAndPipelineOnce() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput { Name = "amount", Default = "007", PipelineType = "int" });
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", Data = new() { ["amount"] = "99" }
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        var textBox = panel.GetVisualDescendants().OfType<TextBox>().Single();

        textBox.Text = "+007";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("7", textBox.Text);
        Assert.Equal("7", option.Data["amount"]);
        Assert.Equal(1, context.Saves);
        Assert.Equal(7L, JObject.Parse(option.PipelineOverride)["Test"]!["amount"]!.Value<long>());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ProcessorStartupBlocksResetBeforeIdleNotification(bool confirmationAlreadyOpen) => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new() { Name = "first", Default = "one" }, new() { Name = "second", Default = "two" });
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", Data = new() { ["first"] = "custom", ["second"] = "custom" }
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var groupReset = FindReset(panel, "RestoreResourceDefaults:settings");
        if (confirmationAlreadyOpen)
        {
            Click(groupReset);
            Assert.NotNull(context.Dialog);
        }

        context.SetProcessorActiveBeforeIdleNotification(true);
        Assert.True(context.Root.Idle);
        Assert.True(context.TaskQueue.Idle);
        if (confirmationAlreadyOpen)
            Click(context.Dialog!.ActionButtons.OfType<Button>().Last());
        else
        {
            Click(FindReset(panel, "RestoreResourceDefault:first"));
            Click(groupReset);
            Assert.Null(context.Dialog);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        Assert.All(option.Data.Values, value => Assert.Equal("custom", value));

        context.SetProcessorActiveBeforeIdleNotification(false);
        Click(FindReset(panel, "RestoreResourceDefault:first"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, context.Saves);
        Assert.Equal("one", option.Data["first"]);
        Assert.Equal("custom", option.Data["second"]);
    });

    [Fact]
    public Task CheckboxGroupRefreshesMultipleTogglesWithOneSave() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Name = "settings", Type = "checkbox", DefaultCases = ["one"],
            Cases = [new() { Name = "one" }, new() { Name = "two" }, new() { Name = "three" }]
        };
        var remembered = new List<MaaInterface.MaaInterfaceSelectOption>
        {
            new() { Name = "child", Data = new() { ["text"] = "retain" } }
        };
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", SelectedCases = ["two", "three"], SubOptions = remembered
        };
        // Child-definition resolution needs MaaProcessor's native initialization, so this
        // control test covers toggle events; nested visibility is checked in the real client.
        var panel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateCheckboxControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, definition, null])!;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        var reset = FindReset(panel, "RestoreResourceDefaults:settings");

        Click(reset);
        Assert.NotNull(context.Dialog);
        Click(context.Dialog.ActionButtons.OfType<Button>().First());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, context.Saves);
        Assert.Equal(["two", "three"], option.SelectedCases);
        Click(reset);
        Assert.NotNull(context.Dialog);
        Click(context.Dialog.ActionButtons.OfType<Button>().Last());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["one"], option.SelectedCases);
        var toggles = panel.GetVisualDescendants().OfType<ToggleButton>().ToArray();
        Assert.Equal(3, toggles.Length);
        Assert.True(toggles[0].IsChecked);
        Assert.False(toggles[1].IsChecked);
        Assert.False(toggles[2].IsChecked);
        Assert.Equal(1, context.Saves);
        Assert.Same(remembered, option.SubOptions);
        Assert.Equal("retain", option.SubOptions![0].Data!["text"]);
        Assert.False(reset.IsEnabled);
    });

    [Theory]
    [InlineData(300, "string")]
    [InlineData(320, "string")]
    [InlineData(340, "string")]
    [InlineData(300, "bool")]
    [InlineData(320, "bool")]
    public Task NarrowInputRowKeepsResetButtonInsideItsBounds(double width, string pipelineType) => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput
        {
            Name = "value", Default = "true", PipelineType = pipelineType
        });
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "settings", Data = new() { ["value"] = "false" } };
        var panel = CreateInputs(context, option, definition);
        context.Window.Width = width;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        var reset = FindReset(panel, "RestoreResourceDefault:value");
        var row = Assert.IsType<Grid>(((StackPanel)panel).Children.Single());
        var right = reset.TranslatePoint(new Point(reset.Bounds.Width, 0), row)!.Value.X;
        Assert.True(right <= row.Bounds.Width + 0.1,
            $"Reset button right={right} exceeds row width={row.Bounds.Width} at window width={width} ({pipelineType}).");
        var windowRight = reset.TranslatePoint(new Point(reset.Bounds.Width, 0), context.Window)!.Value.X;
        Assert.True(windowRight <= context.Window.ClientSize.Width + 0.1,
            $"Reset button right={windowRight} exceeds client width={context.Window.ClientSize.Width} ({pipelineType}).");
        Assert.Equal(0, context.Saves);
    });

    [Fact]
    public Task HotkeyInputPathDoesNotExposeRestoreButtons() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Name = "shortcut", Type = "hotkey", Hotkeys = [new() { Name = "activate", Default = "F6" }]
        };
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "shortcut", Data = new() { ["activate"] = "F7" }
        };
        var panel = (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateHotkeyControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, definition])!;
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button)?.StartsWith("RestoreResourceDefault", StringComparison.Ordinal) == true);
        Assert.Equal("F7", option.Data["activate"]);
        Assert.Equal(0, context.Saves);
    });

    [Theory]
    [InlineData("text")]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("unknown")]
    public Task EditorTypeKeepsEditableTextAndPipelineConversionSeparate(string editorType) => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput
        {
            Name = "value", InputType = editorType, PipelineType = "int", Default = "7"
        });
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "settings" };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        Dispatcher.UIThread.RunJobs();
        var textBox = panel.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal('\0', textBox.PasswordChar);
        Assert.Equal(editorType is "file" or "directory", textBox.InnerRightContent is Button);
        textBox.Text = "23";
        Assert.Equal("23", option.Data!["value"]);
        Assert.Equal(JTokenType.Integer, JObject.Parse(option.PipelineOverride)["Test"]!["value"]!.Type);
        textBox.Text = "C:\\not-yet-created\\配置.json";
        Assert.Equal(textBox.Text, option.Data["value"]);
    });

    [Fact]
    public Task ExplicitTextEditorOverridesLegacyBooleanSwitch() => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput
        {
            Name = "value", InputType = "text", PipelineType = "bool", Default = "true"
        });
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "settings" };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        var textBox = panel.GetVisualDescendants().OfType<TextBox>().Single();
        textBox.Text = "false";
        Assert.False(JObject.Parse(option.PipelineOverride)["Test"]!["value"]!.Value<bool>());
    });

    [Theory]
    [InlineData("string")]
    [InlineData("int")]
    [InlineData("bool")]
    public Task PasswordEditorAlwaysMasksSavedValuesAndHasNoDefaultReset(string pipelineType) => Run(() =>
    {
        using var context = new OptionControlsTestContext();
        var definition = InputDefinition(new MaaInterface.MaaInterfaceOptionInput
        {
            Name = "value", Password = true, PipelineType = pipelineType, Default = "must-not-be-used"
        });
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "settings", Data = new() { ["value"] = "saved-secret" }
        };
        var panel = CreateInputs(context, option, definition);
        context.Show(panel);
        var textBox = panel.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.NotEqual('\0', textBox.PasswordChar);
        Assert.False(textBox.RevealPassword);
        Assert.Equal("saved-secret", textBox.Text);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button)?.StartsWith("RestoreResourceDefault", StringComparison.Ordinal) == true);
        textBox.Text = "new-secret";
        Assert.Equal("new-secret", option.Data!["value"]);
        Assert.DoesNotContain("new-secret", JsonConvert.SerializeObject(option));
    });

    [Fact]
    public Task NotificationFromWorkerThreadCreatesMarkdownOnUiThread()
        => fixture.Session.Dispatch(async () =>
        {
            using var context = new OptionControlsTestContext();
            var notifications = 0;
            Action<object?, object?> showNotification = (title, content) =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                Assert.IsAssignableFrom<Control>(content);
                notifications++;
            };
            var handler = (FocusHandler)typeof(FocusHandler)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [typeof(AutoInitDictionary), typeof(MFAAvalonia.ViewModels.Pages.TaskQueueViewModel), typeof(Action<object?, object?>)], null)!
                .Invoke([new AutoInitDictionary(), context.TaskQueue, showNotification]);
            await Task.Run(() => typeof(FocusHandler)
                .GetMethod("DispatchToChannels", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(handler, ["**content**", new List<string> { "notification" }]));
            Assert.Equal(1, notifications);
            return true;
        }, CancellationToken.None);

    private Task Run(Action action) => fixture.Session.Dispatch(() =>
    {
        action();
        using var process = Process.GetCurrentProcess();
        Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(), module =>
            module.ModuleName is "MaaFramework.dll" or "MaaToolkit.dll" or "MaaAgentClient.dll" or "MaaAgentServer.dll"
            || module.ModuleName.StartsWith("libMaaFramework.", StringComparison.Ordinal)
            || module.ModuleName.StartsWith("libMaaToolkit.", StringComparison.Ordinal));
    }, CancellationToken.None);

    private static Control CreateInputs(OptionControlsTestContext context,
        MaaInterface.MaaInterfaceSelectOption option, MaaInterface.MaaInterfaceOption definition)
        => (Control)typeof(TaskOptionGenerator)
            .GetMethod("CreateInputControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(context.Generator, [option, definition, true])!;

    private static Button FindReset(Control panel, string automationId)
        => panel.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == automationId);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static MaaInterface.MaaInterfaceOption InputDefinition(params MaaInterface.MaaInterfaceOptionInput[] inputs)
        => new()
        {
            Name = "settings", Type = "input", Inputs = inputs.ToList(),
            PipelineOverride = new Dictionary<string, Dictionary<string, JToken>>
            {
                ["Test"] = inputs.ToDictionary(input => input.Name!, input => (JToken)$"{{{input.Name}}}")
            }
        };
}
