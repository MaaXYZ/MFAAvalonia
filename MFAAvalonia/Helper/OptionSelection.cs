using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>Resolves client selections against the current PI without guessing renamed keys.</summary>
public static class OptionSelection
{
    public static bool Resolve(MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceSelectOption selection)
    {
        selection.SelectionError = null;
        if (definition.IsInput || definition.IsHotkey || definition.IsCheckbox) return true;

        var cases = definition.Cases;
        if (cases == null || cases.Count == 0 || cases.Any(item => string.IsNullOrEmpty(item.Name))
            || cases.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            return Invalid(selection, LangKeys.OptionCaseDefinitionInvalid);

        if (selection.SelectedCase != null)
        {
            var namedIndex = cases.FindIndex(item => string.Equals(item.Name, selection.SelectedCase, StringComparison.Ordinal));
            if (namedIndex < 0)
                return Invalid(selection, LangKeys.OptionSavedSelectionInvalid);
            selection.Index = namedIndex;
            return true;
        }

        if (selection.HasInvalidIndex)
            return Invalid(selection, LangKeys.OptionSavedSelectionInvalid);

        if (selection.Index is int legacyIndex)
        {
            if (legacyIndex < 0 || legacyIndex >= cases.Count)
                return Invalid(selection, LangKeys.OptionSavedSelectionInvalid);
            // Compatibility interpretation: without a historical definition the old meaning is unknowable.
            selection.SelectedCase = cases[legacyIndex].Name;
            return true;
        }

        var defaultIndex = definition.DefaultCase == null ? 0
            : cases.FindIndex(item => string.Equals(item.Name, definition.DefaultCase, StringComparison.Ordinal));
        if (defaultIndex < 0)
            return Invalid(selection, LangKeys.OptionDefaultCaseUnavailable);
        return SelectIndex(definition, selection, defaultIndex);
    }

    public static bool SelectIndex(MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceSelectOption selection, int index)
    {
        if (definition.Cases == null || index < 0 || index >= definition.Cases.Count
            || string.IsNullOrEmpty(definition.Cases[index].Name)) return false;
        selection.Index = index;
        selection.SelectedCase = definition.Cases[index].Name;
        selection.SelectionError = null;
        return true;
    }

    /// <summary>Initializes every retained branch, including currently hidden selections.</summary>
    public static void ResolveTree(IReadOnlyDictionary<string, MaaInterface.MaaInterfaceOption> definitions,
        IEnumerable<MaaInterface.MaaInterfaceSelectOption>? selections)
    {
        foreach (var selection in selections ?? [])
        {
            if (selection.Name != null && definitions.TryGetValue(selection.Name, out var definition))
                Resolve(definition, selection);
            ResolveTree(definitions, selection.SubOptions);
        }
    }

    /// <summary>Checks only active branches, without changing stored selections or creating controls.</summary>
    public static bool ValidateActive(IReadOnlyDictionary<string, MaaInterface.MaaInterfaceOption> definitions,
        IEnumerable<MaaInterface.MaaInterfaceSelectOption>? selections, out string? error,
        Func<MaaInterface.MaaInterfaceOption, bool>? isApplicable = null,
        Func<MaaInterface.MaaInterfaceOption, MaaInterface.MaaInterfaceSelectOption, string?>? validateSelection = null)
    {
        error = null;
        foreach (var selection in selections ?? [])
        {
            if (selection.Name == null || !definitions.TryGetValue(selection.Name, out var definition)
                || isApplicable?.Invoke(definition) == false) continue;
            var resolved = new MaaInterface.MaaInterfaceSelectOption();
            resolved.CopySelectionFrom(selection);
            if (!Resolve(definition, resolved))
            {
                error = $"{selection.Name}: {resolved.SelectionError.ToLocalization()}";
                return false;
            }
            var valueError = validateSelection?.Invoke(definition, selection);
            if (valueError != null)
            {
                error = $"{selection.Name}: {valueError}";
                return false;
            }

            IEnumerable<MaaInterface.MaaInterfaceOptionCase> activeCases = [];
            if (definition.IsCheckbox)
                activeCases = definition.Cases?.Where(item => item.Name != null
                    && (selection.SelectedCases ?? definition.DefaultCases ?? []).Contains(item.Name)) ?? [];
            else if (!definition.IsInput && !definition.IsHotkey && resolved.Index is int index)
                activeCases = [definition.Cases![index]];
            var activeNames = new HashSet<string>(activeCases.SelectMany(item => item.Option ?? []), StringComparer.Ordinal);
            if (!ValidateActive(definitions, selection.SubOptions?.Where(item => item.Name != null && activeNames.Contains(item.Name)),
                    out error, isApplicable, validateSelection)) return false;
        }
        return true;
    }

    public static bool HasInvalidSelection(IReadOnlyDictionary<string, MaaInterface.MaaInterfaceOption> definitions,
        IEnumerable<MaaInterface.MaaInterfaceSelectOption>? selections,
        Func<MaaInterface.MaaInterfaceOption, bool>? isApplicable = null)
        => !ValidateActive(definitions, selections, out _, isApplicable);

    private static bool Invalid(MaaInterface.MaaInterfaceSelectOption selection, string reason)
    {
        selection.SelectionError = reason;
        return false;
    }
}
