using System;
using System.Collections.Generic;
using MFAAvalonia.Extensions;

namespace MFAAvalonia.Helper;

/// <summary>
/// Compatibility bridge for mobile-only view code.
/// The actual translations come from the shared Desktop Strings.resx resources.
/// </summary>
public static class MobileLocalization
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInThemeKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Blue"] = "Blue",
            ["Green"] = "Green",
            ["Red"] = "Red",
            ["Orange"] = "Orange",
            ["Purple"] = "Purple"
        };

    public static string Get(string key, string? language = null)
    {
        // ToLocalization reads LanguageHelper/I18nManager's current culture. The optional
        // language argument is retained for source compatibility; language changes already
        // raise the shared LanguageHelper.LanguageChanged event.
        return key.ToLocalization();
    }

    public static string GetThemeName(string displayName, string? language = null) =>
        BuiltInThemeKeys.TryGetValue(displayName, out var key) ? Get(key, language) : displayName;

    public static string Format(string key, object value) => $"{Get(key)}: {value}";
}
