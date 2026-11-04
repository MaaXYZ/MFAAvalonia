using MFAAvalonia.Helper;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MFAAvalonia.Configuration;

public sealed class InstanceConfiguration
{
    private readonly string _instanceId;
    private Dictionary<string, object> _config;
    private readonly string? _configFilePath;
    private readonly MFAConfiguration? _legacyConfig;
    private string? _loadError;
    public string? TaskStateError { get; private set; }
    public bool IsTaskStateValid => _loadError == null && TaskStateError == null;
    private const int MaxReportedDeserializationFailures = 1024;
    private static readonly object DeserializationFailureLock = new();
    private static readonly HashSet<DeserializationFailureFingerprint> ReportedDeserializationFailures = new();
    private static readonly Queue<DeserializationFailureFingerprint> DeserializationFailureOrder = new();
    private volatile bool _isDeleted;
    public bool IsDeleted => _isDeleted;

    internal static string InstancesDir => AppPaths.InstancesDirectory;

    public InstanceConfiguration(string instanceId)
    {
        _instanceId = instanceId;
        _config = LoadInstanceConfig();
    }

    internal InstanceConfiguration(string instanceId, string configFilePath, MFAConfiguration legacyConfig)
    {
        _instanceId = instanceId;
        _configFilePath = configFilePath;
        _legacyConfig = legacyConfig;
        _config = LoadInstanceConfig();
    }

    /// <summary>
    /// 获取实例配置文件路径：config/instances/{id}.json
    /// </summary>
    public string GetConfigFilePath() =>
        _configFilePath ?? Path.Combine(InstancesDir, $"{_instanceId}.json");

    public bool ConfigFileExists()
        => File.Exists(GetConfigFilePath());

    public bool HasLocalConfigData()
        => _config.Count > 0;

    /// <summary>
    /// 加载实例独立配置文件
    /// </summary>
    private Dictionary<string, object> LoadInstanceConfig()
    {
        var filePath = GetConfigFilePath();
        _loadError = null;
        TaskStateError = null;
        if (!File.Exists(filePath))
            return new Dictionary<string, object>();
        try
        {
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(filePath),
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None, CheckAdditionalContent = true })
                ?? throw new JsonSerializationException("实例配置必须是 JSON 对象");
        }
        catch (Exception ex)
        {
            _loadError = $"实例配置读取失败 ({ex.GetType().Name})：{filePath}";
            LoggerHelper.Error(_loadError);
            return new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// 保存实例配置到独立文件
    /// </summary>
    private void SaveInstanceConfig()
    {
        if (_isDeleted || _loadError != null) return;

        var directory = Path.GetDirectoryName(GetConfigFilePath());
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        JsonHelper.SaveJson(
            GetConfigFilePath(),
            _config,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));
    }

    private MFAConfiguration GlobalConfig => _legacyConfig ?? ConfigurationManager.Current;

    private bool ShouldPersistFallbackValue()
    {
        if (_isDeleted)
            return false;

        if (_instanceId != "default")
            return true;

        return File.Exists(GetConfigFilePath());
    }

    private void PersistFallbackValue(string key, object? value)
    {
        if (!ShouldPersistFallbackValue() || IsTaskStateKey(key))
            return;

        SetValue(key, value);
    }

    public bool ContainsKey(string key)
        => _config.ContainsKey(key) || GlobalConfig.ContainsKey(key);

    private static bool IsTaskStateKey(string key) => key is ConfigurationKeys.TaskItems
        or ConfigurationKeys.CurrentTasks or ConfigurationKeys.DeletedTaskNames
        or ConfigurationKeys.MissingTaskSessions or ConfigurationKeys.KeepEmptyTaskQueue
        or ConfigurationKeys.InstancePresetKey or ConfigurationKeys.GlobalOptionItems
        or ConfigurationKeys.ResourceOptionItems or ConfigurationKeys.ControllerOptionItems;

    private bool CanWriteKeys(IEnumerable<string> keys)
    {
        if (_isDeleted || _loadError != null) return false;
        if (!keys.Any(IsTaskStateKey)) return true;
        if (TaskStateError == null && TryReadTaskState(out _, out _)) return true;
        LoggerHelper.Error($"任务配置未保存：{TaskStateError}");
        return false;
    }

    private static object MaterializeTaskItems(string key, object value) =>
        key == ConfigurationKeys.TaskItems && value is IEnumerable<MaaInterface.MaaInterfaceTask> tasks
            ? tasks.ToList()
            : TaskQueueState.IsExtraKey(key) ? TaskQueueState.CloneExtraValue(key, value)
            : value;

    /// <summary>
    /// False means invalid, true with IsInitialized=false means genuinely absent.
    /// Reading legacy values never writes a partial migration or removes its source.
    /// </summary>
    public bool TryReadTaskState(out TaskQueueState state, out string? error)
    {
        state = new();
        var currentField = ConfigurationKeys.TaskItems;
        error = _loadError;
        if (error != null)
        {
            TaskStateError = error;
            return false;
        }
        try
        {
            Dictionary<string, object?> ReadExtraValues()
            {
                var extras = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in new[] { ConfigurationKeys.GlobalOptionItems,
                             ConfigurationKeys.ResourceOptionItems, ConfigurationKeys.ControllerOptionItems })
                {
                    currentField = key;
                    if (_config.TryGetValue(key, out var value))
                    {
                        extras[key] = TaskQueueState.CloneExtraValue(key, value);
                        continue;
                    }
                    var legacy = GlobalConfig;
                    if (!legacy.IsLoadedSuccessfully)
                        throw new JsonSerializationException("Legacy task options could not be loaded.");
                    var prefixes = _instanceId == "default"
                        ? new[] { "Instance.default.", string.Empty }
                        : new[] { $"Instance.{_instanceId}.", "Instance.default.", string.Empty };
                    foreach (var candidatePrefix in prefixes)
                    {
                        if (!legacy.Config.TryGetValue(candidatePrefix + key, out value)) continue;
                        extras[key] = TaskQueueState.CloneExtraValue(key, value);
                        break;
                    }
                }
                return extras;
            }

            var source = _config;
            var prefix = string.Empty;
            if (!_config.ContainsKey(ConfigurationKeys.TaskItems))
            {
                var legacy = GlobalConfig;
                if (!legacy.IsLoadedSuccessfully)
                    throw new JsonSerializationException("旧配置未成功读取，不能初始化任务队列");
                var prefixes = _instanceId == "default"
                    ? new[] { $"Instance.{_instanceId}.", string.Empty }
                    : new[] { $"Instance.{_instanceId}.", "Instance.default.", string.Empty };
                var legacyPrefix = prefixes.FirstOrDefault(candidate =>
                    legacy.Config.ContainsKey(candidate + ConfigurationKeys.TaskItems));
                if (legacyPrefix == null)
                {
                    if (_config.Keys.Any(key => IsTaskStateKey(key)
                            && key != ConfigurationKeys.InstancePresetKey && !TaskQueueState.IsExtraKey(key))
                        || prefixes.Any(candidate => new[]
                        {
                            ConfigurationKeys.CurrentTasks, ConfigurationKeys.DeletedTaskNames,
                            ConfigurationKeys.MissingTaskSessions, ConfigurationKeys.KeepEmptyTaskQueue,
                        }.Any(key => legacy.Config.ContainsKey(candidate + key))))
                        throw new JsonSerializationException("任务状态存在，但 TaskItems 缺失");
                    state.ExtraValues = ReadExtraValues();
                    TaskStateError = null;
                    return true;
                }
                source = legacy.Config;
                prefix = legacyPrefix;
            }

            bool TryField(string key, out object value)
            {
                if (_config.TryGetValue(key, out value!)) return true;
                return source.TryGetValue(prefix + key, out value!);
            }

            JToken? Field(string key)
            {
                currentField = key;
                if (!TryField(key, out var value)) return null;
                if (value == null) return JValue.CreateNull();
                // Serialize to detach mutable queues/options before synchronization edits them.
                return JToken.FromObject(value);
            }

            var tasksToken = Field(ConfigurationKeys.TaskItems);
            if (tasksToken is not JArray tasksArray || tasksArray.Any(task =>
                    task is not JObject obj || obj["name"]?.Type != JTokenType.String
                    || string.IsNullOrWhiteSpace(obj["name"]!.Value<string>())))
                throw new JsonSerializationException("TaskItems 必须是包含有效任务名称的数组");
            state.Tasks = tasksArray.ToObject<List<MaaInterface.MaaInterfaceTask>>(
                JsonSerializer.Create(DeserializeSettings))!;
            state.IsInitialized = true;

            List<string> StringList(string key)
            {
                var token = Field(key);
                if (token == null) return new();
                if (token is not JArray array || array.Any(value => value.Type != JTokenType.String))
                    throw new JsonSerializationException($"{key} 必须是字符串数组");
                return array.Values<string>().Select(value => value!).ToList();
            }

            state.CurrentTasks = StringList(ConfigurationKeys.CurrentTasks);
            state.HasExplicitDeletionState = TryField(ConfigurationKeys.DeletedTaskNames, out _);
            state.DeletedTaskNames = new(StringList(ConfigurationKeys.DeletedTaskNames), StringComparer.Ordinal);

            var missing = Field(ConfigurationKeys.MissingTaskSessions);
            if (missing != null)
            {
                if (missing is not JObject sessions || sessions.Properties().Any(property =>
                        string.IsNullOrWhiteSpace(property.Name) || property.Value.Type != JTokenType.String
                        || string.IsNullOrWhiteSpace(property.Value.Value<string>())))
                    throw new JsonSerializationException("MissingTaskSessions 必须是任务名称到启动批次的字典");
                state.MissingTaskSessions = sessions.Properties().ToDictionary(
                    property => property.Name, property => property.Value.Value<string>()!, StringComparer.Ordinal);
            }

            var keepEmpty = Field(ConfigurationKeys.KeepEmptyTaskQueue);
            if (keepEmpty != null && keepEmpty.Type != JTokenType.Boolean)
                throw new JsonSerializationException("KeepEmptyTaskQueue 必须是布尔值");
            state.KeepEmpty = keepEmpty?.Value<bool>() ?? state.Tasks.Count == 0;

            var preset = Field(ConfigurationKeys.InstancePresetKey);
            state.HasPresetKey = preset != null;
            if (preset != null && preset.Type != JTokenType.String && preset.Type != JTokenType.Null)
                throw new JsonSerializationException("InstancePresetKey 必须是字符串或 null");
            state.PresetKey = preset?.Value<string>();
            state.ExtraValues = ReadExtraValues();
            TaskStateError = null;
            return true;
        }
        catch (Exception ex)
        {
            // Exception messages may include option data. Never log deserialized payloads.
            error = $"任务配置读取失败：{currentField} ({ex.GetType().Name})，{GetConfigFilePath()}";
            TaskStateError = error;
            LoggerHelper.Error(error);
            state = new();
            return false;
        }
    }

    /// <summary>Commit the complete queue state, retaining every unrelated configuration key.</summary>
    public bool TryCommitTaskState(TaskQueueState state, out string? error)
    {
        error = TaskStateError ?? _loadError;
        if (_isDeleted)
        {
            error = "实例已经删除，不能保存任务配置";
            return false;
        }
        if (error != null || !TryReadTaskState(out _, out error)) return false;
        try
        {
            if (!state.IsInitialized || state.Tasks.Any(task => task == null || string.IsNullOrWhiteSpace(task.Name))
                || state.CurrentTasks.Any(name => name == null)
                || state.DeletedTaskNames.Any(name => name == null)
                || state.MissingTaskSessions.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            {
                error = "待保存的任务状态无效";
                return false;
            }
            var detached = state.Clone();
            var candidate = new Dictionary<string, object>(_config)
            {
                [ConfigurationKeys.TaskItems] = detached.Tasks,
                [ConfigurationKeys.CurrentTasks] = detached.CurrentTasks,
                [ConfigurationKeys.DeletedTaskNames] = detached.DeletedTaskNames,
                [ConfigurationKeys.MissingTaskSessions] = detached.MissingTaskSessions,
                [ConfigurationKeys.KeepEmptyTaskQueue] = detached.KeepEmpty,
                [ConfigurationKeys.OptionStorageVersion] = 2,
            };
            if (detached.HasPresetKey)
                candidate[ConfigurationKeys.InstancePresetKey] = detached.PresetKey!;
            foreach (var pair in detached.ExtraValues)
                candidate[pair.Key] = pair.Value!;
            if (!TaskConfigurationWriter.TryWrite(GetConfigFilePath(), candidate,
                    !_config.ContainsKey(ConfigurationKeys.DeletedTaskNames), out error,
                    new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false)))
                return false;
            _config = candidate;
            return true;
        }
        catch (Exception ex)
        {
            error = $"任务配置提交失败 ({ex.GetType().Name})：{GetConfigFilePath()}";
            return false;
        }
    }

    public void SetValue(string key, object? value)
    {
        if (value == null || !CanWriteKeys(new[] { key })) return;
        var materialized = MaterializeTaskItems(key, value);
        if (IsTaskStateKey(key))
        {
            SaveTaskValues(new Dictionary<string, object> { [key] = materialized });
            return;
        }
        _config[key] = materialized;
        SaveInstanceConfig();
    }

    private void SaveTaskValues(Dictionary<string, object> values)
    {
        var candidate = new Dictionary<string, object>(_config);
        foreach (var pair in values) candidate[pair.Key] = pair.Value;
        if (TaskConfigurationWriter.TryWrite(GetConfigFilePath(), candidate,
                !_config.ContainsKey(ConfigurationKeys.DeletedTaskNames), out var error,
                new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false)))
            _config = candidate;
        else
            LoggerHelper.Error(error ?? "任务配置未保存");
    }

    public void RemoveValue(string key)
    {
        if (!CanWriteKeys(new[] { key })) return;
        if (IsTaskStateKey(key))
        {
            var candidate = new Dictionary<string, object>(_config);
            if (!candidate.Remove(key)) return;
            if (TaskConfigurationWriter.TryWrite(GetConfigFilePath(), candidate,
                    !_config.ContainsKey(ConfigurationKeys.DeletedTaskNames), out var error,
                    new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false)))
                _config = candidate;
            else
                LoggerHelper.Error(error ?? "任务配置未保存");
            return;
        }
        if (!_config.Remove(key)) return;
        SaveInstanceConfig();
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        if (_loadError != null) return defaultValue;
        // 优先从实例独立配置读取
        if (_config.TryGetValue(key, out var data))
        {
            var result = ConvertValue<T>(data);
            if (result != null) return result;
            return defaultValue;
        }

        // 回退：从全局 config.json 的旧 scoped key 读取（兼容过渡）
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue);
            // 迁移到实例文件；临时 default 实例不落盘，避免重建 default.json
            PersistFallbackValue(key, value);
            return value;
        }

        // 回退：从 default 实例的 scoped key 读取
        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        // 回退：从全局 config.json 的无前缀 key 读取（最早版本兼容）
        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, List<T> whitelist)
    {
        var value = GetValue(key, defaultValue);
        return whitelist.Contains(value) ? value : defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, Dictionary<object, T> options)
    {
        if (_loadError != null) return defaultValue;
        if (_config.TryGetValue(key, out var data))
        {
            if (options != null && options.TryGetValue(data, out var result))
                return result;
            var converted = ConvertValue<T>(data);
            if (converted != null) return converted;
            return defaultValue;
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, options);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, options);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, options);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, T? noValue = default, params JsonConverter[] valueConverters)
    {
        if (_loadError != null) return defaultValue;
        if (_config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (result != null && !result.Equals(noValue))
                    return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
                return defaultValue;
            }
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, noValue, valueConverters);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public T GetValue<T>(string key, T defaultValue, List<T>? noValue = null, params JsonConverter[] valueConverters)
    {
        if (_loadError != null) return defaultValue;
        if (_config.TryGetValue(key, out var data))
        {
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                var result = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data), settings) ?? defaultValue;
                if (noValue == null || !noValue.Contains(result))
                    return result;
            }
            catch (Exception e)
            {
                LoggerHelper.Error($"类型转换失败: {e.Message}");
                return defaultValue;
            }
        }

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.ContainsKey(scopedKey))
        {
            var value = GlobalConfig.GetValue<T>(scopedKey, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.ContainsKey(defaultScopedKey))
            {
                var value = GlobalConfig.GetValue<T>(defaultScopedKey, defaultValue, noValue, valueConverters);
                PersistFallbackValue(key, value);
                return value;
            }
        }

        if (GlobalConfig.ContainsKey(key))
        {
            var value = GlobalConfig.GetValue<T>(key, defaultValue, noValue, valueConverters);
            PersistFallbackValue(key, value);
            return value;
        }

        return defaultValue;
    }

    public bool TryGetValue<T>(string key, out T output, params JsonConverter[] valueConverters)
    {
        if (_loadError != null)
        {
            output = default!;
            return false;
        }
        if (_config.ContainsKey(key))
            return TryGetLocalValue(key, out output, valueConverters);

        // 回退到全局配置
        var scopedKey = $"Instance.{_instanceId}.{key}";
        if (GlobalConfig.TryGetValue(scopedKey, out output, valueConverters))
        {
            PersistFallbackValue(key, output);
            return true;
        }

        if (_instanceId != "default")
        {
            var defaultScopedKey = $"Instance.default.{key}";
            if (GlobalConfig.TryGetValue(defaultScopedKey, out output, valueConverters))
            {
                PersistFallbackValue(key, output);
                return true;
            }
        }

        if (GlobalConfig.TryGetValue(key, out output, valueConverters))
        {
            PersistFallbackValue(key, output);
            return true;
        }

        output = default!;
        return false;
    }

    /// <summary>
    /// 仅从当前实例配置读取指定值；读取失败时记录诊断，不回退或迁移其他配置。
    /// </summary>
    public bool TryGetLocalValue<T>(string key, out T output, params JsonConverter[] valueConverters)
    {
        if (_config.TryGetValue(key, out var data))
        {
            string? serializedData = null;
            try
            {
                var settings = new JsonSerializerSettings();
                foreach (var converter in valueConverters)
                    settings.Converters.Add(converter);
                serializedData = JsonConvert.SerializeObject(data);
                output = JsonConvert.DeserializeObject<T>(serializedData, settings) ?? default!;
                return true;
            }
            catch (Exception ex)
            {
                var converterSignature = string.Join("\0", Array.ConvertAll(valueConverters,
                    converter => converter is null
                        ? "<null>"
                        : converter.GetType().AssemblyQualifiedName ?? converter.GetType().FullName ?? converter.GetType().Name));
                var targetType = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;
                var failureContent = serializedData ??
                    CreateSerializationFailureFingerprint(data, ex);
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(failureContent)));
                var fingerprint = new DeserializationFailureFingerprint(
                    _instanceId, key, contentHash, targetType, converterSignature);
                if (TryRegisterDeserializationFailure(fingerprint))
                {
                    var exceptionSummary = GetRedactedExceptionSummary(ex, serializedData, key);
                    LoggerHelper.Error($"实例配置反序列化失败：实例={SanitizeLogField(_instanceId)}，配置项={SanitizeLogField(key)}，目标类型={SanitizeLogField(typeof(T).FullName ?? typeof(T).Name)}，配置文件={SanitizeLogField(GetConfigFilePath())}，异常类型={SanitizeLogField(ex.GetType().FullName ?? ex.GetType().Name)}，异常摘要={SanitizeLogField(exceptionSummary)}");
                }
            }
        }

        output = default!;
        return false;
    }

    private static bool TryRegisterDeserializationFailure(DeserializationFailureFingerprint fingerprint)
    {
        lock (DeserializationFailureLock)
        {
            if (!ReportedDeserializationFailures.Add(fingerprint))
                return false;

            DeserializationFailureOrder.Enqueue(fingerprint);
            while (DeserializationFailureOrder.Count > MaxReportedDeserializationFailures)
                ReportedDeserializationFailures.Remove(DeserializationFailureOrder.Dequeue());

            return true;
        }
    }

    private readonly record struct DeserializationFailureFingerprint(
        string InstanceId, string Key, string ContentHash, string TargetType, string ConverterSignature);

    private static string CreateSerializationFailureFingerprint(object? value, Exception exception)
    {
        const int maxFingerprintLength = 16_384;
        try
        {
            var builder = new StringBuilder();
            var currentPath = new HashSet<object>(ReferenceEqualityComparer.Instance);
            AppendStableValue(builder, value, currentPath, 0, maxFingerprintLength);
            builder.Append("\0exception:").Append(exception.GetType().AssemblyQualifiedName);
            if (builder.Length > maxFingerprintLength)
                builder.Length = maxFingerprintLength;
            return builder.ToString();
        }
        catch
        {
            return $"unavailable\0{value?.GetType().AssemblyQualifiedName}\0{exception.GetType().AssemblyQualifiedName}\0{exception.Message}";
        }
    }

    private static void AppendStableValue(
        StringBuilder builder, object? value, HashSet<object> currentPath, int depth, int maxLength)
    {
        if (builder.Length >= maxLength)
            return;

        if (value is null)
        {
            builder.Append("null");
            return;
        }

        var type = value.GetType();
        builder.Append(type.AssemblyQualifiedName).Append(':');
        if (value is string || type.IsPrimitive || type.IsEnum || value is decimal
            || value is DateTime || value is DateTimeOffset || value is TimeSpan || value is Guid
            || value is Uri || value is Version)
        {
            builder.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        if (depth >= 16)
        {
            builder.Append("<max-depth>");
            return;
        }

        var tracksReference = !type.IsValueType;
        if (tracksReference && !currentPath.Add(value))
        {
            builder.Append("<cycle>");
            return;
        }

        try
        {
            switch (value)
            {
                case JObject obj:
                    builder.Append('{');
                    foreach (var property in obj.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        builder.Append(property.Name).Append('=');
                        AppendStableValue(builder, property.Value, currentPath, depth + 1, maxLength);
                        builder.Append(';');
                        if (builder.Length >= maxLength)
                            break;
                    }
                    builder.Append('}');
                    break;
                case JArray array:
                    AppendStableSequence(builder, array, currentPath, depth, maxLength);
                    break;
                case JValue tokenValue:
                    builder.Append(Convert.ToString(tokenValue.Value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case IDictionary dictionary:
                    AppendStableDictionary(builder, dictionary, currentPath, depth, maxLength);
                    break;
                case IEnumerable sequence:
                    AppendStableSequence(builder, sequence, currentPath, depth, maxLength);
                    break;
                default:
                    AppendStableMembers(builder, value, currentPath, depth, maxLength);
                    break;
            }
        }
        finally
        {
            if (tracksReference)
                currentPath.Remove(value);
        }
    }

    private static void AppendStableDictionary(
        StringBuilder builder, IDictionary dictionary, HashSet<object> currentPath, int depth, int maxLength)
    {
        var entries = new List<(string Key, object? Value)>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            entries.Add((key, entry.Value));
        }

        builder.Append('{');
        foreach (var (key, itemValue) in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(key).Append('=');
            AppendStableValue(builder, itemValue, currentPath, depth + 1, maxLength);
            builder.Append(';');
            if (builder.Length >= maxLength)
                break;
        }
        builder.Append('}');
    }

    private static void AppendStableSequence(
        StringBuilder builder, IEnumerable sequence, HashSet<object> currentPath, int depth, int maxLength)
    {
        builder.Append('[');
        var count = 0;
        foreach (var item in sequence)
        {
            if (count++ >= 1024)
            {
                builder.Append("<max-items>");
                break;
            }

            AppendStableValue(builder, item, currentPath, depth + 1, maxLength);
            builder.Append(';');
            if (builder.Length >= maxLength)
                break;
        }
        builder.Append(']');
    }

    private static void AppendStableMembers(
        StringBuilder builder, object value, HashSet<object> currentPath, int depth, int maxLength)
    {
        builder.Append('{');
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        foreach (var field in value.GetType().GetFields(flags).OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            builder.Append(field.Name).Append('=');
            try
            {
                AppendStableValue(builder, field.GetValue(value), currentPath, depth + 1, maxLength);
            }
            catch
            {
                builder.Append("<unavailable>");
            }
            builder.Append(';');
            if (builder.Length >= maxLength)
                break;
        }

        foreach (var property in value.GetType().GetProperties(flags)
                     .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (builder.Length >= maxLength)
                break;

            builder.Append(property.Name).Append('=');
            try
            {
                AppendStableValue(builder, property.GetValue(value), currentPath, depth + 1, maxLength);
            }
            catch
            {
                builder.Append("<unavailable>");
            }
            builder.Append(';');
        }
        builder.Append('}');
    }

    private static string GetRedactedExceptionSummary(Exception exception, string? serializedData, string key)
    {
#if DEBUG
        return exception.Message;
#else
        if (serializedData is null)
            return "异常消息因无法检查配置值而省略";

        try
        {
            var configValue = JToken.Parse(serializedData);
            var sensitiveValues = new HashSet<string>(StringComparer.Ordinal);
            CollectSensitiveValues(configValue, IsSensitiveFieldName(key), sensitiveValues);

            var summary = exception.Message;
            foreach (var sensitiveValue in sensitiveValues.OrderByDescending(value => value.Length))
            {
                summary = summary.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal);
                var escapedValue = JsonConvert.ToString(sensitiveValue).Trim('"');
                if (!string.Equals(escapedValue, sensitiveValue, StringComparison.Ordinal))
                    summary = summary.Replace(escapedValue, "[REDACTED]", StringComparison.Ordinal);
            }

            return summary;
        }
        catch
        {
            return "异常消息因无法检查配置值而省略";
        }
#endif
    }

    private static void CollectSensitiveValues(JToken token, bool inheritedSensitive, HashSet<string> values)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                    CollectSensitiveValues(property.Value, inheritedSensitive || IsSensitiveFieldName(property.Name), values);
                break;
            case JArray array:
                foreach (var item in array)
                    CollectSensitiveValues(item, inheritedSensitive, values);
                break;
            case JValue value when inheritedSensitive && value.Type != JTokenType.Null:
                var text = Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(text))
                    values.Add(text);
                break;
        }
    }

    private static bool IsSensitiveFieldName(string fieldName)
    {
        var normalized = fieldName.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static string SanitizeLogField(string value)
    {
        const int maxLength = 512;
        var sanitized = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (char.IsControl(character))
                continue;

            if (sanitized.Length == maxLength)
                break;

            sanitized.Append(character);
        }

        return sanitized.ToString();
    }

    /// <summary>
    /// 从实例配置文件中快速读取指定 key 的字符串值（不创建完整实例）
    /// </summary>
    public static string ReadValueFromFile(string instanceId, string key, string defaultValue = "")
    {
        var filePath = Path.Combine(InstancesDir, $"{instanceId}.json");
        if (!File.Exists(filePath)) return defaultValue;

        try
        {
            var data = JsonHelper.LoadJson(filePath, new Dictionary<string, object>());
            if (data.TryGetValue(key, out var value))
            {
                if (value is string str)
                    return str;

                if (value is JValue jValue)
                    return jValue.ToString();

                if (value != null)
                    return Convert.ToString(value) ?? defaultValue;
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"[实例加载] 读取实例名称失败: {filePath}", ex);
        }

        return defaultValue;
    }

    /// <summary>
    /// 从磁盘重新加载配置（迁移后调用，刷新内存中的过期数据）
    /// </summary>
    public void ReloadFromDisk()
    {
        _config = LoadInstanceConfig();
    }

    /// <summary>
    /// 将当前实例的配置复制到新实例的配置文件（排除实例名称），
    /// 需在新实例创建前调用，确保构造函数能读到完整配置
    /// </summary>
    public void CopyToNewInstance(string targetInstanceId)
    {
        if (!TryReadTaskState(out _, out _)) return;
        if (!Directory.Exists(InstancesDir))
            Directory.CreateDirectory(InstancesDir);

        var data = new Dictionary<string, object>(_config);
        data.Remove(ConfigurationKeys.InstanceName);

        JsonHelper.SaveJson(
            Path.Combine(InstancesDir, $"{targetInstanceId}.json"),
            data,
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false));
    }

    /// <summary>
    /// 批量设置配置（用于迁移，只保存一次）
    /// </summary>
    public void SetValues(Dictionary<string, object> values)
    {
        if (!CanWriteKeys(values.Keys)) return;
        var materialized = values.ToDictionary(pair => pair.Key, pair => MaterializeTaskItems(pair.Key, pair.Value));
        if (values.Keys.Any(IsTaskStateKey))
        {
            SaveTaskValues(materialized);
            return;
        }
        foreach (var kvp in materialized)
        {
            _config[kvp.Key] = kvp.Value;
        }
        SaveInstanceConfig();
    }

    /// <summary>
    /// 删除实例配置文件
    /// </summary>
    public void DeleteConfigFile()
    {
        _isDeleted = true;
        var filePath = GetConfigFilePath();
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// 反序列化时使用的自定义转换器（与 SaveInstanceConfig 保持一致，确保读写对称）
    /// </summary>
    private static readonly JsonSerializerSettings DeserializeSettings = new()
    {
        Converters =
        {
            new MaaInterfaceSelectAdvancedConverter(false),
            new MaaInterfaceSelectOptionConverter(false),
        }
    };

    private static T? ConvertValue<T>(object data)
    {
        try
        {
            if (data is long longValue && typeof(T) == typeof(int))
                return (T)(object)Convert.ToInt32(longValue);

            if (data is T t)
                return t;

            if (data is JArray jArray)
                return JsonConvert.DeserializeObject<T>(jArray.ToString(), DeserializeSettings);

            if (data is JObject jObject)
                return JsonConvert.DeserializeObject<T>(jObject.ToString(), DeserializeSettings);
        }
        catch (Exception e)
        {
            LoggerHelper.Error("在进行类型转换时发生错误!", e);
        }

        return default;
    }
}
