using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public class OptionDefaultResetTests
{
    [Fact]
    public void MissingDefaultDoesNotMutateOption()
    {
        var input = new MaaInterface.MaaInterfaceOptionInput { Name = "amount" };
        var option = new MaaInterface.MaaInterfaceSelectOption();

        Assert.False(OptionDefaultReset.HasInputDefault(input));
        Assert.False(OptionDefaultReset.CanRestoreInput(option, input));
        Assert.False(OptionDefaultReset.RestoreInput(option, input));
        Assert.Null(option.Data);
    }

    [Fact]
    public void ExplicitEmptyDefaultRestoresToEmptyString()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"text","default":""}],
             "pipeline_override":{"Test":{"value":"{text}"}}}
            """);
        var option = InputOption(("text", "custom"));

        Assert.True(OptionDefaultReset.HasInputDefault(definition.Inputs![0]));
        Assert.Equal(1, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal(string.Empty, option.Data!["text"]);
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
        Assert.Equal(expectedCanRestore, OptionDefaultReset.CanRestoreInput(option, input));
        Assert.Equal(expectedCanRestore, OptionDefaultReset.RestoreInput(option, input));
        Assert.False(OptionDefaultReset.CanRestoreInput(option, input));
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
        Assert.Equal("007", option.Data!["amount"]);
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
        Assert.Equal("null", option.Data!["text"]);
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

        Assert.True(OptionDefaultReset.RestoreInput(option, definition.Inputs![0]));
        Assert.Equal("one", option.Data!["first"]);
        Assert.Equal("also changed", option.Data["second"]);
        Assert.Equal("retain", option.Data["unknown"]);
        Assert.Equal(["keep-case"], option.SelectedCases);
        Assert.Same(children, option.SubOptions);
        Assert.Same(child, option.SubOptions![0]);
        Assert.Equal("keep", child.Data!["child"]);
    }

    [Fact]
    public void GroupRestoreChangesOnlyFieldsWithExplicitDefaults()
    {
        var definition = ReadDefinition("""
            {"type":"input","inputs":[{"name":"first","default":"one"},{"name":"second","default":""},
             {"name":"keep"},{"default":"unnamed"}]}
            """);
        var option = InputOption(("first", "changed"), ("second", "changed"), ("keep", "custom"));

        Assert.Equal(2, OptionDefaultReset.CountInputDefaults(definition));
        Assert.Equal(2, OptionDefaultReset.CountChangedInputs(option, definition));
        Assert.Equal(2, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal("one", option.Data!["first"]);
        Assert.Equal(string.Empty, option.Data["second"]);
        Assert.Equal("custom", option.Data["keep"]);
        Assert.Equal(3, option.Data.Count);
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
        TaskLoader.SetDefaultOptionValue(new MaaInterface
        {
            Option = new Dictionary<string, MaaInterface.MaaInterfaceOption> { ["settings"] = definition }
        }, newTask);

        Assert.Equal(4, OptionDefaultReset.RestoreInputs(option, definition));
        foreach (var input in definition.Inputs!)
            Assert.Equal(input.Default, option.Data![input.Name!]);
        Assert.Equal(newTask.Data, option.Data);
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

        Assert.Equal(52, OptionDefaultReset.CountInputDefaults(definition));
        Assert.Equal(51, OptionDefaultReset.CountChangedInputs(option, definition));
        Assert.Equal(51, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.Equal(0, OptionDefaultReset.CountChangedInputs(option, definition));
    }

    [Fact]
    public void MissingCheckboxDefaultDoesNotRestoreOrCreateSelection()
    {
        var definition = ReadDefinition("""{"type":"checkbox","cases":[{"name":"one"}]}""");
        var option = new MaaInterface.MaaInterfaceSelectOption();

        Assert.False(OptionDefaultReset.HasCheckboxDefault(definition));
        Assert.False(OptionDefaultReset.CanRestoreCheckbox(option, definition));
        Assert.False(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Null(option.SelectedCases);
    }

    [Fact]
    public void ExplicitEmptyCheckboxDefaultClearsSelectionWithoutRemovingSubOptions()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one","option":["child"]}],"default_case":[]}
            """);
        var child = InputOption(("remember", "custom"));
        var children = new List<MaaInterface.MaaInterfaceSelectOption> { child };
        var option = new MaaInterface.MaaInterfaceSelectOption { SelectedCases = ["one"], SubOptions = children };

        Assert.True(OptionDefaultReset.HasCheckboxDefault(definition));
        Assert.Equal(1, OptionDefaultReset.CountChangedCheckboxCases(option, definition));
        Assert.True(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Empty(option.SelectedCases!);
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
    public void CheckboxRestorationCopiesDefaultsAndPreservesOtherData()
    {
        var definition = ReadDefinition("""
            {"type":"checkbox","cases":[{"name":"one"},{"name":"two"}],"default_case":["one"]}
            """);
        var option = InputOption(("unrelated", "keep"));
        option.SelectedCases = ["two", "removed"];

        Assert.Equal(2, OptionDefaultReset.CountChangedCheckboxCases(option, definition));
        Assert.True(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Equal(definition.DefaultCases, option.SelectedCases);
        Assert.NotSame(definition.DefaultCases, option.SelectedCases);
        Assert.Equal("keep", option.Data!["unrelated"]);
        option.SelectedCases!.Add("two");
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

        Assert.Equal(0, OptionDefaultReset.CountInputDefaults(definition));
        Assert.Equal(0, OptionDefaultReset.RestoreInputs(option, definition));
        Assert.False(OptionDefaultReset.HasCheckboxDefault(definition));
        Assert.False(OptionDefaultReset.RestoreCheckbox(option, definition));
        Assert.Equal("custom", option.Data!["value"]);
        Assert.Empty(option.SelectedCases);
    }

    private static MaaInterface.MaaInterfaceOption ReadDefinition(string json)
        => JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOption>(json)!;

    private static MaaInterface.MaaInterfaceSelectOption InputOption(params (string Name, string? Value)[] values)
        => new() { Data = values.ToDictionary(item => item.Name, item => item.Value) };

    private static JObject Generate(MaaInterface.MaaInterfaceOption definition, MaaInterface.MaaInterfaceSelectOption option)
        => JObject.Parse(definition.GenerateProcessedPipeline(
            option.Data!.Where(item => item.Value != null).ToDictionary(item => item.Key, item => item.Value!)));
}
