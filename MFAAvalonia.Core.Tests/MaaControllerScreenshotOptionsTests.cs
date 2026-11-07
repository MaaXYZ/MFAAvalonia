using MFAAvalonia.Extensions.MaaFW;
using System;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class MaaControllerScreenshotOptionsTests
{
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(720, 1280)]
    [InlineData(1920, 1080)]
    public void ExpandPassesTwoNativeInt32ValuesInWidthHeightOrder(int width, int height)
    {
        var calls = 0;
        var expectedHandle = new IntPtr(123);
        var result = MaaControllerScreenshotOptions.SetScreenshotTargetExpand(expectedHandle, width, height,
            (handle, option, bytes, size) =>
            {
                calls++;
                Assert.Equal(expectedHandle, handle);
                Assert.Equal(8, option);
                Assert.Equal(8UL, size);
                Assert.Equal(8, bytes.Length);
                Assert.Equal(width, BitConverter.ToInt32(bytes, 0));
                Assert.Equal(height, BitConverter.ToInt32(bytes, sizeof(int)));
                return true;
            });

        Assert.True(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExpandPreservesNativeFailureResult()
    {
        Assert.False(MaaControllerScreenshotOptions.SetScreenshotTargetExpand(new IntPtr(123), 1280, 720,
            (_, _, _, _) => false));
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-1, 720)]
    [InlineData(1280, -1)]
    public void InvalidDimensionsNeverReachNativeCode(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaaControllerScreenshotOptions.SetScreenshotTargetExpand(new IntPtr(123), width, height,
                (_, _, _, _) => throw new InvalidOperationException("Native call must not run")));
    }
}
