using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class TaskConfigurationLocalizationTests
{
    internal static readonly string[] Keys =
    [
        LangKeys.TaskConfigurationTitle,
        LangKeys.TaskCleanupPaused,
        LangKeys.TaskMissingFromResource,
        LangKeys.TaskConfigurationReadFailed,
        LangKeys.TaskConfigurationDefinitionsNotLoaded,
        LangKeys.TaskConfigurationDefinitionsInvalid,
        LangKeys.TaskConfigurationSaveFailed,
        LangKeys.TaskConfigurationInstanceDeleted,
        LangKeys.TaskConfigurationSyncFailed,
        LangKeys.OptionCaseDefinitionInvalid,
        LangKeys.OptionSavedSelectionInvalid,
        LangKeys.OptionDefaultCaseUnavailable,
        LangKeys.TaskExecutionBlockedByOptions,
        LangKeys.TaskSkippedInvalidOptions,
        LangKeys.ResetTasksToPreset,
    ];

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-Hant")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    public void EveryNewKeyHasTextAndMatchingFormatPlaceholders(string language)
    {
        var baseline = ReadResources("zh-CN");
        var translated = ReadResources(language);
        foreach (var key in Keys)
        {
            Assert.True(translated.TryGetValue(key, out var value), $"{language}: missing {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language}: empty {key}");
            Assert.Equal(Placeholders(baseline[key]), Placeholders(value!));
            // Parsing also rejects malformed braces, alignment and format items.
            var format = CompositeFormat.Parse(value!);
            var arguments = Enumerable.Repeat<object>("Sample task", format.MinimumArgumentCount).ToArray();
            Assert.False(string.IsNullOrWhiteSpace(string.Format(CultureInfo.InvariantCulture, format, arguments)));
        }
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-Hant")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    public void CompiledResourcesContainTheExactTranslationWithoutFallback(string language)
    {
        var manager = new ResourceManager("MFAAvalonia.Assets.Localization.Strings", typeof(LangKeys).Assembly);
        var culture = language == "zh-CN" ? CultureInfo.InvariantCulture : new CultureInfo(language);
        using var resources = manager.GetResourceSet(culture, true, false);
        Assert.NotNull(resources);
        foreach (var (key, value) in ReadResources(language).Where(pair => Keys.Contains(pair.Key)))
            Assert.Equal(value, resources.GetString(key));
    }

    private static Dictionary<string, string> ReadResources(string language)
    {
        var name = language == "zh-CN" ? "Strings.resx" : $"Strings.{language}.resx";
        return XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Localization", name))
            .Root!.Elements("data").ToDictionary(node => node.Attribute("name")!.Value,
                node => node.Element("value")!.Value);
    }

    private static string[] Placeholders(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
        .Select(match => match.Groups[1].Value).OrderBy(index => index, StringComparer.Ordinal).ToArray();
}
