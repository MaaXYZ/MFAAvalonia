using MaaFramework.Binding;
using System;

namespace MFAAvalonia.Extensions.MaaFW;

internal static class MaaControllerScreenshotOptions
{
    // Maa.Framework.Binding 5.10 rejects the newer option in SetOption<T> before
    // calling native code. Use its existing interop entry point with int32_t[2].
    internal static bool SetScreenshotTargetExpand(this MaaController controller, int width, int height)
    {
        var result = SetScreenshotTargetExpand(controller.Handle, width, height,
            MaaFramework.Binding.Interop.Native.MaaController.MaaControllerSetOption);
        GC.KeepAlive(controller);
        return result;
    }

    internal static bool SetScreenshotTargetExpand(nint handle, int width, int height,
        Func<nint, int, byte[], ulong, bool> setOption)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        const int screenshotTargetExpand = 8;
        var dimensions = new[] { width, height };
        var bytes = new byte[2 * sizeof(int)];
        Buffer.BlockCopy(dimensions, 0, bytes, 0, bytes.Length);
        return setOption(handle, screenshotTargetExpand, bytes, (ulong)bytes.Length);
    }
}
