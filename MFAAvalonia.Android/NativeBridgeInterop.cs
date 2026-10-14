using System.Runtime.InteropServices;

namespace MFAAvalonia.Android;

internal static partial class NativeBridgeInterop
{
    [LibraryImport("mfabridge", EntryPoint = "MfaBridgeConfigure")]
    internal static partial int Configure(uint width, uint height);

    [LibraryImport("mfabridge", EntryPoint = "MfaBridgeSetInputPort")]
    internal static partial int SetInputPort(uint port);

    [LibraryImport("mfabridge", EntryPoint = "MfaBridgeUpdateFrame")]
    internal static partial int UpdateFrame(nint data, uint width, uint height, uint stride);

    [LibraryImport("mfabridge", EntryPoint = "MfaBridgeSafeAgentClientConnect")]
    internal static partial int SafeAgentClientConnect(nint client);

    [LibraryImport("mfabridge", EntryPoint = "MfaBridgeSafeAgentClientDisconnect")]
    internal static partial int SafeAgentClientDisconnect(nint client);

}
