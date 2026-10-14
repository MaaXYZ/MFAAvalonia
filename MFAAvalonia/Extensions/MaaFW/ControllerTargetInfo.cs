namespace MFAAvalonia.Extensions.MaaFW;

public sealed record MacOSWindowInfo(uint WindowId, string Name)
{
    public override string ToString() => Name;
}

public sealed record WlRootsSocketInfo(string SocketPath)
{
    public override string ToString() => SocketPath;
}
