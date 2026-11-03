using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Lang.Avalonia;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.Localization;
using SukiUI;
using System;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public partial class OptionControlsTests
{
    [Fact]
    public Task SelectionStatusAndMissingTaskReasonUseCurrentLanguage() => Run(() =>
    {
        var theme = new SukiTheme();
        Application.Current!.Styles.Add(theme);
        var previousLanguage = LanguageHelper.CurrentLanguage;
        var plugin = new MFAResxLangPlugin();
        I18nManager.Instance.Register(plugin, defaultCulture: new CultureInfo("zh-CN"), error: out var error);
        Assert.True(plugin.IsLoaded, error?.ToString());
        var resources = new ResourceManager("MFAAvalonia.Assets.Localization.Strings", typeof(LangKeys).Assembly);
        var selection = new MaaInterface.MaaInterfaceSelectOption { SelectedCase = "removed" };
        Assert.False(OptionSelection.Resolve(new() { Cases = [new() { Name = "available" }] }, selection));
        var status = (TextBlock)typeof(TaskOptionGenerator)
            .GetMethod("CreateSelectionStatus", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [selection])!;
        var row = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        row.UnavailableReasonKey = LangKeys.TaskMissingFromResource;
        try
        {
            foreach (var language in new[] { "zh-CN", "zh-Hant", "en-US", "ja-JP", "ko-KR" })
            {
                LanguageHelper.ChangeLanguage(language);
                Dispatcher.UIThread.RunJobs();
                var culture = new CultureInfo(language);
                Assert.True(status.IsVisible);
                Assert.Equal(resources.GetString(LangKeys.OptionSavedSelectionInvalid, culture), status.Text);
                Assert.Equal(resources.GetString(LangKeys.TaskMissingFromResource, culture), row.UnavailableReason);
                foreach (var key in TaskConfigurationLocalizationTests.Keys)
                    Assert.Equal(resources.GetString(key, culture), key.ToLocalization());
                Assert.Contains("Sample task", LangKeys.TaskSkippedInvalidOptions.ToLocalizationFormatted(false, "Sample task"));
                Assert.Contains("Sample task", LangKeys.TaskExecutionBlockedByOptions.ToLocalizationFormatted(false, "Sample task"));
            }
        }
        finally
        {
            LanguageHelper.ChangeLanguage(previousLanguage);
            Application.Current.Styles.Remove(theme);
        }
    });
}
