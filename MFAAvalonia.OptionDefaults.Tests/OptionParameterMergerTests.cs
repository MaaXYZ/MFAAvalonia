using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

/// <summary>Option selections to pipeline overrides, independent of a running processor.</summary>
public class OptionParameterMergerTests
{
    private const string OptionsJson = """
        {
          "Mode": {"type":"select","default_case":"B","cases":[
            {"name":"A","pipeline_override":{"N":{"mode":"A"}}},
            {"name":"B","pipeline_override":{"N":{"mode":"B"}},"option":["Count"]}]},
          "Enabled": {"type":"switch","cases":[
            {"name":"Yes","pipeline_override":{"N":{"enabled":true}}},
            {"name":"No","pipeline_override":{"N":{"enabled":false}}}]},
          "Flags": {"type":"checkbox","default_case":["x","y"],"cases":[
            {"name":"x","pipeline_override":{"N":{"x":1}}},
            {"name":"y","pipeline_override":{"N":{"y":1}}},
            {"name":"z","pipeline_override":{"N":{"z":1}}}]},
          "Count": {"type":"input","inputs":[
            {"name":"count","default":"3","pipeline_type":"int","verify":"^\\d+$"},
            {"name":"text","default":""}],
            "pipeline_override":{"N":{"count":"{count}","text":"{text}"}}},
          "Keys": {"type":"hotkey","hotkeys":[{"name":"start","default":"Ctrl+F6"}],
            "pipeline_override":{"N":{"key":"{start}"}}},
          "PcOnly": {"type":"switch","controller":["Win32"],"cases":[
            {"name":"Yes","pipeline_override":{"N":{"pc":true}}},
            {"name":"No"}]}
        }
        """;

    private static readonly MaaInterface Interface = new()
    {
        Option = JsonConvert.DeserializeObject<Dictionary<string, MaaInterface.MaaInterfaceOption>>(OptionsJson)
    };

    [Fact]
    public void SelectedCaseMergesItsOverrideAndActiveSubOption()
    {
        var mode = Select("Mode", "B");
        mode.SubOptions = [Fields("Count", ("count", "5"), ("text", "t"))];

        var output = Merge(mode);

        Assert.Equal(2, output.Count);
        Assert.Equal("B", output[0]["N"]!["mode"]!.Value<string>());
        Assert.Equal(5, output[1]["N"]!["count"]!.Value<long>());
        Assert.Equal("t", output[1]["N"]!["text"]!.Value<string>());
    }

    [Fact]
    public void InactiveSubOptionIsIgnored()
    {
        var mode = Select("Mode", "A");
        mode.SubOptions = [Fields("Count", ("count", "5"), ("text", "t"))];

        var output = Merge(mode);

        Assert.Equal("A", Assert.Single(output)["N"]!["mode"]!.Value<string>());
    }

    [Fact]
    public void MissingActiveSubOptionUsesDefaults()
    {
        var output = Merge(Select("Mode", "B"));

        Assert.Equal(2, output.Count);
        Assert.Equal(3, output[1]["N"]!["count"]!.Value<long>());
        Assert.Equal(string.Empty, output[1]["N"]!["text"]!.Value<string>());
    }

    [Fact]
    public void CheckboxMergesSelectedCasesInDefinitionOrder()
    {
        var output = Merge(Cases("Flags", "z", "x"));

        Assert.Equal(["x", "z"], output.Select(item => ((JObject)item["N"]!).Properties().Single().Name));
    }

    [Fact]
    public void ExplicitEmptyCheckboxMergesNothing()
        => Assert.Empty(Merge(Cases("Flags")));

    [Fact]
    public void InvalidSavedSelectionMergesNothing()
        => Assert.Empty(Merge(Select("Mode", "Gone")));

    [Theory]
    [InlineData("Win32", true)]
    [InlineData("Adb", false)]
    public void ControllerRestrictedOptionOnlyAppliesToItsController(string controller, bool applied)
    {
        var token = new MaaToken();

        OptionParameterMerger.Apply(token, Interface, controller, null, [Select("PcOnly", "Yes")]);

        Assert.Equal(applied ? 1 : 0, JArray.Parse(token.ToString()).Count);
    }

    [Fact]
    public void ScopesMergeFromGlobalToTask()
    {
        var token = new MaaToken();

        OptionParameterMerger.MergeScopes(token, Interface, "Adb", null,
            [Select("Enabled", "Yes")], null, [], [Select("Enabled", "No")]);

        Assert.Equal([true, false], JArray.Parse(token.ToString()).Select(item => item["N"]!["enabled"]!.Value<bool>()));
    }

    [Fact]
    public void HotkeyOptionsProduceNoTaskOverride()
        => Assert.Empty(Merge(Fields("Keys", ("start", "Ctrl+F7"))));

    [Fact]
    public void PreTaskJsonCombinesSavedValuesWithDefaults()
    {
        var output = PreTask(["Mode", "Flags", "Count", "Enabled", "Keys"],
            Select("Mode", "A"), Fields("Count", ("count", "5")));

        Assert.Equal("A", output["Mode"]!.Value<string>());
        Assert.Equal(["x", "y"], output["Flags"]!.Values<string>());
        Assert.Equal("5", output["Count"]!["count"]!.Value<string>());
        Assert.Equal(string.Empty, output["Count"]!["text"]!.Value<string>());
        Assert.Equal("Yes", output["Enabled"]!.Value<string>());
        Assert.Equal("Ctrl+F6", output["Keys"]!["start"]!.Value<string>());
    }

    [Fact]
    public void PreTaskJsonSkipsOptionsForOtherControllers()
        => Assert.Null(PreTask(["PcOnly"])["PcOnly"]);

    [Fact]
    public void PreTaskJsonRejectsInvalidSavedSelection()
        => Assert.Throws<InvalidOperationException>(() => PreTask(["Mode"], Select("Mode", "Gone")));

    // Baseline behaviour that stage 1 of the option-default layering changes.

    [Fact]
    public void Baseline_CheckboxWithoutSavedValueMergesNothing()
        => Assert.Empty(Merge(new MaaInterface.MaaInterfaceSelectOption { Name = "Flags" }));

    [Fact]
    public void Baseline_MissingFieldWithEmptyDefaultKeepsPlaceholder()
    {
        var output = Merge(Fields("Count", ("count", "5")));

        Assert.Equal("{text}", Assert.Single(output)["N"]!["text"]!.Value<string>());
    }

    [Fact]
    public void Baseline_PreTaskNullFieldOverridesDefault()
    {
        var output = PreTask(["Count"], Fields("Count", ("count", null)));

        Assert.Equal(JTokenType.Null, output["Count"]!["count"]!.Type);
    }

    [Fact]
    public void Baseline_InputValidationTreatsMissingFieldAsEmpty()
        => Assert.False(Interface.Option!["Count"].ValidateAllInputs(new Dictionary<string, string?>()).IsValid);

    [Fact]
    public void Baseline_MergingWritesDefaultsIntoSelections()
    {
        var mode = new MaaInterface.MaaInterfaceSelectOption { Name = "Mode" };
        var count = new MaaInterface.MaaInterfaceSelectOption { Name = "Count" };

        Merge(mode);
        Merge(count);

        Assert.Equal("B", mode.SelectedCase);
        Assert.Equal("3", count.Data!["count"]);
    }

    private static JArray Merge(params MaaInterface.MaaInterfaceSelectOption[] options)
    {
        var token = new MaaToken();
        OptionParameterMerger.Apply(token, Interface, "Adb", null, options.ToList());
        return JArray.Parse(token.ToString());
    }

    private static JObject PreTask(string[] names, params MaaInterface.MaaInterfaceSelectOption[] saved)
        => JObject.Parse(OptionParameterMerger.BuildPreTaskOptionJson(Interface,
            saved.ToDictionary(option => option.Name!), names, "Adb", null));

    private static MaaInterface.MaaInterfaceSelectOption Select(string name, string selectedCase)
        => new() { Name = name, SelectedCase = selectedCase };

    private static MaaInterface.MaaInterfaceSelectOption Cases(string name, params string[] cases)
        => new() { Name = name, SelectedCases = cases.ToList() };

    private static MaaInterface.MaaInterfaceSelectOption Fields(string name, params (string Name, string? Value)[] values)
        => new() { Name = name, Data = values.ToDictionary(item => item.Name, item => item.Value) };
}
