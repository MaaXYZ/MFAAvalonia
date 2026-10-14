using System;
using MaaFramework.Binding;

namespace MFAAvalonia.Extensions.MaaFW;

public static class PlatformControllerFactory
{
    public static Func<MaaControllerTypes, MaaController?>? Create { get; set; }

    public static bool CanInitializeWithoutDevice => Create != null;

    public static MaaController? TryCreate(MaaControllerTypes controllerType) => Create?.Invoke(controllerType);
}
