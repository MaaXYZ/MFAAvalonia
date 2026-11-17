using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

internal static class MaaInterfaceActivationHelper
{
    // Shared with TaskQueueViewModel.GetSelectedControllerDefinition: controller identity
    // is its normalized name AND backend. Backend order is only a missing-name fallback.
    public static MaaInterface.MaaResourceController? ResolveControllerDefinition(
        IEnumerable<MaaInterface.MaaResourceController>? controllers,
        MaaControllerTypes controllerType,
        string? controllerName)
    {
        controllerName = NormalizeControllerName(controllerName);
        return controllers?.FirstOrDefault(c => c.ControllerType == controllerType
                && controllerName != null
                && string.Equals(NormalizeControllerName(c.Name), controllerName, StringComparison.OrdinalIgnoreCase))
            ?? controllers?.FirstOrDefault(c => c.ControllerType == controllerType);
    }

    public static string? ResolveControllerName(
        MaaInterface? maaInterface, MaaControllerTypes controllerType, string? controllerName) =>
        ResolveControllerDefinition(maaInterface?.Controller, controllerType, controllerName)?.Name;

    internal static string? NormalizeControllerName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public static bool IsOptionApplicable(
        MaaInterface? maaInterface,
        MaaInterface.MaaInterfaceOption interfaceOption,
        string? currentControllerName,
        string? currentResourceName)
    {
        return MatchesFilter(interfaceOption.Controller, currentControllerName)
               && MatchesFilter(interfaceOption.Resource, currentResourceName);
    }

    public static bool IsTaskSupportedByResource(
        MaaInterface? maaInterface,
        MaaInterface.MaaInterfaceTask? task,
        string? currentResourceName)
    {
        if (task == null)
            return false;

        if (!MatchesFilter(task.Resource, currentResourceName))
            return false;

        if (string.IsNullOrWhiteSpace(currentResourceName))
            return true;

        var currentResource = maaInterface?.Resources.Values.FirstOrDefault(r =>
            r.Name != null && r.Name.Equals(currentResourceName, StringComparison.OrdinalIgnoreCase));

        return IsTaskAllowedByOwner(currentResource?.Task, task.Name);
    }

    public static bool IsTaskSupportedByController(
        MaaInterface? maaInterface,
        MaaInterface.MaaInterfaceTask? task,
        string? currentControllerName)
    {
        if (task == null)
            return false;

        if (!MatchesFilter(task.Controller, currentControllerName))
            return false;

        if (string.IsNullOrWhiteSpace(currentControllerName))
            return true;

        var currentController = maaInterface?.Controller?.FirstOrDefault(c =>
            c.Name != null && c.Name.Equals(currentControllerName, StringComparison.OrdinalIgnoreCase));

        return IsTaskAllowedByOwner(currentController?.Task, task.Name);
    }

    private static bool IsTaskAllowedByOwner(IReadOnlyCollection<string>? allowedTasks, string? taskName)
    {
        if (allowedTasks == null || allowedTasks.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(taskName))
            return false;

        return allowedTasks.Any(name => name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesFilter(IReadOnlyCollection<string>? allowedNames, string? currentName)
    {
        if (allowedNames == null || allowedNames.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(currentName))
            return false;

        return allowedNames.Any(name => name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
    }
}
