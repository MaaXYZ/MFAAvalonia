using MaaFramework.Binding;
using MFAAvalonia.Extensions.MaaFW;
using Xunit;

namespace MFAAvalonia.Core.Tests;

public class AdbControllerSnapshotTests
{
    [Fact]
    public void DeviceSwitchDoesNotChangeParametersCapturedForScreenshotController()
    {
        var selection = new AdbDeviceCoreConfig
        {
            AdbPath = "adb-a",
            AdbSerial = "127.0.0.1:16416",
            Config = "{\"extras\":{\"mumu\":{\"index\":1}}}"
        };
        var mainConnection = AdbControllerSnapshot.Capture(selection);

        selection.AdbPath = "adb-b";
        selection.AdbSerial = "127.0.0.1:16448";
        selection.Config = "{\"extras\":{\"mumu\":{\"index\":2}}}";
        selection.ScreenCap = (AdbScreencapMethods)123;
        selection.Input = (AdbInputMethods)456;

        Assert.Equal("adb-a", mainConnection.AdbPath);
        Assert.Equal("127.0.0.1:16416", mainConnection.AdbSerial);
        Assert.Equal("{\"extras\":{\"mumu\":{\"index\":1}}}", mainConnection.Config);
        Assert.Equal(AdbScreencapMethods.Default, mainConnection.ScreenCap);
        Assert.Equal(AdbInputMethods.Default, mainConnection.Input);
        Assert.NotEqual(mainConnection, AdbControllerSnapshot.Capture(selection));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("serial")]
    [InlineData("screencap")]
    [InlineData("input")]
    [InlineData("config")]
    public void ChangesToConnectionParametersInvalidateSnapshot(string field)
    {
        var selection = new AdbDeviceCoreConfig { AdbSerial = "device-a" };
        var connection = AdbControllerSnapshot.Capture(selection);
        switch (field)
        {
            case "path": selection.AdbPath = "other-adb"; break;
            case "serial": selection.AdbSerial = "device-b"; break;
            case "screencap": selection.ScreenCap = (AdbScreencapMethods)123; break;
            case "input": selection.Input = (AdbInputMethods)456; break;
            case "config": selection.Config = "{\"changed\":true}"; break;
        }
        Assert.NotEqual(connection, AdbControllerSnapshot.Capture(selection));
    }

    [Fact]
    public void DisplayNameAndEquivalentEmptyConfigDoNotInvalidateConnection()
    {
        var selection = new AdbDeviceCoreConfig { AdbSerial = "device-a", Config = "" };
        var connection = AdbControllerSnapshot.Capture(selection);
        selection.Name = "Renamed emulator";
        selection.Config = "{}";
        Assert.Equal(connection, AdbControllerSnapshot.Capture(selection));
    }
}
