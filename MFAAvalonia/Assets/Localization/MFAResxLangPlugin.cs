using Lang.Avalonia;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;

#nullable enable

namespace MFAAvalonia.Localization;

/// <summary>
/// 适配Assets.Localization路径下Resx资源的本地化插件
/// </summary>
public class MFAResxLangPlugin : ILangPlugin
{
    public Dictionary<string, LocalizationLanguage> Resources { get; } = new();
    public string Mark { get; set; } = "MFAAvalonia.Assets.Localization";
    private Dictionary<Type, ResourceManager>? _resourceManagers;
    private CultureInfo? _defaultCulture;
    private readonly object _resourceLock = new();

    public CultureInfo Culture
    {
        get => field ?? CultureInfo.InvariantCulture;
        set
        {
            field = value;
            Sync(value);
        }
    }

    public bool IsLoaded { get; set; } = false;
    public void Load(CultureInfo cultureInfo)
    {
        _defaultCulture = cultureInfo;
        Culture = cultureInfo;
        _resourceManagers = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
                assembly.GetTypes()
                    .Where(type => type.FullName!.Contains(Mark, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        type => type,
                        type => type.GetProperty(nameof(ResourceManager),
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                            ?.GetValue(null, null) as ResourceManager)
            )
            .Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);

        Sync(Culture);
        IsLoaded = true;
    }

    public void AddResource(params Assembly[] assemblies)
    {
        var dicts = assemblies.SelectMany(assembly =>
                assembly.GetTypes()
                    .Where(type => type.FullName!.Contains(Mark, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(
                        type => type,
                        type => type.GetProperty(nameof(ResourceManager),
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                            ?.GetValue(null, null) as ResourceManager)
            )
            .Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
        if (dicts.Count != 0)
        {
            foreach (KeyValuePair<Type, ResourceManager> pair in dicts)
            {
                if (!_resourceManagers!.ContainsKey(pair.Key))
                {
                    _resourceManagers.Add(pair.Key, pair.Value);
                }
            }
        }

        Sync(Culture);
    }

    public List<LocalizationLanguage>? GetLanguages() =>
        throw new NotSupportedException("This plugin does not support the current interface for the time being.");

    public string? GetResource(string key, string? cultureName = null)
    {
        lock (_resourceLock)
        {
            var culture = Culture.Name;

            string? Get(string currentCulture)
            {
                if (Resources.TryGetValue(currentCulture, out var currentLanguages)
                    && currentLanguages.Languages.TryGetValue(key, out var resource))
                {
                    return resource;
                }

                return default;
            }

            if (!string.IsNullOrWhiteSpace(cultureName))
            {
                culture = cultureName;
            }

            foreach (var candidate in GetFallbackCultures(culture))
            {
                if (!Resources.TryGetValue(candidate, out var cachedLanguage)
                    || cachedLanguage.Languages.Count == 0)
                {
                    try
                    {
                        Sync(new CultureInfo(candidate));
                    }
                    catch
                    {
                        continue;
                    }
                }

                var resource = Get(candidate);
                if (!string.IsNullOrWhiteSpace(resource))
                {
                    return resource;
                }
            }

            return key;
        }
    }

    private IEnumerable<string> GetFallbackCultures(string culture)
    {
        var candidates = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidates.Contains(candidate))
                return;
            candidates.Add(candidate);
        }

        var normalized = culture.Replace('_', '-');
        Add(normalized);

        try
        {
            var cultureInfo = new CultureInfo(normalized);
            if (!cultureInfo.IsNeutralCulture)
            {
                Add(cultureInfo.TwoLetterISOLanguageName);
            }
        }
        catch
        {
            // Ignore invalid culture name and continue with explicit fallbacks.
        }

        if (normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase))
        {
            Add("zh-Hant");
            Add("zh-CN");
            Add("en-US");
        }
        else if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            Add("zh-CN");
            Add("en-US");
        }
        else
        {
            Add("en-US");
            Add("zh-CN");
        }

        Add(_defaultCulture?.Name);
        return candidates;
    }

    private void Sync(CultureInfo cultureInfo)
    {
        lock (_resourceLock)
        {
            if (_resourceManagers == null || _resourceManagers.Count == 0)
            {
                return;
            }

            IEnumerable<DictionaryEntry> GetResources(ResourceManager resourceManager)
            {
                var targetCulture = cultureInfo.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
                    ? CultureInfo.InvariantCulture
                    : cultureInfo;
                var resourceSet = resourceManager.GetResourceSet(targetCulture, true, false);
                if (resourceSet == null)
                {
                    yield break;
                }

                foreach (var entry in resourceSet.OfType<DictionaryEntry>())
                {
                    yield return entry;
                }
            }

            var cultureName = cultureInfo.Name;
            LocalizationLanguage? currentLanResources;
            if (Resources.TryGetValue(cultureName, out var language))
            {
                currentLanResources = language;
            }
            else
            {
                currentLanResources = new LocalizationLanguage()
                {
                    Language = cultureInfo.DisplayName,
                    Description = cultureInfo.DisplayName,
                    CultureName = cultureName
                };
                Resources[cultureName] = currentLanResources;
            }

            currentLanResources.Languages.Clear();

            foreach (var pair in _resourceManagers)
            {
                pair.Key.GetProperty("Culture", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, cultureInfo);
                foreach (var entry in GetResources(pair.Value))
                {
                    if (entry is { Key: string key, Value: string value })
                    {
                        // 使用字符串驻留减少重复字符串的内存占用
                        // 对于资源键和值都使用驻留,因为很多资源字符串是重复的
                        var internedKey = string.Intern(key);
                        var internedValue = string.Intern(value);
                        currentLanResources.Languages[internedKey] = internedValue;
                    }
                }
            }
        }
    }
}
