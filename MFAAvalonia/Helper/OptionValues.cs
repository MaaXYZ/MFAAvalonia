using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// Effective option values: a valid saved value wins, otherwise the current resource default.
/// Reading never writes to the selection.
/// </summary>
public static class OptionValues
{
    /// <summary>
    /// Case index for select/switch. Precedence: saved case name, then a legal legacy index, then the
    /// declared default (first case when undeclared). An invalid saved value never falls back to the default.
    /// </summary>
    public static bool TryGetCaseIndex(MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceSelectOption? selection, out int index, out string? error)
    {
        index = -1;
        error = null;
        var cases = definition.Cases;
        if (cases == null || cases.Count == 0 || cases.Any(item => string.IsNullOrEmpty(item.Name))
            || cases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            error = LangKeys.OptionCaseDefinitionInvalid;
            return false;
        }

        if (selection?.SelectedCase is { } savedName)
        {
            index = cases.FindIndex(item => string.Equals(item.Name, savedName, StringComparison.Ordinal));
            if (index >= 0) return true;
            error = LangKeys.OptionSavedSelectionInvalid;
            return false;
        }

        if (selection?.HasInvalidIndex == true)
        {
            error = LangKeys.OptionSavedSelectionInvalid;
            return false;
        }

        if (selection?.Index is int legacyIndex)
        {
            if (legacyIndex >= 0 && legacyIndex < cases.Count)
            {
                index = legacyIndex;
                return true;
            }
            error = LangKeys.OptionSavedSelectionInvalid;
            return false;
        }

        index = definition.DefaultCase == null ? 0
            : cases.FindIndex(item => string.Equals(item.Name, definition.DefaultCase, StringComparison.Ordinal));
        if (index >= 0) return true;
        error = LangKeys.OptionDefaultCaseUnavailable;
        return false;
    }

    /// <summary>Checkbox selection; <c>[]</c> is an explicit empty choice, only <c>null</c> follows the default.</summary>
    public static IReadOnlyList<string> GetSelectedCases(MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceSelectOption? selection)
        => selection?.SelectedCases ?? definition.DefaultCases ?? [];

    /// <summary>
    /// Input/hotkey field values. Saved non-null values win; every other declared field takes its default,
    /// or an empty string when none is declared. Saved keys outside the definition are kept.
    /// </summary>
    public static Dictionary<string, string> GetFieldValues(MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceSelectOption? selection)
    {
        var values = new Dictionary<string, string>();
        if (definition.IsHotkey)
        {
            foreach (var hotkey in definition.Hotkeys ?? [])
                if (!string.IsNullOrEmpty(hotkey.Name)) values[hotkey.Name] = hotkey.Default ?? string.Empty;
        }
        else
        {
            foreach (var input in definition.Inputs ?? [])
                if (!string.IsNullOrEmpty(input.Name)) values[input.Name] = input.Default ?? string.Empty;
        }

        if (selection?.Data != null)
            foreach (var (key, value) in selection.Data)
                if (value != null) values[key] = value;
        return values;
    }

    /// <summary>
    /// Removes saved values that equal the current default so they follow future default changes, and
    /// writes a legal legacy index back as its case name. Applies to the option and all retained children,
    /// active or hidden. Values without a definition, invalid values and password fields are kept as they are.
    /// </summary>
    /// <returns>The number of saved values that now follow the default.</returns>
    public static int Normalize(IReadOnlyDictionary<string, MaaInterface.MaaInterfaceOption> definitions,
        MaaInterface.MaaInterfaceSelectOption selection)
    {
        var removed = 0;
        if (selection.Name != null && definitions.TryGetValue(selection.Name, out var definition))
            removed += NormalizeValue(definition, selection);
        foreach (var child in selection.SubOptions ?? [])
            removed += Normalize(definitions, child);
        return removed;
    }

    private static int NormalizeValue(MaaInterface.MaaInterfaceOption definition, MaaInterface.MaaInterfaceSelectOption selection)
    {
        if (definition.IsCheckbox)
        {
            if (selection.SelectedCases == null) return 0;
            var valid = new HashSet<string>(definition.Cases?.Select(item => item.Name).OfType<string>() ?? [], StringComparer.Ordinal);
            var saved = new HashSet<string>(selection.SelectedCases.Where(valid.Contains), StringComparer.Ordinal);
            if (!saved.SetEquals((definition.DefaultCases ?? []).Where(valid.Contains))) return 0;
            selection.SelectedCases = null;
            return 1;
        }

        if (definition.IsInput || definition.IsHotkey)
        {
            if (selection.Data == null) return 0;
            var removed = 0;
            foreach (var (key, value) in selection.Data.ToList())
            {
                if (value == null)
                    selection.Data.Remove(key);
                else if (IsDefaultField(definition, key, value))
                {
                    selection.Data.Remove(key);
                    removed++;
                }
            }
            if (selection.Data.Count == 0) selection.Data = null;
            return removed;
        }

        if ((selection.SelectedCase == null && selection.Index == null)
            || !TryGetCaseIndex(definition, selection, out var index, out _))
            return 0;
        if (TryGetCaseIndex(definition, null, out var defaultIndex, out _) && index == defaultIndex)
        {
            selection.SelectedCase = null;
            selection.Index = null;
            return 1;
        }
        // Keep the choice by name; the derived index stays for older readers.
        selection.SelectedCase = definition.Cases![index].Name;
        selection.Index = index;
        return 0;
    }

    private static bool IsDefaultField(MaaInterface.MaaInterfaceOption definition, string name, string value)
    {
        if (definition.IsHotkey)
        {
            var hotkey = definition.Hotkeys?.FirstOrDefault(item => item.Name == name);
            return hotkey != null && string.Equals(value, hotkey.Default ?? string.Empty, StringComparison.Ordinal);
        }

        var input = definition.Inputs?.FirstOrDefault(item => item.Name == name);
        if (input == null || input.IsPassword) return false;
        var defaultValue = input.Default ?? string.Empty;
        // Boolean defaults may deserialize as "True" while the switch stores "true"; compare valid booleans by meaning.
        if (string.Equals(input.PipelineType, "bool", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var current) && bool.TryParse(defaultValue, out var expected))
            return current == expected;
        // Keep integer formatting such as "007" and the explicit null marker as user values.
        return string.Equals(value, defaultValue, StringComparison.Ordinal);
    }
}
