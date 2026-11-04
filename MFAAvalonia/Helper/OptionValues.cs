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
}
