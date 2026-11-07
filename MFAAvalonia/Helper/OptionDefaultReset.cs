using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>
/// Restores explicitly declared resource defaults without changing unrelated option data.
/// The caller is responsible for refreshing controls, generating the pipeline, and saving once.
/// </summary>
public static class OptionDefaultReset
{
    public static bool HasInputDefault(MaaInterface.MaaInterfaceOptionInput input)
        => !input.IsPassword && !string.IsNullOrEmpty(input.Name) && input.Default != null;

    public static int CountInputDefaults(MaaInterface.MaaInterfaceOption definition)
        => definition.IsInput ? definition.Inputs?.Count(HasInputDefault) ?? 0 : 0;

    public static bool CanRestoreInput(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOptionInput input)
    {
        if (!HasInputDefault(input)) return false;
        if (option.Data?.TryGetValue(input.Name!, out var value) != true || value == null)
            return true;

        // Boolean defaults may deserialize as "True", while the switch stores "true".
        // Only compare valid Boolean values semantically; invalid values must remain restorable.
        if (string.Equals(input.PipelineType, "bool", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var currentBoolean)
            && bool.TryParse(input.Default, out var defaultBoolean))
            return currentBoolean != defaultBoolean;

        // Preserve integer formatting and distinguish literal "null" from ExplicitNullMarker.
        return !string.Equals(value, input.Default, StringComparison.Ordinal);
    }

    public static int CountChangedInputs(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
        => definition.IsInput
            ? definition.Inputs?.Count(input => CanRestoreInput(option, input)) ?? 0
            : 0;

    public static bool RestoreInput(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOptionInput input)
    {
        if (!CanRestoreInput(option, input)) return false;
        option.Data ??= new Dictionary<string, string?>();
        option.Data[input.Name!] = input.Default;
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
            if (RestoreInput(option, input)) changedCount++;
        }
        return changedCount;
    }

    public static bool HasCheckboxDefault(MaaInterface.MaaInterfaceOption definition)
        => definition.IsCheckbox && definition.DefaultCases != null;

    public static int CountChangedCheckboxCases(
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption definition)
    {
        if (!HasCheckboxDefault(definition)) return 0;

        var validNames = new HashSet<string>(
            definition.Cases?.Select(item => item.Name).OfType<string>() ?? [],
            StringComparer.Ordinal);
        var current = new HashSet<string>(
            (option.SelectedCases ?? definition.DefaultCases!).Where(validNames.Contains),
            StringComparer.Ordinal);
        var defaults = definition.DefaultCases!.Where(validNames.Contains);
        current.SymmetricExceptWith(defaults);
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
        option.SelectedCases = new List<string>(definition.DefaultCases!);
        return true;
    }
}
