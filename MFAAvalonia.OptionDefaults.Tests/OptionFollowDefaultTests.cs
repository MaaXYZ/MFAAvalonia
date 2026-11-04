using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

/// <summary>Saved values equal to the current default are dropped so they follow later default changes.</summary>
public class OptionFollowDefaultTests
{
    private static Dictionary<string, MaaInterface.MaaInterfaceOption> Definitions() => new()
    {
        ["mode"] = new() { Type = "select", DefaultCases = ["B"], Cases = [new() { Name = "A" }, new() { Name = "B", Option = ["child"] }] },
        ["first"] = new() { Type = "switch", Cases = [new() { Name = "Yes" }, new() { Name = "No" }] },
        ["flags"] = new() { Type = "checkbox", DefaultCases = ["x", "y"], Cases = [new() { Name = "x" }, new() { Name = "y" }, new() { Name = "z" }] },
        ["none"] = new() { Type = "checkbox", DefaultCases = [], Cases = [new() { Name = "x" }] },
        ["child"] = new()
        {
            Type = "input",
            Inputs =
            [
                new() { Name = "count", Default = "3", PipelineType = "int" },
                new() { Name = "text", Default = "" },
                new() { Name = "enabled", Default = "True", PipelineType = "bool" },
                new() { Name = "secret", Password = true }
            ]
        },
        ["keys"] = new() { Type = "hotkey", Hotkeys = [new() { Name = "start", Default = "Ctrl+F6" }] }
    };

    [Theory]
    [InlineData("B", null, true)]
    [InlineData("A", "A", false)]
    public void SelectEqualToDefaultIsDropped(string saved, string? remaining, bool followed)
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "mode", SelectedCase = saved, Index = 5 };

        Assert.Equal(followed ? 1 : 0, OptionValues.Normalize(Definitions(), selection));

        Assert.Equal(remaining, selection.SelectedCase);
        Assert.Equal(followed ? null : 0, selection.Index);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(0, "A", 0)]
    public void LegalLegacyIndexIsDroppedOrWrittenAsName(int legacyIndex, string? name, int? index)
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "mode", Index = legacyIndex };

        OptionValues.Normalize(Definitions(), selection);

        Assert.Equal(name, selection.SelectedCase);
        Assert.Equal(index, selection.Index);
    }

    [Fact]
    public void SwitchWithoutDeclaredDefaultFollowsTheFirstCase()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "first", SelectedCase = "Yes" };

        Assert.Equal(1, OptionValues.Normalize(Definitions(), selection));
        Assert.Null(selection.SelectedCase);
    }

    [Fact]
    public void InvalidSavedValuesAreKept()
    {
        var removedName = new MaaInterface.MaaInterfaceSelectOption { Name = "mode", SelectedCase = "gone", Index = 1 };
        var malformed = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>("""{"name":"mode","index":"x"}""")!;
        var outOfRange = new MaaInterface.MaaInterfaceSelectOption { Name = "mode", Index = 9 };

        foreach (var selection in new[] { removedName, malformed, outOfRange })
            Assert.Equal(0, OptionValues.Normalize(Definitions(), selection));

        Assert.Equal("gone", removedName.SelectedCase);
        Assert.True(malformed.HasInvalidIndex);
        Assert.Equal(9, outOfRange.Index);
    }

    [Fact]
    public void UnknownDefaultCaseKeepsTheSavedChoice()
    {
        var definitions = Definitions();
        definitions["mode"].DefaultCases = ["missing"];
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "mode", SelectedCase = "B" };

        Assert.Equal(0, OptionValues.Normalize(definitions, selection));
        Assert.Equal("B", selection.SelectedCase);
    }

    [Theory]
    [InlineData(new[] { "y", "x", "x" }, true)]
    [InlineData(new[] { "x", "y", "gone" }, true)]
    [InlineData(new[] { "x" }, false)]
    [InlineData(new string[0], false)]
    public void CheckboxComparesValidCaseSets(string[] saved, bool followed)
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "flags", SelectedCases = saved.ToList() };

        OptionValues.Normalize(Definitions(), selection);

        Assert.Equal(followed, selection.SelectedCases == null);
    }

    [Fact]
    public void ExplicitEmptyCheckboxFollowsAnEmptyDefault()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "none", SelectedCases = [] };

        Assert.Equal(1, OptionValues.Normalize(Definitions(), selection));
        Assert.Null(selection.SelectedCases);
    }

    [Fact]
    public void InputFieldsAreDroppedOnlyWhenEqualToTheirDefault()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "child",
            Data = new()
            {
                ["count"] = "3", ["text"] = "", ["enabled"] = "true", ["secret"] = "", ["extra"] = "kept", ["gone"] = null
            }
        };

        Assert.Equal(3, OptionValues.Normalize(Definitions(), selection));

        Assert.Equal(new Dictionary<string, string?> { ["secret"] = "", ["extra"] = "kept" }, selection.Data);
    }

    [Theory]
    [InlineData("count", "007")]
    [InlineData("count", "0")]
    [InlineData("text", " ")]
    [InlineData("text", MaaInterface.MaaInterfaceOption.ExplicitNullMarker)]
    [InlineData("enabled", "false")]
    [InlineData("enabled", "1")]
    public void InputValuesThatDifferFromTheDefaultAreKept(string field, string value)
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "child", Data = new() { [field] = value } };

        Assert.Equal(0, OptionValues.Normalize(Definitions(), selection));
        Assert.Equal(value, selection.Data![field]);
    }

    [Fact]
    public void EmptiedDataBecomesNull()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "keys", Data = new() { ["start"] = "Ctrl+F6" } };

        Assert.Equal(1, OptionValues.Normalize(Definitions(), selection));
        Assert.Null(selection.Data);
    }

    [Fact]
    public void HiddenAndActiveChildrenAreNormalized()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "mode", SelectedCase = "A",
            SubOptions = [new() { Name = "child", Data = new() { ["count"] = "3", ["text"] = "t" } }]
        };

        Assert.Equal(1, OptionValues.Normalize(Definitions(), selection));

        Assert.Equal("A", selection.SelectedCase);
        Assert.Equal(new Dictionary<string, string?> { ["text"] = "t" }, selection.SubOptions[0].Data);
    }

    [Fact]
    public void OptionsWithoutDefinitionAreUntouched()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "unknown", SelectedCase = "B", Index = 1, SelectedCases = ["x"], Data = new() { ["count"] = "3", ["n"] = null }
        };
        var before = JsonConvert.SerializeObject(selection);

        Assert.Equal(0, OptionValues.Normalize(Definitions(), selection));
        Assert.Equal(before, JsonConvert.SerializeObject(selection));
        Assert.True(selection.Data!.ContainsKey("n"));
    }

    [Fact]
    public void FollowingValuesAreNotWrittenToTheConfiguration()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "mode",
            SubOptions = [new() { Name = "child", Data = new() { ["count"] = null } }, new() { Name = "flags", SelectedCases = [] }]
        };

        var json = JArray.Parse(JsonConvert.SerializeObject(new List<MaaInterface.MaaInterfaceSelectOption> { selection },
            new MaaInterfaceSelectOptionConverter(false)));

        Assert.Equal("""[{"name":"mode","sub_options":[{"name":"child"},{"name":"flags","selected_cases":[]}]}]""",
            json.ToString(Formatting.None));
    }

    [Fact]
    public void CloningKeepsFollowingValuesSparse()
    {
        var task = new MaaInterface.MaaInterfaceTask
        {
            Name = "A", Entry = "A",
            Option = [new() { Name = "mode" }, new() { Name = "child", Data = new() { ["text"] = "mine" } }, new() { Name = "flags", SelectedCases = [] }]
        };

        var clone = task.Clone();

        Assert.Null(clone.Option![0].SelectedCase);
        Assert.Null(clone.Option[0].Index);
        Assert.Equal(new Dictionary<string, string?> { ["text"] = "mine" }, clone.Option[1].Data);
        Assert.Empty(clone.Option[2].SelectedCases!);
    }

    [Fact]
    public void LoadingDropsSavedDefaultsAndFollowsLaterDefaultChanges()
    {
        var catalogue = new MaaInterface
        {
            Task = [new() { Name = "A", Entry = "A", Option = [new() { Name = "mode" }, new() { Name = "child" }] }],
            Option = Definitions()
        };
        var saved = new TaskQueueState
        {
            IsInitialized = true, HasExplicitDeletionState = true,
            Tasks =
            [
                new()
                {
                    Name = "A", Entry = "A",
                    Option = [new() { Name = "mode", SelectedCase = "B", Index = 1 }, new() { Name = "child", Data = new() { ["count"] = "3", ["text"] = "mine" } }]
                }
            ],
            CurrentTasks = ["A<|||>A"],
            ExtraValues = { [ConfigurationKeys.GlobalOptionItems] = new List<MaaInterface.MaaInterfaceSelectOption> { new() { Name = "first", SelectedCase = "Yes" } } }
        };

        var first = TaskQueueReconciler.Reconcile(saved, catalogue, "launch", true, new HashSet<string>());

        var options = first.State.Tasks[0].Option!;
        Assert.Null(options[0].SelectedCase);
        Assert.Equal(new Dictionary<string, string?> { ["text"] = "mine" }, options[1].Data);
        Assert.Null(((List<MaaInterface.MaaInterfaceSelectOption>)first.State.ExtraValues[ConfigurationKeys.GlobalOptionItems]!)[0].SelectedCase);
        Assert.Equal(3, first.FollowingDefaultCount);

        catalogue.Option["mode"].DefaultCases = ["A"];
        catalogue.Option["child"].Inputs![0].Default = "5";
        catalogue.Option["child"].Inputs![1].Default = "theirs";
        var second = TaskQueueReconciler.Reconcile(first.State, catalogue, "launch-2", true, new HashSet<string>());

        var next = second.State.Tasks[0].Option!;
        Assert.True(OptionValues.TryGetCaseIndex(catalogue.Option["mode"], next[0], out var index, out _));
        Assert.Equal("A", catalogue.Option["mode"].Cases![index].Name);
        var values = OptionValues.GetFieldValues(catalogue.Option["child"], next[1]);
        Assert.Equal("5", values["count"]);
        Assert.Equal("mine", values["text"]);
        Assert.Equal(0, second.FollowingDefaultCount);
    }
}
