using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Configuration;

/// <summary>A detached snapshot of the client-owned task configuration.</summary>
public sealed class TaskQueueState
{
    public bool IsInitialized { get; set; }
    public List<MaaInterface.MaaInterfaceTask> Tasks { get; set; } = new();
    public List<string> CurrentTasks { get; set; } = new();
    public HashSet<string> DeletedTaskNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> MissingTaskSessions { get; set; } = new(StringComparer.Ordinal);
    public bool KeepEmpty { get; set; }
    public bool HasExplicitDeletionState { get; set; }
    public bool HasPresetKey { get; set; }
    public string? PresetKey { get; set; }
    public Dictionary<string, object?> ExtraValues { get; set; } = new(StringComparer.Ordinal);

    public TaskQueueState Clone() => new()
    {
        IsInitialized = IsInitialized,
        Tasks = JsonConvert.DeserializeObject<List<MaaInterface.MaaInterfaceTask>>(
            JsonConvert.SerializeObject(Tasks)) ?? new(),
        CurrentTasks = new(CurrentTasks),
        DeletedTaskNames = new(DeletedTaskNames, StringComparer.Ordinal),
        MissingTaskSessions = new(MissingTaskSessions, StringComparer.Ordinal),
        KeepEmpty = KeepEmpty,
        HasExplicitDeletionState = HasExplicitDeletionState,
        HasPresetKey = HasPresetKey,
        PresetKey = PresetKey,
        ExtraValues = ExtraValues.ToDictionary(pair => pair.Key,
            pair => (object?)CloneExtraValue(pair.Key, pair.Value), StringComparer.Ordinal),
    };

    internal static bool IsExtraKey(string key) => key is ConfigurationKeys.GlobalOptionItems
        or ConfigurationKeys.ResourceOptionItems or ConfigurationKeys.ControllerOptionItems;

    internal static object CloneExtraValue(string key, object? value)
    {
        if (!IsExtraKey(key)) throw new ArgumentException("Unsupported task option configuration key.", nameof(key));
        var token = value == null ? JValue.CreateNull() : JToken.FromObject(value);
        if (key == ConfigurationKeys.GlobalOptionItems) return CloneOptions(token);
        if (token is not JObject buckets)
            throw new JsonSerializationException("Task option buckets must be an object.");
        return buckets.Properties().ToDictionary(property => property.Name,
            property => CloneOptions(property.Value), StringComparer.Ordinal);
    }

    private static List<MaaInterface.MaaInterfaceSelectOption> CloneOptions(JToken token)
    {
        if (token is not JArray array)
            throw new JsonSerializationException("Task options must be an array.");
        return array.Select(item =>
        {
            if (item.Type == JTokenType.String && !string.IsNullOrWhiteSpace(item.Value<string>()))
                return new MaaInterface.MaaInterfaceSelectOption { Name = item.Value<string>() };
            ValidateOption(item);
            return item.ToObject<MaaInterface.MaaInterfaceSelectOption>()!;
        }).ToList();
    }

    private static void ValidateOption(JToken token)
    {
        if (token is not JObject option || option["name"]?.Type != JTokenType.String
            || string.IsNullOrWhiteSpace(option["name"]!.Value<string>()))
            throw new JsonSerializationException("Task option must have a valid name.");
        if (option["sub_options"] is { Type: not JTokenType.Null } children)
        {
            if (children is not JArray array)
                throw new JsonSerializationException("Nested task options must be an array.");
            foreach (var child in array) ValidateOption(child);
        }
    }
}
