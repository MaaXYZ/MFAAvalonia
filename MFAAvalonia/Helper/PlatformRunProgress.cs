using System;

namespace MFAAvalonia.Helper;

public readonly record struct RunProgressSnapshot(
    string InstanceId,
    string AppName,
    string State,
    string CurrentTask,
    int Completed,
    int Total,
    bool Indeterminate = false);

public readonly record struct RunLogEntry(
    string InstanceId,
    string Time,
    string Content,
    bool UseMarkdown,
    bool ShowTime,
    uint ForegroundArgb,
    uint BackgroundArgb);

public static class PlatformRunProgress
{
    public static Action<RunProgressSnapshot>? Update { get; set; }
    public static Action<string>? Stop { get; set; }
    public static Action? RequestStop { get; set; }
    public static Action<RunLogEntry>? Log { get; set; }
    public static Action<string>? ClearLogs { get; set; }
}
