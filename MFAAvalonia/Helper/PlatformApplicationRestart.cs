using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>
/// Platform-specific process restart hook used when an update must reload the app.
/// </summary>
public static class PlatformApplicationRestart
{
    public static Func<Task>? RestartAsync { get; set; }
    public static Func<string, Task>? InstallApkAsync { get; set; }
}
