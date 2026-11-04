using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// Restoring a default clears the saved value, so the option follows the current default again.
/// A value is restorable when it is saved and differs from the default (or is invalid). Undeclared
/// defaults count too: empty text, no checkbox case, the first select case. Password fields and
/// hotkeys are not restorable. The caller refreshes controls, rebuilds the pipeline and saves once.
/// </summary>
public static class OptionDefaultReset
{
    public static bool SupportsRestore(MaaInterface.MaaInterfaceOptionInput input)
        => !input.IsPassword && !string.IsNullOrEmpty(input.Name);

    public static int CountRestorableInputs(MaaInterface.MaaInterfaceOption definition)
        => definition.IsInput ? definition.Inputs?.Count(SupportsRestore) ?? 0 : 0;

    public static bool CanRestoreInput(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceOptionInput input)
        => SupportsRestore(input)
           && option.Data?.TryGetValue(input.Name!, out var value) == true && value != null
           && !OptionValues.IsDefaultField(definition, input.Name!, value);

    public static int CountChangedInputs(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
        => definition.IsInput
            ? definition.Inputs?.Count(input => CanRestoreInput(option, definition, input)) ?? 0
            : 0;

    public static bool RestoreInput(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition,
        MaaInterface.MaaInterfaceOptionInput input)
    {
        if (!CanRestoreInput(option, definition, input)) return false;
        option.Data!.Remove(input.Name!);
        if (option.Data.Count == 0) option.Data = null;
        return true;
    }

    public static int RestoreInputs(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (!definition.IsInput || definition.Inputs == null) return 0;
        var changedCount = 0;
        foreach (var input in definition.Inputs)
        {
            if (RestoreInput(option, definition, input)) changedCount++;
        }
        return changedCount;
    }

    /// <summary>Number of checkbox cases whose checked state changes when restoring.</summary>
    public static int CountChangedCheckboxCases(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (!definition.IsCheckbox || option.SelectedCases == null) return 0;

        var validNames = new HashSet<string>(
            definition.Cases?.Select(item => item.Name).OfType<string>() ?? [],
            StringComparer.Ordinal);
        var current = new HashSet<string>(option.SelectedCases.Where(validNames.Contains), StringComparer.Ordinal);
        current.SymmetricExceptWith((definition.DefaultCases ?? []).Where(validNames.Contains));
        return current.Count;
    }

    public static bool CanRestoreCheckbox(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
        => CountChangedCheckboxCases(option, definition) > 0;

    public static bool RestoreCheckbox(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (!CanRestoreCheckbox(option, definition)) return false;
        option.SelectedCases = null;
        return true;
    }

    /// <summary>
    /// A select/switch is restorable when its saved choice differs from a usable default or is invalid.
    /// An invalid resource default is never offered as a restore target.
    /// </summary>
    public static bool CanRestoreSelection(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (definition.IsInput || definition.IsHotkey || definition.IsCheckbox) return false;
        if (option.SelectedCase == null && option.Index == null && !option.HasInvalidIndex) return false;
        if (!OptionValues.TryGetCaseIndex(definition, null, out var defaultIndex, out _)) return false;
        return !OptionValues.TryGetCaseIndex(definition, option, out var index, out _) || index != defaultIndex;
    }

    public static bool RestoreSelection(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (!CanRestoreSelection(option, definition)) return false;
        option.SelectedCase = null;
        option.Index = null;
        option.SelectionError = null;
        return true;
    }
}
