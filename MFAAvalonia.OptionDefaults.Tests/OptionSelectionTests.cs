using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class OptionSelectionTests
{
    [Theory]
    [InlineData("select")]
    [InlineData("switch")]
    public void NamedSelectionSurvivesInsertionReorderingAndDeletionOfOtherCases(string type)
    {
        var definition = Definition("A", "B", "C");
        definition.Type = type;
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", Index = 1 };
        Assert.True(OptionSelection.Resolve(definition, option));
        Assert.Equal("B", option.SelectedCase);
        var restored = RoundTrip(option);

        definition.Cases = Definition("N", "C", "A", "B").Cases;
        Assert.True(OptionSelection.Resolve(definition, restored));
        Assert.Equal(3, restored.Index);
        definition.Cases = Definition("B", "N").Cases;
        Assert.True(OptionSelection.Resolve(definition, restored));
        Assert.Equal(0, restored.Index);
        Assert.Equal("B", restored.SelectedCase);
    }

    [Fact]
    public void MissingNameNeverFallsBackToAnOtherwiseValidIndexAndReselectionClearsError()
    {
        var option = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", Index = 1, SelectedCase = "removed" };
        Assert.False(OptionSelection.Resolve(Definition("A", "B"), option));
        Assert.Equal("removed", option.SelectedCase);
        Assert.Equal(1, option.Index);
        Assert.NotNull(option.SelectionError);
        var restored = RoundTrip(option);
        Assert.False(OptionSelection.Resolve(Definition("B", "A"), restored));
        Assert.True(OptionSelection.SelectIndex(Definition("B", "A"), restored, 0));
        Assert.Equal("B", restored.SelectedCase);
        Assert.Equal(0, restored.Index);
        Assert.Null(restored.SelectionError);
    }

    [Fact]
    public void LegacyIndexIsInterpretedOnceUsingCurrentDefinition()
    {
        var option = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>("""{"name":"choice","index":1}""")!;
        Assert.True(OptionSelection.Resolve(Definition("B", "A"), option));
        Assert.Equal("A", option.SelectedCase);
        var restored = RoundTrip(option);
        Assert.True(OptionSelection.Resolve(Definition("A", "B"), restored));
        Assert.Equal("A", restored.SelectedCase);
        Assert.Equal(0, restored.Index);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("\"1\"")]
    [InlineData("1.0")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("9999999999999999999999999999999999999999999999")]
    public void InvalidLegacyIndexIsRetainedAcrossSerializationAndCopies(string rawIndex)
    {
        var option = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(
            "{\"name\":\"choice\",\"index\":" + rawIndex + "}")!;
        Assert.False(OptionSelection.Resolve(Definition("A", "B"), option));
        var copy = new MaaInterface.MaaInterfaceSelectOption { Name = option.Name };
        copy.CopySelectionFrom(option);
        Assert.Null(copy.SelectedCase);
        var json = JObject.Parse(JsonConvert.SerializeObject(copy));
        Assert.True(JToken.DeepEquals(JToken.Parse(rawIndex), json["index"]));
        Assert.False(OptionSelection.Resolve(Definition("A", "B"), RoundTrip(copy)));
        Assert.True(OptionSelection.SelectIndex(Definition("A", "B"), copy, 1));
        Assert.False(copy.HasInvalidIndex);
        Assert.Equal("B", copy.SelectedCase);
    }

    [Fact]
    public void PiStringReferencesDoNotPretendToBeSavedIndexZero()
    {
        var definitions = new MaaInterface
        {
            Option = new() { ["choice"] = Definition("A", "B") }
        };
        definitions.Option["choice"].DefaultCases = ["B"];
        var task = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceTask>(
            """{"name":"task","entry":"entry","option":["choice"]}""",
            new MaaInterfaceSelectOptionConverter(true))!;
        var option = Assert.Single(task.Option!);
        Assert.Null(option.Index);
        TaskLoader.PrepareOption(definitions, option);
        // Nothing is written: the option follows the declared default "B".
        Assert.Null(option.Index);
        Assert.Null(option.SelectedCase);
        Assert.True(OptionValues.TryGetCaseIndex(definitions.Option["choice"], option, out var index, out _));
        Assert.Equal(1, index);
        var pi = JObject.Parse(JsonConvert.SerializeObject(task, new MaaInterfaceSelectOptionConverter(true)));
        Assert.Equal("choice", pi["option"]![0]!.Value<string>());
        Assert.DoesNotContain("selected_case", pi.ToString());
    }

    [Fact]
    public void HiddenInvalidBranchIsPreservedButBlocksOnlyWhenActivated()
    {
        var definition = Definition("off", "on");
        definition.Cases![1].Option = ["child"];
        var definitions = new Dictionary<string, MaaInterface.MaaInterfaceOption>
        {
            ["parent"] = definition,
            ["child"] = Definition("available")
        };
        var parent = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "parent", SelectedCase = "off", Index = 0,
            SubOptions = [new() { Name = "child", SelectedCase = "removed", Index = 0 }]
        };
        TaskLoader.PrepareOption(new MaaInterface { Option = definitions }, parent);
        Assert.Equal("removed", parent.SubOptions[0].SelectedCase);
        Assert.False(OptionValues.TryGetCaseIndex(definitions["child"], parent.SubOptions[0], out _, out _));
        Assert.True(OptionSelection.ValidateActive(definitions, [parent], out _));
        OptionSelection.SelectIndex(definition, parent, 1);
        Assert.False(OptionSelection.ValidateActive(definitions, [parent], out var error));
        Assert.Contains("child", error);
        Assert.Equal("removed", parent.SubOptions[0].SelectedCase);
        Assert.False(OptionSelection.HasInvalidSelection(definitions, [parent], item => item != definitions["child"]));
    }

    [Fact]
    public void ActiveValidationDoesNotMutateLegacyValues()
    {
        var definitions = new Dictionary<string, MaaInterface.MaaInterfaceOption> { ["choice"] = Definition("A", "B") };
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", Index = 1 };
        var before = JsonConvert.SerializeObject(selection);
        Assert.True(OptionSelection.ValidateActive(definitions, [selection], out _));
        Assert.Equal(before, JsonConvert.SerializeObject(selection));
    }

    [Fact]
    public void AdditionalValueValidationVisitsOnlyApplicableActiveBranches()
    {
        var parentDefinition = Definition("off", "on");
        parentDefinition.Cases![1].Option = ["input"];
        var definitions = new Dictionary<string, MaaInterface.MaaInterfaceOption>
        {
            ["parent"] = parentDefinition,
            ["input"] = new() { Type = "input" }
        };
        var parent = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "parent", SelectedCase = "off", Index = 0,
            SubOptions = [new() { Name = "input", Data = new() { ["amount"] = "invalid" } }]
        };
        var visited = new List<string>();
        string? ValidateValue(MaaInterface.MaaInterfaceOption definition, MaaInterface.MaaInterfaceSelectOption value)
        {
            visited.Add(value.Name!);
            return definition.IsInput && value.Data!["amount"] == "invalid" ? "Invalid amount" : null;
        }

        Assert.True(OptionSelection.ValidateActive(definitions, [parent], out _, validateSelection: ValidateValue));
        Assert.Equal(new[] { "parent" }, visited);
        OptionSelection.SelectIndex(parentDefinition, parent, 1);
        Assert.False(OptionSelection.ValidateActive(definitions, [parent], out var error, validateSelection: ValidateValue));
        Assert.Equal("input: Invalid amount", error);
        Assert.True(OptionSelection.ValidateActive(definitions, [parent], out _,
            definition => !definition.IsInput, ValidateValue));
        Assert.False(OptionSelection.HasInvalidSelection(definitions, [parent]));
    }

    [Fact]
    public void ExplicitInputAndCheckboxValuesAreUnaffected()
    {
        var values = new Dictionary<string, string?> { ["false"] = "false", ["zero"] = "0", ["empty"] = "" };
        var input = new MaaInterface.MaaInterfaceSelectOption { Name = "input", Data = values };
        var checkbox = new MaaInterface.MaaInterfaceSelectOption { Name = "check", SelectedCases = [] };
        Assert.True(OptionSelection.Resolve(new() { Type = "input" }, input));
        Assert.True(OptionSelection.Resolve(new() { Type = "checkbox", DefaultCases = ["A"] }, checkbox));
        Assert.Same(values, input.Data);
        Assert.Empty(RoundTrip(checkbox).SelectedCases!);
        Assert.Equal(values, RoundTrip(input).Data);
        Assert.Null(input.SelectedCase);
        Assert.Null(checkbox.SelectedCase);
    }

    [Fact]
    public void ShareExportAndTaskCloneRetainIdentityAtEveryScopeAndDepth()
    {
        var root = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "choice", Index = 1, SelectedCase = "B",
            SubOptions = [new() { Name = "child", Index = 0, SelectedCase = "childCase" }]
        };
        var task = new MaaInterface.MaaInterfaceTask { Name = "task", Entry = "entry", Option = [root] };
        var copy = task.Clone();
        Assert.Equal("B", copy.Option![0].SelectedCase);
        Assert.Equal("childCase", copy.Option[0].SubOptions![0].SelectedCase);
        var payload = new InstanceConfigSharePayload
        {
            Tasks = [task], GlobalOptions = [root],
            ControllerOptions = new() { ["controller"] = [root] },
            ResourceOptions = new() { ["resource"] = [root] }
        };
        var imported = InstanceConfigShareService.ParseImportText("test",
            InstanceConfigShareService.BuildExportText("test", "instance", payload)).Payload;
        foreach (var selection in new[] { imported.Tasks[0].Option![0], imported.GlobalOptions![0],
                     imported.ControllerOptions!["controller"][0], imported.ResourceOptions!["resource"][0] })
        {
            Assert.Equal("B", selection.SelectedCase);
            Assert.Equal(1, selection.Index);
            Assert.Equal("childCase", selection.SubOptions![0].SelectedCase);
        }
    }

    [Fact]
    public void ClientConverterKeepsPasswordEnvelopesAndExplicitEmptySelections()
    {
        var option = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "password", Data = new() { ["secret"] = "do-not-export-plaintext" },
            SubOptions = [new() { Name = "check", SelectedCases = [] }]
        };
        option.MarkPasswordFields(new() { Type = "input", Inputs = [new() { Name = "secret", Password = true }] });
        var json = JsonConvert.SerializeObject(new List<MaaInterface.MaaInterfaceSelectOption> { option },
            new MaaInterfaceSelectOptionConverter(false));
        Assert.DoesNotContain("do-not-export-plaintext", json);
        var loaded = JsonConvert.DeserializeObject<List<MaaInterface.MaaInterfaceSelectOption>>(json,
            new MaaInterfaceSelectOptionConverter(false))!;
        Assert.Equal("do-not-export-plaintext", loaded[0].Data!["secret"]);
        Assert.Empty(loaded[0].SubOptions![0].SelectedCases!);
    }

    private static MaaInterface.MaaInterfaceSelectOption RoundTrip(MaaInterface.MaaInterfaceSelectOption option)
        => JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(JsonConvert.SerializeObject(option))!;

    private static MaaInterface.MaaInterfaceOption Definition(params string[] cases)
        => new() { Type = "select", Cases = cases.Select(name => new MaaInterface.MaaInterfaceOptionCase { Name = name }).ToList() };
}
