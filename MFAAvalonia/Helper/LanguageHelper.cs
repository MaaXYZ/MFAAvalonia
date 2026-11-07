using Lang.Avalonia;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Localization;
using MFAAvalonia.ViewModels.Other;
using Newtonsoft.Json;
using SukiUI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Helper;

public static class LanguageHelper
{
    public static event EventHandler<LanguageEventArgs>? LanguageChanged;
    private static readonly string[] SupportedLanguageKeys = ["zh-CN", "zh-Hant", "en-US", "ja-JP", "ko-KR"];
    private static readonly Dictionary<object, string> LegacyLanguageMappings = new()
    {
        ["zh-CN"] = "zh-CN",
        ["zh-Hant"] = "zh-Hant",
        ["en-US"] = "en-US",
        ["ja-JP"] = "ja-JP",
        ["ko-KR"] = "ko-KR",
        [0] = "zh-CN",
        [1] = "zh-Hant",
        [2] = "en-US",
        [3] = "ja-JP",
        [4] = "ko-KR",
        [0L] = "zh-CN",
        [1L] = "zh-Hant",
        [2L] = "en-US",
        [3L] = "ja-JP",
        [4L] = "ko-KR",
    };

    public static readonly List<SupportedLanguage> SupportedLanguages =
    [
        new("zh-CN", "简体中文"),
        new("zh-Hant", "繁體中文"),
        new("en-US", "English"),
        new("ja-JP", "日本語"),
        new("ko-KR", "한국어"),
    ];

    public static Dictionary<string, CultureInfo> Cultures { get; } = new();

    public static SupportedLanguage GetLanguage(string key)
    {
        return SupportedLanguages.FirstOrDefault(lang => lang.Key == key, SupportedLanguages[0]);
    }

    public static void ChangeLanguage(SupportedLanguage language)
    {
        // 设置应用程序的文化
        I18nManager.Instance.Culture = Cultures.TryGetValue(language.Key, out var culture)
            ? culture
            : Cultures[language.Key] = new CultureInfo(language.Key);
        _currentLanguage = language.Key;
        CultureInfo.CurrentCulture = I18nManager.Instance.Culture ?? CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = I18nManager.Instance.Culture ?? CultureInfo.InvariantCulture;
        SukiTheme.GetInstance().Locale = I18nManager.Instance.Culture;
        LanguageChanged?.Invoke(null, new LanguageEventArgs(language));
    }

    public static void ChangeLanguage(string language)
    {
        I18nManager.Instance.Culture = Cultures.TryGetValue(language, out var culture)
            ? culture
            : Cultures[language] = new CultureInfo(language);
        _currentLanguage = language;
        CultureInfo.CurrentCulture = I18nManager.Instance.Culture ?? CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = I18nManager.Instance.Culture ?? CultureInfo.InvariantCulture;
        SukiTheme.GetInstance().Locale = I18nManager.Instance.Culture;
        LanguageChanged?.Invoke(null, new LanguageEventArgs(GetLanguage(language)));
    }

    // 存储语言的字典
    private static readonly Dictionary<string, Dictionary<string, string>> Langs = new();
    private static string _currentLanguage = LoadConfiguredLanguage();
    public static string CurrentLanguage => _currentLanguage;
    public static void Initialize()
    {
        LoggerHelper.Info("开始初始化语言管理器。");
        var plugin = new MFAResxLangPlugin();
        var configuredLanguage = LoadConfiguredLanguage();
        var defaultCulture = Cultures.TryGetValue(configuredLanguage, out var configuredCulture)
            ? configuredCulture
            : Cultures[configuredLanguage] = new CultureInfo(configuredLanguage);
        I18nManager.Instance.Register(
            plugin, // 格式插件
            defaultCulture: defaultCulture, // 默认语言
            error: out var error // 错误信息（可选）
        );
        if (!plugin.IsLoaded)
        {
            plugin.Load(defaultCulture);
        }
        PersistNormalizedCurrentLanguage();
        ChangeLanguage(_currentLanguage);
        LoadLanguages();
    }

    private static string LoadConfiguredLanguage()
    {
        return ConfigurationManager.Current.GetValue(
            ConfigurationKeys.CurrentLanguage,
            SupportedLanguages[0].Key,
            LegacyLanguageMappings);
    }

    private static void PersistNormalizedCurrentLanguage()
    {
        var configuredLanguage = LoadConfiguredLanguage();
        _currentLanguage = SupportedLanguageKeys.Contains(configuredLanguage)
            ? configuredLanguage
            : SupportedLanguages[0].Key;
        ConfigurationManager.Current.SetValue(ConfigurationKeys.CurrentLanguage, _currentLanguage);
    }

    private static void LoadLanguages()
    {
        // 旧版：从 lang 目录加载语言文件（已弃用，保留用于兼容）
        // var langPath = Path.Combine(AppContext.BaseDirectory, "lang");
        // if (Directory.Exists(langPath))
        // {
        //     var langFiles = Directory.GetFiles(langPath, "*.json");
        //     foreach (string langFile in langFiles)
        //     {
        //         var langCode = Path.GetFileNameWithoutExtension(langFile).ToLower();
        //         if (IsSimplifiedChinese(langCode))
        //         {
        //             langCode = "zh-hans";
        //         }
        //         else if (IsTraditionalChinese(langCode))
        //         {
        //             langCode = "zh-hant";
        //         }
        //         var jsonContent = File.ReadAllText(langFile);
        //         var langResources = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonContent);
        //         if (langResources is not null)
        //             Langs[langCode] = langResources;
        //     }
        // }
    }

    /// <summary>
    /// 从 MaaInterface.Languages 加载多语言配置
    /// </summary>
    /// <param name="languages">语言配置字典（键为语言代码，值为翻译文件相对路径）</param>
    /// <param name="basePath">interface.json 所在目录（用于解析相对路径）</param>
    public static void LoadLanguagesFromInterface(Dictionary<string, string>? languages, string? basePath)
    {
        if (languages == null || languages.Count == 0 || Langs.Count > 0)
            return;

        var safeBasePath = basePath ?? AppPaths.DataRoot;

        foreach (var (langCode, relativePath) in languages)
        {
            try
            {
                // 使用 ReplacePlaceholder 处理路径（支持 {PROJECT_DIR} 占位符）
                var processedPath = MaaInterface.ReplacePlaceholder(relativePath, safeBasePath);

                if (string.IsNullOrEmpty(processedPath) || !File.Exists(processedPath))
                {
                    LoggerHelper.Warning($"语言文件不存在：文件={processedPath}");
                    continue;
                }

                var jsonContent = File.ReadAllText(processedPath);
                var langResources = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonContent);

                if (langResources != null)
                {
                    // 标准化语言代码。未支持的语言不参与映射，避免覆盖现有语言资源。
                    var normalizedLangCode = NormalizeLangCode(langCode);
                    if (normalizedLangCode == null)
                    {
                        LoggerHelper.Warning($"跳过未支持的语言文件：语言={langCode}，文件={processedPath}");
                        continue;
                    }

                    // 合并到现有语言资源（如果已存在则覆盖）
                    if (Langs.TryGetValue(normalizedLangCode, out var existingDict))
                    {
                        foreach (var (key, value) in langResources)
                        {
                            existingDict[key] = value;
                        }
                    }
                    else
                    {
                        Langs[normalizedLangCode] = langResources;
                    }

                    LoggerHelper.Info($"已加载语言文件：语言={langCode}，文件={processedPath}");
                }
            }
            catch (Exception ex)
            {
                LoggerHelper.Error($"加载语言文件失败：语言={langCode}，原因={ex.Message}", ex);
            }
        }
        
    }

    /// <summary>
    /// 标准化语言代码（将各种格式统一为内部使用的格式）
    /// </summary>
    private static string? NormalizeLangCode(string langCode)
    {
        var normalized = langCode.ToLower().Replace("_", "-");

        if (IsSimplifiedChinese(normalized))
            return "zh-CN";

        if (IsTraditionalChinese(normalized))
            return "zh-Hant";

        if (IsEnglish(normalized))
            return "en-US";

        if (IsJapanese(normalized))
            return "ja-JP";

        if (IsKorean(normalized))
            return "ko-KR";

        return null;
    }

    private static Dictionary<string, string> GetLocalizedStrings()
    {
        return Langs.TryGetValue(_currentLanguage,
            out var dict)
            ? dict
            : new Dictionary<string, string>();
    }

    public static string GetLocalizedString(string? key)
    {
        if (key == null)
            return string.Empty;
        if (key.StartsWith("$"))
        {
            var key1 = key.Substring(1);
            return GetLocalizedStrings().ContainsKey(key1) ? GetLocalizedStrings()[key1] : GetLocalizedStrings().ContainsKey(key) ? GetLocalizedStrings()[key] : key;
        }
        return GetLocalizedStrings().GetValueOrDefault(key, key);
    }

    public static string GetLocalizedDisplayName(string? displayName, string? fallbackName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return LanguageHelper.GetLocalizedString(fallbackName) ?? fallbackName ?? string.Empty;
        }

        if (displayName.StartsWith("$"))
        {
            var localized = LanguageHelper.GetLocalizedString(displayName);

            if (string.IsNullOrEmpty(localized) || localized == displayName)
            {
                if (fallbackName != displayName && !string.IsNullOrEmpty(fallbackName))
                {
                    return LanguageHelper.GetLocalizedString(fallbackName) ?? fallbackName;
                }
                return displayName;
            }
            return localized;
        }
        return displayName;
    }
    
    /// <summary>
    /// 创建一个资源绑定，当语言切换时自动更新
    /// </summary>
    /// <param name="key">资源键（如 "$你好"）</param>
    /// <returns>可用于控件绑定的 ResourceBinding</returns>
    /// <example>
    /// <code>
    /// textBlock.Bind(TextBlock.TextProperty, LanguageHelper.CreateBinding("$你好"));
    /// </code>
    /// </example>
    public static Extensions.ResourceBinding CreateBinding(string key)
    {
        return new Extensions.ResourceBinding(key);
    }

    private static bool IsSimplifiedChinese(string langCode)
    {
        string[] simplifiedPrefixes =
        [
            "zh-hans",
            "zh-cn",
            "zh-sg"
        ];
        foreach (string prefix in simplifiedPrefixes)
        {
            if (langCode.Replace("_", "-").StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTraditionalChinese(string langCode)
    {
        string[] traditionalPrefixes =
        [
            "zh-hant",
            "zh-tw",
            "zh-hk",
            "zh-mo"
        ];
        foreach (string prefix in traditionalPrefixes)
        {
            if (langCode.Replace("_", "-").StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEnglish(string langCode)
    {
        string[] englishPrefixes =
        [
            "en",
            "en-us",
            "en-gb"
        ];
        foreach (string prefix in englishPrefixes)
        {
            if (langCode.Replace("_", "-").StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsJapanese(string langCode)
    {
        string[] japanesePrefixes =
        [
            "ja",
            "ja-jp"
        ];
        foreach (string prefix in japanesePrefixes)
        {
            if (langCode.Replace("_", "-").StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsKorean(string langCode)
    {
        string[] koreanPrefixes =
        [
            "ko",
            "ko-kr"
        ];
        foreach (string prefix in koreanPrefixes)
        {
            if (langCode.Replace("_", "-").StartsWith(prefix))
            {
                return true;
            }
        }
        return false;
    }

    public class LanguageEventArgs(SupportedLanguage language) : EventArgs
    {
        public SupportedLanguage Value { get; set; } = language;
    }
}
