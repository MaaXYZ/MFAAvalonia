using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

/// <summary>Restoring a default clears the saved value so the option follows the current default again.</summary>
public class OptionDefaultResetTests
{
    [Fact]
    public void UndeclaredDefaultRestoresToEmptyText()
    {
        var definition = ReadDefinition("""{"type":"input","inputs":[{"name":"amount"}]}""");
        var input = definition.Inputs![0];
        var untouched = new MaaInterface.MaaInterfaceSelectOption();
        var option = InputOption(("amount", "5"));

        Assert.True(OptionDefaultReset.SupportsRestore(input));
        Assert.False(OptionDefaultReset.CanRestoreInput(untouched, definition, input));
        Assert.False(OptionDefaultReset.RestoreInput(untouched, definition, input));
        Assert.Null(untouched.Data);
        Assert.True(OptionDefaultReset.RestoreInput(option, definition, input));
        Assert.Null(option.Data);
        Assert.Equal(string.Empty, OptionValues.GetFieldValues(definition, option)["amount"]);
    }

    [Fact]
    public void ExplicitEmptyDefaultRestoresToEmptyString()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"text","default":""}],
             "pipeline_override":{"Test":{"value":"{text}"}}}
            """);
        var option = InputOption(("text", "custom"));

        Assert.Equal(1, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Null(option.Data);
        var output = Generate(definition, option);
        Assert.Equal(JTokenType.String, output["Test"]!["value"]!.Type);
        Assert.Equal(string.Empty, output["Test"]!["value"]!.Value<string>());
        Assert.Equal(0, OptionDefaultReset.RestoreInputs(option, definition));
    }

    [Theory]
    [InlineData("true", false)]
    [InlineData("TRUE", false)]
    [InlineData("false", true)]
    [InlineData("1", true)]
    [InlineData("invalid", true)]
    public void BooleanDefaultsUseValidBooleanMeaning(string current, bool expectedCanRestore)
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"enabled","default":true,"pipeline_type":"bool"}],
             "pipeline_override":{"Test":{"enabled":"{enabled}"}}}
            """);
        var input = definition.Inputs![0];
        var option = InputOption(("enabled", current));

        Assert.True(bool.Parse(input.Default!));
        Assert.Equal(expectedCanRestore, OptionDefaultReset.CanRestoreInput(option, definition, input));
        Assert.Equal(expectedCanRestore, OptionDefaultReset.RestoreInput(option, definition, input));
        Assert.False(OptionDefaultReset.CanRestoreInput(option, definition, input));
        Assert.Equal(!expectedCanRestore, option.Data?.ContainsKey("enabled") == true);
        var output = Generate(definition, option);
        Assert.Equal(JTokenType.Boolean, output["Test"]!["enabled"]!.Type);
        Assert.True(output["Test"]!["enabled"]!.Value<bool>());
    }

    [Fact]
    public void IntegerDefaultPreservesDeclaredFormattingAndGeneratesNumber()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"amount","default":"007","pipeline_type":"int"}],
             "pipeline_override":{"Test":{"amount":"{amount}"}}}
            """);
        var option = InputOption(("amount", "70"));

        Assert.Equal(1, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal("007", OptionValues.GetFieldValues(definition, option)["amount"]);
        Assert.Equal(0, OptionDefaultReset.CountChangedInputs(option, definition));
        var output = Generate(definition, option);
        Assert.Equal(JTokenType.Integer, output["Test"]!["amount"]!.Type);
        Assert.Equal(7L, output["Test"]!["amount"]!.Value<long>());
    }

    [Fact]
    public void LiteralNullDefaultDoesNotBecomeExplicitNullMarker()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"text","default":"null"}],
             "pipeline_override":{"Test":{"value":"{text}"}}}
            """);
        var option = InputOption(("text", MaaInterface.MaaInterfaceOption.ExplicitNullMarker));

        Assert.Equal(JTokenType.Null, Generate(definition, option)["Test"]!["value"]!.Type);
        Assert.Equal(1, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal("null", OptionValues.GetFieldValues(definition, option)["text"]);
        var output = Generate(definition, option);
        Assert.Equal(JTokenType.String, output["Test"]!["value"]!.Type);
        Assert.Equal("null", output["Test"]!["value"]!.Value<string>());
    }

    [Fact]
    public void SingleInputRestorePreservesOtherFieldsAndSubOptions()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"first","default":"one"},{"name":"second","default":"two"}]}
            """);
        var child = InputOption(("child", "keep"));
        var children = new List<MaaInterface.MaaInterfaceSelectOption> { child };
        var option = InputOption(("first", "changed"), ("second", "also changed"), ("unknown", "retain"));
        option.SubOptions = children;
        option.SelectedCases = ["keep-case"];

        Assert.True(OptionDefaultReset.RestoreInput(option, definition, definition.Inputs![0]));
        Assert.False(option.Data!.ContainsKey("first"));
        Assert.Equal("also changed", option.Data["second"]);
        Assert.Equal("retain", option.Data["unknown"]);
        Assert.Equal(["keep-case"], option.SelectedCases);
        Assert.Same(children, option.SubOptions);
        Assert.Same(child, option.SubOptions![0]);
        Assert.Equal("keep", child.Data!["child"]);
    }

    [Fact]
    public void GroupRestoreClearsEveryChangedNamedField()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"first","default":"one"},{"name":"second","default":""},
             {"name":"undeclared"},{"default":"unnamed"},{"name":"secret","password":true}]}
            """);
        var option = InputOption(("first", "changed"), ("second", "changed"), ("undeclared", "custom"), ("secret", "kept"));

        Assert.Equal(3, OptionDefaultReset.CountRestorableInputs(definition));
        Assert.Equal(3, OptionDefaultReset.CountChangedInputs(option, definition));
        Assert.Equal(3, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal(new Dictionary<string, string?> { ["secret"] = "kept" }, option.Data);
        Assert.Equal(0, OptionDefaultReset.CountChangedInputs(option, definition));
    }

    [Fact]
    public void RestoreMatchesDeclaredDefaultsAndNewTaskPipeline()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"text","default":"null"},{"name":"empty","default":""},
             {"name":"amount","default":"007","pipeline_type":"int"},
             {"name":"enabled","default":true,"pipeline_type":"bool"}],
             "pipeline_override":{"Test":{"text":"{text}","empty":"{empty}","amount":"{amount}","enabled":"{enabled}"}}}
            """);
        var option = InputOption(("text", "changed"), ("empty", "changed"), ("amount", "55"), ("enabled", "false"));
        var newTask = new MaaInterface.MaaInterfaceSelectOption { Name = "settings" };
        TaskLoader.PrepareOption(new MaaInterface
        {
            Option = new Dictionary<string, MaaInterface.MaaInterfaceOption> { ["settings"] = definition }
        }, newTask);

        // A new task saves nothing and follows the defaults; restoring reaches the same state.
        Assert.Null(newTask.Data);
        Assert.Equal(4, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Null(option.Data);
        Assert.True(JToken.DeepEquals(Generate(definition, newTask), Generate(definition, option)));
    }

    [Fact]
    public void GroupWithFiftyTwoInputsReportsActualChangedCount()
    {
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "input",
            Inputs = Enumerable.Range(0, 52).Select(index => new MaaInterface.MaaInterfaceOptionInput
            {
                Name = $"material{index}", Default = index.ToString(), PipelineType = "int"
            }).ToList()
        };
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Data = definition.Inputs.ToDictionary(input => input.Name!, _ => (string?)"custom")
        };
        option.Data["material0"] = "0";

        Assert.Equal(52, OptionDefaultReset.CountRestorableInputs(definition));
        Assert.Equal(51, OptionDefaultReset.CountChangedInputs(option, definition));
        Assert.Equal(51, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal(0, OptionDefaultReset.CountChangedInputs(option, definition));
    }

    [Fact]
    public void CheckboxWithoutDeclaredDefaultRestoresToNoCase()
    {
        var definition = ReadDefinition("""{"type":"checkbox","cases":[{"name":"one"}]}""");
        var untouched = new MaaInterface.MaaInterfaceSelectOption();
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCases = ["one"] };

        Assert.False(OptionDefaultReset.CanRestoreCheckbox(untouched, definition));
        Assert.False(OptionDefaultReset.RestoreCheckbox(untouched, definition));
        Assert.True(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Null(option.SelectedCases);
        Assert.Empty(OptionValues.GetSelectedCases(definition, option));
    }

    [Fact]
    public void ExplicitEmptyCheckboxDefaultFollowsWithoutRemovingSubOptions()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one","option":["child"]}],"default_case":[]}
            """);
        var child = InputOption(("remember", "custom"));
        var children = new List<MaaInterface.MaaInterfaceSelectOption> { child };
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCases = ["one"], SubOptions = children };

        Assert.Equal(1, OptionDefaultReset.CountChangedCheckboxCases(option, definition));
        Assert.True(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Null(option.SelectedCases);
        Assert.Same(children, option.SubOptions);
        Assert.Equal("custom", option.SubOptions![0].Data!["remember"]);
        Assert.False(OptionDefaultReset.CanRestoreCheckbox(option, definition));
    }

    [Fact]
    public void CheckboxComparisonUsesValidNamesAndIgnoresOrderAndDuplicates()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one"},{"name":"two"}],"default_case":["one","two"]}
            """);
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCases = ["two", "removed", "one", "one"] };

        Assert.Equal(0, OptionDefaultReset.CountChangedCheckboxCases(option, definition));
        Assert.False(OptionDefaultReset.CanRestoreCheckbox(option, definition));
        Assert.False(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Contains("removed", option.SelectedCases);
    }

    [Fact]
    public void CheckboxRestorationClearsSelectionAndPreservesOtherData()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one"},{"name":"two"}],"default_case":["one"]}
            """);
        var option = InputOption(("unrelated", "keep"));
        option.SelectedCases = ["two", "removed"];

        Assert.Equal(2, OptionDefaultReset.CountChangedCheckboxCases(option, definition));
        Assert.True(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Null(option.SelectedCases);
        Assert.Equal(["one"], OptionValues.GetSelectedCases(definition, option));
        Assert.Equal("keep", option.Data!["unrelated"]);
        Assert.Equal(["one"], definition.DefaultCases);
    }

    [Fact]
    public void UninitializedCheckboxSelectionUsesItsResourceDefault()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one"}],"default_case":["one"]}
            """);
        var option = new MaaInterface.MaaInterfaceSelectOption();

        Assert.False(OptionDefaultReset.CanRestoreCheckbox(option, definition));
        Assert.Null(option.SelectedCases);
    }

    [Theory]
    [InlineData("hotkey")]
    [InlineData("select")]
    [InlineData("switch")]
    public void GroupHelpersDoNotRestoreOtherOptionTypes(string type)
    {
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = type,
            Inputs = [new() { Name = "value", Default = "default" }],
            Cases = [new() { Name = "one" }],
            DefaultCases = ["one"]
        };
        var option = InputOption(("value", "custom"));
        option.SelectedCases = [];

        Assert.Equal(0, OptionDefaultReset.CountRestorableInputs(definition));
        Assert.Equal(0, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.False(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Equal("custom", option.Data!["value"]);
        Assert.Empty(option.SelectedCases);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", false)]
    public void SelectionRestoresOnlyWhenItDiffersFromTheDefault(string saved, bool restorable)
    {
        var definition = Select("B");
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = saved, Index = saved == "A" ? 0 : 1 };

        Assert.Equal(restorable, OptionDefaultReset.CanRestoreSelection(option, definition));
        Assert.Equal(restorable, OptionDefaultReset.RestoreSelection(option, definition));
        if (!restorable) return;
        Assert.Null(option.SelectedCase);
        Assert.Null(option.Index);
        Assert.True(OptionValues.TryGetCaseIndex(definition, option, out var index, out _));
        Assert.Equal(1, index);
    }

    [Fact]
    public void UnsetSelectionHasNothingToRestore()
        => Assert.False(OptionDefaultReset.CanRestoreSelection(new MaaInterface.MaaInterfaceSelectOption(), Select("B")));

    [Fact]
    public void UndeclaredSelectionDefaultIsTheFirstCase()
    {
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "B" };

        Assert.True(OptionDefaultReset.RestoreSelection(option, Select(null)));
        Assert.Null(option.SelectedCase);
    }

    [Fact]
    public void InvalidSavedSelectionsCanBeRestored()
    {
        var removed = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "gone", SelectionError = LangKeys.OptionSavedSelectionInvalid };
        var malformed = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>("""{"index":"x"}""")!;
        var outOfRange = new MaaInterface.MaaInterfaceSelectOption { Index = 7 };

        foreach (var option in new[] { removed, malformed, outOfRange })
        {
            Assert.True(OptionDefaultReset.RestoreSelection(option, Select("B")));
            Assert.Null(option.SelectedCase);
            Assert.Null(option.Index);
            Assert.False(option.HasInvalidIndex);
            Assert.Null(option.SelectionError);
        }
    }

    [Fact]
    public void InvalidResourceDefaultIsNeverOfferedAsRestoreTarget()
    {
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "A" };

        Assert.False(OptionDefaultReset.CanRestoreSelection(option, Select("missing")));
        Assert.False(OptionDefaultReset.RestoreSelection(option, Select("missing")));
        Assert.Equal("A", option.SelectedCase);
    }

    private static MaaInterface.MaaInterfaceOption Select(string? defaultCase) => new()
    {
        Type = "select",
        DefaultCases = defaultCase == null ? null : [defaultCase],
        Cases = [new() { Name = "A" }, new() { Name = "B" }]
    };

    private static MaaInterface.MaaInterfaceOption ReadDefinition(string json)
        => JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOption>(json)!;

    private static MaaInterface.MaaInterfaceSelectOption InputOption(params (string Name, string? Value)[] values)
        => new() { Data = values.ToDictionary(item => item.Name, item => item.Value) };

    private static JObject Generate(MaaInterface.MaaInterfaceOption definition, MaaInterface.MaaInterfaceSelectOption option)
        => JObject.Parse(definition.GenerateProcessedPipeline(OptionValues.GetFieldValues(definition, option)));
}
