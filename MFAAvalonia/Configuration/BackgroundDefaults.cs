using MFAAvalonia.Controls;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Configuration;

/// <summary>Author defaults remain separate from persisted user choices.</summary>
public sealed class BackgroundDefaults
{
    public const string HistoryKey = "LegacyTemplates";
    public const string MigrationKey = "BackgroundDefaultsMigrationVersion";
    public const int MigrationVersion = 1;

    private static readonly Dictionary<string, object> BuiltIns = new(StringComparer.Ordinal)
    {
        [ConfigurationKeys.BackgroundImagePath] = string.Empty,
        [ConfigurationKeys.BackgroundImageExtendToTitleBar] = false,
        [ConfigurationKeys.BackgroundImageOpacity] = 0.2,
        [ConfigurationKeys.BackgroundImageLightOpacity] = 0.16,
        [ConfigurationKeys.BackgroundImagePlacement] = nameof(BackgroundPlacement.Right),
        [ConfigurationKeys.BackgroundImageBlurRadius] = 32.0,
        [ConfigurationKeys.BackgroundImageLightMode] = nameof(ScenicImageMode.Blurred),
        [ConfigurationKeys.BackgroundImageDarkMode] = nameof(ScenicImageMode.SideBlend),
    };

    private readonly Dictionary<string, object> _defaults = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, object>> _history = [];
    private readonly Action<string>? _warning;

    public static BackgroundDefaults Empty { get; } = new();
    public static IEnumerable<string> Keys => BuiltIns.Keys;
    public bool HasMigrationHistory => _history.Count > 0;
    public IEnumerable<string> TemplateKeys => _defaults.Keys;

    private BackgroundDefaults(Action<string>? warning = null) => _warning = warning;

    public static bool IsBackgroundKey(string key) => BuiltIns.ContainsKey(key);
    public static bool IsMetadataKey(string key) => key == HistoryKey || key == MigrationKey;

    public static BackgroundDefaults FromTemplate(JObject template, string dataRoot, Action<string>? warning = null)
    {
        var result = new BackgroundDefaults(warning);
        foreach (var key in Keys)
        {
            if (!template.TryGetValue(key, StringComparison.Ordinal, out var value)) continue;
            if (TryValue(key, value, out var normalized) && IsAllowedTemplatePath(key, normalized, dataRoot))
                result._defaults[key] = normalized;
            else
                warning?.Invoke($"忽略无效背景模板字段：{key}");
        }

        if (template.TryGetValue(HistoryKey, out var history))
        {
            if (history is JArray snapshots)
            {
                foreach (var snapshot in snapshots.OfType<JObject>())
                {
                    var values = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var key in Keys)
                    {
                        if (snapshot.TryGetValue(key, StringComparison.Ordinal, out var value)
                            && TryValue(key, value, out var normalized)
                            && IsAllowedTemplatePath(key, normalized, dataRoot))
                            values[key] = value.DeepClone();
                    }
                    if (values.Count > 0) result._history.Add(values);
                }
            }
            if (!result.HasMigrationHistory)
                warning?.Invoke("LegacyTemplates 没有有效背景历史值，暂不自动迁移。");
        }
        return result;
    }

    public Settings Resolve(IReadOnlyDictionary<string, object> user)
    {
        var hasUserPath = user.TryGetValue(ConfigurationKeys.BackgroundImagePath, out var path)
            && TryValue(ConfigurationKeys.BackgroundImagePath, path, out _);
        var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pair in BuiltIns)
        {
            if (user.TryGetValue(pair.Key, out var value))
            {
                if (TryValue(pair.Key, value, out var valid))
                {
                    resolved[pair.Key] = valid;
                    continue;
                }
                _warning?.Invoke($"忽略无效用户背景字段，保留原配置：{pair.Key}");
            }
            resolved[pair.Key] = (!hasUserPath || pair.Key == ConfigurationKeys.BackgroundImagePath)
                && _defaults.TryGetValue(pair.Key, out var author) ? author : pair.Value;
        }
        return new Settings(
            (string)resolved[ConfigurationKeys.BackgroundImagePath],
            (bool)resolved[ConfigurationKeys.BackgroundImageExtendToTitleBar],
            (double)resolved[ConfigurationKeys.BackgroundImageOpacity],
            (double)resolved[ConfigurationKeys.BackgroundImageLightOpacity],
            Enum.Parse<BackgroundPlacement>((string)resolved[ConfigurationKeys.BackgroundImagePlacement]),
            (double)resolved[ConfigurationKeys.BackgroundImageBlurRadius],
            Enum.Parse<ScenicImageMode>((string)resolved[ConfigurationKeys.BackgroundImageLightMode]),
            Enum.Parse<ScenicImageMode>((string)resolved[ConfigurationKeys.BackgroundImageDarkMode]),
            Keys.Any(user.ContainsKey));
    }

    public Change? CreateMigration(IReadOnlyDictionary<string, object> user)
    {
        if (!HasMigrationHistory || ShouldSkipMigration(user)) return null;
        var next = new Dictionary<string, object>(user, StringComparer.Ordinal);
        var removed = new List<string>();
        var hasPath = user.TryGetValue(ConfigurationKeys.BackgroundImagePath, out var rawPath)
            && TryValue(ConfigurationKeys.BackgroundImagePath, rawPath, out var _);
        var path = hasPath ? (string)Unwrap(rawPath!)! : null;
        var customImage = !string.IsNullOrEmpty(path) && !_history.Any(snapshot =>
            snapshot.TryGetValue(ConfigurationKeys.BackgroundImagePath, out var oldPath)
            && Equivalent(rawPath!, oldPath));

        if (!customImage)
        {
            foreach (var key in Keys)
            {
                // Empty is an explicit opt-out, even if an old template was empty.
                if (key == ConfigurationKeys.BackgroundImagePath && path == string.Empty) continue;
                if (!user.TryGetValue(key, out var value) || !TryValue(key, value, out _)) continue;
                if (!_history.Any(snapshot => snapshot.TryGetValue(key, out var oldValue) && Equivalent(value, oldValue))) continue;
                next.Remove(key);
                removed.Add(key);
            }
        }
        next[MigrationKey] = MigrationVersion;
        return new Change(next, true, removed);
    }

    public Change CreateUserChange(IReadOnlyDictionary<string, object> user, string? key = null, object? value = null)
    {
        if (key != null && (!IsBackgroundKey(key) || !TryValue(key, value, out _)))
            throw new ArgumentException("Invalid background setting", nameof(key));
        var migration = CreateMigration(user);
        var next = migration?.Values ?? new Dictionary<string, object>(user, StringComparer.Ordinal);
        if (key == null)
            foreach (var backgroundKey in Keys) next.Remove(backgroundKey);
        else
        {
            TryValue(key, value, out var normalized);
            next[key] = normalized;
        }
        // Explicit edits must not be reclassified when a delayed template arrives.
        if (!next.ContainsKey(MigrationKey)) next[MigrationKey] = MigrationVersion;
        return new Change(next, migration != null, migration?.RemovedKeys ?? []);
    }

    private bool ShouldSkipMigration(IReadOnlyDictionary<string, object> user)
    {
        if (!user.TryGetValue(MigrationKey, out var value)) return false;
        value = Unwrap(value)!;
        if (value is int i && i >= MigrationVersion || value is long l && l >= MigrationVersion) return true;
        _warning?.Invoke("背景迁移标记无效或不受支持，保留配置并跳过自动迁移。");
        return true;
    }

    private static bool Equivalent(object left, object right)
    {
        var a = Unwrap(left);
        var b = Unwrap(right);
        if (TryNumber(a, out var x) && TryNumber(b, out var y)) return x.Equals(y);
        return JToken.DeepEquals(JToken.FromObject(left), JToken.FromObject(right));
    }

    private static object? Unwrap(object? value) => value is JValue j ? j.Value : value;

    public static bool TryValue(string key, object? value, out object normalized)
    {
        value = Unwrap(value);
        normalized = null!;
        if (key == ConfigurationKeys.BackgroundImagePath && value is string path && !path.Contains('\0'))
            normalized = path;
        else if (key == ConfigurationKeys.BackgroundImageExtendToTitleBar && value is bool enabled)
            normalized = enabled;
        else if ((key == ConfigurationKeys.BackgroundImageOpacity || key == ConfigurationKeys.BackgroundImageLightOpacity
                || key == ConfigurationKeys.BackgroundImageBlurRadius) && TryNumber(value, out var number)
            && number >= 0 && number <= (key == ConfigurationKeys.BackgroundImageBlurRadius ? 60 : 1))
            normalized = number;
        else if (key == ConfigurationKeys.BackgroundImagePlacement && value is string placement
            && Enum.GetNames<BackgroundPlacement>().Any(name => name.Equals(placement, StringComparison.OrdinalIgnoreCase)))
            normalized = Enum.Parse<BackgroundPlacement>(placement, true).ToString();
        else if ((key == ConfigurationKeys.BackgroundImageLightMode || key == ConfigurationKeys.BackgroundImageDarkMode)
            && value is string mode && Enum.GetNames<ScenicImageMode>().Any(name => name.Equals(mode, StringComparison.OrdinalIgnoreCase)))
            normalized = Enum.Parse<ScenicImageMode>(mode, true).ToString();
        return normalized != null;
    }

    private static bool TryNumber(object? value, out double number)
    {
        number = 0;
        if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)) return false;
        try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch (Exception) { return false; }
        return double.IsFinite(number);
    }

    private static bool IsAllowedTemplatePath(string key, object value, string dataRoot)
    {
        if (key != ConfigurationKeys.BackgroundImagePath || value is not string path || path.Length == 0) return true;
        if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':')) return false;
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(dataRoot), ResolveImagePath(path, dataRoot));
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
                && !Path.IsPathRooted(relative) && relative != ".";
        }
        catch (Exception) { return false; }
    }

    public static string ResolveImagePath(string path, string dataRoot)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(dataRoot,
            path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    public sealed record Settings(string ImagePath, bool ExtendToTitleBar, double Opacity, double LightOpacity,
        BackgroundPlacement Placement, double BlurRadius, ScenicImageMode LightMode, ScenicImageMode DarkMode,
        bool HasUserValues);

    public sealed record Change(Dictionary<string, object> Values, bool IsMigration, IReadOnlyList<string> RemovedKeys);
}
