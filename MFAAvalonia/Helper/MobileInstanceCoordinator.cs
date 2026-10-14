using System;
using MFAAvalonia.Extensions.MaaFW;

namespace MFAAvalonia.Helper;

public static class MobileInstanceCoordinator
{
    public static event EventHandler? CurrentChanged;

    public static bool TrySwitch(string instanceId)
    {
        var manager = MaaProcessorManager.Instance;
        var current = manager.GetViewModel(manager.Current.InstanceId);
        if (current?.IsRunning == true)
            return false;

        manager.EnsureInstanceLoaded(instanceId);
        if (!manager.SwitchCurrent(instanceId))
            return false;

        manager.Current.InitializeData();
        CurrentChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static void NotifyChanged() => CurrentChanged?.Invoke(null, EventArgs.Empty);
}
