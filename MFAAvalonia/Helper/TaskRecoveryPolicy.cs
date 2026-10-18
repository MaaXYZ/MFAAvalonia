namespace MFAAvalonia.Helper;

public static class TaskRecoveryPolicy
{
    public const string TargetPackageName = "com.dmm.games.touken.puzzle";

    public static bool ShouldMonitor(
        bool isAndroid,
        bool isAdbController,
        bool canInitializeWithoutDevice,
        bool isSpecialTask)
        => !isAndroid && isAdbController && !canInitializeWithoutDevice && !isSpecialTask;
}
