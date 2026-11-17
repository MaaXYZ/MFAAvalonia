using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public class OptionValuesTests
{
    [Fact]
    public void SavedNameWinsOverLegacyIndexAndDefault()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "C", Index = 0 };

        Assert.True(OptionValues.TryGetCaseIndex(Select("B", "A", "B", "C"), selection, out var index, out _));
        Assert.Equal(2, index);
    }

    [Fact]
    public void LegalLegacyIndexWinsOverDefault()
    {
        Assert.True(OptionValues.TryGetCaseIndex(Select("B", "A", "B"),
            new MaaInterface.MaaInterfaceSelectOption { Index = 0 }, out var index, out _));
        Assert.Equal(0, index);
    }

    [Theory]
    [InlineData("B", 1)]
    [InlineData(null, 0)]
    public void MissingSelectionUsesDeclaredDefaultOrFirstCase(string? defaultCase, int expected)
    {
        Assert.True(OptionValues.TryGetCaseIndex(Select(defaultCase, "A", "B"), null, out var index, out _));
        Assert.Equal(expected, index);
    }

    [Fact]
    public void InvalidSavedNameDoesNotFallBackToDefault()
    {
        Assert.False(OptionValues.TryGetCaseIndex(Select("A", "A", "B"),
            new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "Gone" }, out _, out var error));
        Assert.Equal(LangKeys.OptionSavedSelectionInvalid, error);
    }

    [Fact]
    public void MalformedOrOutOfRangeLegacyIndexIsInvalid()
    {
        var malformed = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>("""{"index":"x"}""")!;
        var outOfRange = new MaaInterface.MaaInterfaceSelectOption { Index = 5 };

        Assert.False(OptionValues.TryGetCaseIndex(Select("A", "A"), malformed, out _, out var malformedError));
        Assert.False(OptionValues.TryGetCaseIndex(Select("A", "A"), outOfRange, out _, out var outOfRangeError));
        Assert.Equal(LangKeys.OptionSavedSelectionInvalid, malformedError);
        Assert.Equal(LangKeys.OptionSavedSelectionInvalid, outOfRangeError);
    }

    [Fact]
    public void UnknownDefaultCaseIsReported()
    {
        Assert.False(OptionValues.TryGetCaseIndex(Select("Gone", "A"), null, out _, out var error));
        Assert.Equal(LangKeys.OptionDefaultCaseUnavailable, error);
    }

    [Fact]
    public void ReadingNeverWritesTheSelection()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Index = 0 };

        OptionValues.TryGetCaseIndex(Select("B", "A", "B"), selection, out _, out _);

        Assert.Null(selection.SelectedCase);
        Assert.Null(selection.SelectionError);
    }

    [Fact]
    public void CheckboxNullFollowsDefaultButEmptyListIsKept()
    {
        var definition = new MaaInterface.MaaInterfaceOption { Type = "checkbox", DefaultCases = ["a"] };

        Assert.Equal(["a"], OptionValues.GetSelectedCases(definition, new MaaInterface.MaaInterfaceSelectOption()));
        Assert.Empty(OptionValues.GetSelectedCases(definition, new MaaInterface.MaaInterfaceSelectOption { SelectedCases = [] }));
    }

    [Fact]
    public void FieldValuesOverlaySavedValuesOnDefaults()
    {
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "input",
            Inputs =
            [
                new() { Name = "count", Default = "3" },
                new() { Name = "text" },
                new() { Name = "flag", Default = "true" },
                new() { Name = "secret", Password = true }
            ]
        };
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Data = new Dictionary<string, string?> { ["count"] = "5", ["flag"] = null, ["extra"] = "kept" }
        };

        var values = OptionValues.GetFieldValues(definition, selection);

        Assert.Equal("5", values["count"]);
        Assert.Equal(string.Empty, values["text"]);
        Assert.Equal("true", values["flag"]);
        Assert.Equal(string.Empty, values["secret"]);
        Assert.Equal("kept", values["extra"]);
    }

    [Fact]
    public void HotkeyFieldsUseHotkeyDefaults()
    {
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "hotkey",
            Hotkeys = [new() { Name = "start", Default = "Ctrl+F6" }, new() { Name = "stop" }]
        };

        var values = OptionValues.GetFieldValues(definition, null);

        Assert.Equal("Ctrl+F6", values["start"]);
        Assert.Equal(string.Empty, values["stop"]);
    }

    private static MaaInterface.MaaInterfaceOption Select(string? defaultCase, params string[] cases) => new()
    {
        Type = "select",
        DefaultCases = defaultCase == null ? null : [defaultCase],
        Cases = cases.Select(name => new MaaInterface.MaaInterfaceOptionCase { Name = name }).ToList()
    };
}
