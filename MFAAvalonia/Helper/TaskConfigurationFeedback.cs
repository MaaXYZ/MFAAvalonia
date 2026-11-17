using MFAAvalonia.Configuration;

namespace MFAAvalonia.Helper;

/// <summary>Maps configuration state to user-facing summaries, without exposing diagnostic details.</summary>
public static class TaskConfigurationFeedback
{
    public static string GetSaveFailureKey(InstanceConfiguration configuration) =>
        configuration.IsDeleted ? LangKeys.TaskConfigurationInstanceDeleted
        : !configuration.IsTaskStateValid ? LangKeys.TaskConfigurationReadFailed
        : LangKeys.TaskConfigurationSaveFailed;
}
