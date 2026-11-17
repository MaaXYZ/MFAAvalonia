using MFAAvalonia.Configuration;
using MFAAvalonia.Controls;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public class BackgroundDefaultsTests
{
    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "MFA-background-data");

    [Fact]
    public void MissingAndEmptyTemplateResolveToBuiltIns()
    {
        var missing = BackgroundDefaults.Empty.Resolve(new Dictionary<string, object>());
        var empty = BackgroundDefaults.FromTemplate(JObject.Parse("{}"), DataRoot)
            .Resolve(new Dictionary<string, object>());

        AssertBuiltIns(missing);
        AssertBuiltIns(empty);
    }

    [Fact]
    public void NoUserValuesResolveTemplateDefaults()
    {
        var defaults = Defaults();

        var resolved = defaults.Resolve(new Dictionary<string, object>());

        Assert.Equal(Path.Combine(DataRoot, "Templates", "bg.png"), BackgroundDefaults.ResolveImagePath(resolved.ImagePath, DataRoot));
        Assert.Equal(0.65, resolved.Opacity);
        Assert.Equal(0.45, resolved.LightOpacity);
        Assert.True(resolved.ExtendToTitleBar);
        Assert.Equal(BackgroundPlacement.Left, resolved.Placement);
        Assert.Equal(14.0, resolved.BlurRadius);
        Assert.Equal(ScenicImageMode.CenteredBlend, resolved.LightMode);
        Assert.Equal(ScenicImageMode.Blurred, resolved.DarkMode);
        Assert.False(resolved.HasUserValues);
    }

    [Fact]
    public void UserImageUsesBuiltInDisplayDefaultsWhenUserHasNoParameters()
    {
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImagePath] = @"User\custom.png"
        };

        var resolved = Defaults().Resolve(user);

        Assert.Equal(@"User\custom.png", resolved.ImagePath);
        Assert.Equal(0.2, resolved.Opacity);
        Assert.Equal(0.16, resolved.LightOpacity);
        Assert.False(resolved.ExtendToTitleBar);
        Assert.Equal(BackgroundPlacement.Right, resolved.Placement);
        Assert.Equal(32.0, resolved.BlurRadius);
        Assert.Equal(ScenicImageMode.Blurred, resolved.LightMode);
        Assert.Equal(ScenicImageMode.SideBlend, resolved.DarkMode);
        Assert.True(resolved.HasUserValues);
    }

    [Fact]
    public void FalseZeroAndEmptyPathAreValidExplicitUserValues()
    {
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImagePath] = string.Empty,
            [ConfigurationKeys.BackgroundImageExtendToTitleBar] = false,
            [ConfigurationKeys.BackgroundImageOpacity] = 0,
            [ConfigurationKeys.BackgroundImagePlacement] = "Bottom"
        };

        var resolved = Defaults().Resolve(user);

        Assert.Equal(string.Empty, resolved.ImagePath);
        Assert.False(resolved.ExtendToTitleBar);
        Assert.Equal(0.0, resolved.Opacity);
        Assert.Equal(BackgroundPlacement.Bottom, resolved.Placement);
        Assert.True(resolved.HasUserValues);
    }

    [Fact]
    public void InvalidUserValuesFallBackWithoutChangingInput()
    {
        var invalidNumber = "0.7";
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImagePath] = @"User\custom.png",
            [ConfigurationKeys.BackgroundImageOpacity] = invalidNumber,
            [ConfigurationKeys.BackgroundImageLightOpacity] = double.NaN,
            [ConfigurationKeys.BackgroundImageBlurRadius] = 61,
            [ConfigurationKeys.BackgroundImageExtendToTitleBar] = 1,
            [ConfigurationKeys.BackgroundImagePlacement] = "Diagonal",
            [ConfigurationKeys.BackgroundImageDarkMode] = "UnknownMode"
        };
        var before = new Dictionary<string, object>(user);

        var resolved = Defaults().Resolve(user);

        Assert.Equal(0.2, resolved.Opacity);
        Assert.Equal(0.16, resolved.LightOpacity);
        Assert.Equal(32.0, resolved.BlurRadius);
        Assert.False(resolved.ExtendToTitleBar);
        Assert.Equal(BackgroundPlacement.Right, resolved.Placement);
        Assert.Equal(ScenicImageMode.SideBlend, resolved.DarkMode);
        Assert.Equal(before, user);

        var booleanNumber = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImagePath] = @"User\custom.png",
            [ConfigurationKeys.BackgroundImageOpacity] = false
        };
        Assert.Equal(0.2, Defaults().Resolve(booleanNumber).Opacity);
        Assert.Equal(false, booleanNumber[ConfigurationKeys.BackgroundImageOpacity]);
    }

    [Fact]
    public void TemplateAcceptsIntegerNumericValuesAndRejectsEscapingPaths()
    {
        var template = JObject.Parse("""
            {"BackgroundImagePath":"../escape.png","BackgroundImageOpacity":1,
             "BackgroundImageLightOpacity":0,"BackgroundImageBlurRadius":10}
            """);

        var defaults = BackgroundDefaults.FromTemplate(template, DataRoot);
        var resolved = defaults.Resolve(new Dictionary<string, object>());

        Assert.DoesNotContain(ConfigurationKeys.BackgroundImagePath, defaults.TemplateKeys);
        Assert.Equal(string.Empty, resolved.ImagePath);
        Assert.Equal(1.0, resolved.Opacity);
        Assert.Equal(0.0, resolved.LightOpacity);
        Assert.Equal(10.0, resolved.BlurRadius);

        var absolute = BackgroundDefaults.FromTemplate(
            JObject.Parse("""{"BackgroundImagePath":"C:\\outside.png"}"""), DataRoot);
        Assert.DoesNotContain(ConfigurationKeys.BackgroundImagePath, absolute.TemplateKeys);
    }

    [Fact]
    public void MatchingCompleteLegacySnapshotMigratesAllEightKeysOnce()
    {
        var snapshot = JObject.Parse("""
            {"BackgroundImagePath":"Themes/bg.png","BackgroundImageExtendToTitleBar":true,
             "BackgroundImageOpacity":0.24,"BackgroundImageLightOpacity":0.44,
             "BackgroundImagePlacement":"Left","BackgroundImageBlurRadius":24.0,
             "BackgroundImageLightMode":"CenteredBlend","BackgroundImageDarkMode":"Blurred"}
            """);
        var defaults = BackgroundDefaults.FromTemplate(TemplateWithHistory(snapshot), DataRoot);
        var user = CompleteUserSettings(0.24, 0.44);
        user[ConfigurationKeys.BackgroundImageBlurRadius] = 24;

        var migration = defaults.CreateMigration(user);

        Assert.NotNull(migration);
        Assert.True(migration!.IsMigration);
        Assert.Equal(8, migration.RemovedKeys.Count);
        Assert.All(BackgroundDefaults.Keys, key => Assert.DoesNotContain(key, migration.Values.Keys));
        Assert.Equal(BackgroundDefaults.MigrationVersion, migration.Values[BackgroundDefaults.MigrationKey]);
        Assert.Null(defaults.CreateMigration(migration.Values));
    }

    [Fact]
    public void UserImageMatchingLegacySnapshotRetainsEveryBackgroundValue()
    {
        var snapshot = JObject.Parse("""
            {"BackgroundImagePath":"OldTemplate\\bg.png","BackgroundImageOpacity":0.24,
             "BackgroundImageLightOpacity":0.44,"BackgroundImagePlacement":"Left",
             "BackgroundImageBlurRadius":24.0,"BackgroundImageExtendToTitleBar":true,
             "BackgroundImageLightMode":"CenteredBlend","BackgroundImageDarkMode":"Blurred"}
            """);
        var defaults = BackgroundDefaults.FromTemplate(TemplateWithHistory(snapshot), DataRoot);
        var user = CompleteUserSettings(0.24, 0.44);
        user[ConfigurationKeys.BackgroundImageBlurRadius] = 24;

        var migration = defaults.CreateMigration(user);

        Assert.NotNull(migration);
        Assert.Empty(migration!.RemovedKeys);
        Assert.All(BackgroundDefaults.Keys, key => Assert.Contains(key, migration.Values.Keys));
        Assert.Equal(BackgroundDefaults.MigrationVersion, migration.Values[BackgroundDefaults.MigrationKey]);
    }

    [Fact]
    public void EmptyUserImagePathIsRetainedEvenWhenLegacyTemplateWasEmpty()
    {
        var snapshot = JObject.Parse("""
            {"BackgroundImagePath":"","BackgroundImageOpacity":0.2}
            """);
        var defaults = BackgroundDefaults.FromTemplate(TemplateWithHistory(snapshot), DataRoot);
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImagePath] = string.Empty,
            [ConfigurationKeys.BackgroundImageOpacity] = 0.2
        };

        var migration = defaults.CreateMigration(user);

        Assert.NotNull(migration);
        Assert.Contains(ConfigurationKeys.BackgroundImagePath, migration!.Values.Keys);
        Assert.DoesNotContain(ConfigurationKeys.BackgroundImagePath, migration.RemovedKeys);
        Assert.DoesNotContain(ConfigurationKeys.BackgroundImageOpacity, migration.Values.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExistingMigrationMarkerSkipsMigration(int marker)
    {
        var user = CompleteUserSettings(0.24, 0.44);
        user[BackgroundDefaults.MigrationKey] = marker;

        Assert.Null(DefaultsWithValidHistory().CreateMigration(user));
    }

    [Fact]
    public void MissingOrEmptyHistoryDoesNotMigrate()
    {
        var user = CompleteUserSettings(0.24, 0.44);

        Assert.Null(Defaults().CreateMigration(user));
        Assert.Null(BackgroundDefaults.FromTemplate(
            JObject.Parse("""{"LegacyTemplates":[]}"""), DataRoot).CreateMigration(user));
    }

    [Fact]
    public void BooleanDoesNotCompareEqualToNumericZeroDuringMigration()
    {
        var snapshot = JObject.Parse("""{"BackgroundImageOpacity":0}""");
        var defaults = BackgroundDefaults.FromTemplate(TemplateWithHistory(snapshot), DataRoot);
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImageOpacity] = false
        };

        var migration = defaults.CreateMigration(user);

        Assert.NotNull(migration);
        Assert.Contains(ConfigurationKeys.BackgroundImageOpacity, migration!.Values.Keys);
        Assert.DoesNotContain(ConfigurationKeys.BackgroundImageOpacity, migration.RemovedKeys);
        Assert.Equal(false, migration.Values[ConfigurationKeys.BackgroundImageOpacity]);
    }

    [Fact]
    public void UserChangeAfterMigrationPreventsReclassification()
    {
        var defaults = DefaultsWithValidHistory();
        var user = CompleteUserSettings(0.24, 0.44);
        var migrated = defaults.CreateMigration(user)!;
        migrated.Values[ConfigurationKeys.BackgroundImageOpacity] = 0.24;

        Assert.Null(defaults.CreateMigration(migrated.Values));
    }

    [Fact]
    public void UserChangeBeforeDelayedTemplateArrivalSetsMarker()
    {
        var user = new Dictionary<string, object>();
        var changed = BackgroundDefaults.Empty.CreateUserChange(user, ConfigurationKeys.BackgroundImageOpacity, 0.48);
        var laterTemplate = DefaultsWithValidHistory();

        Assert.Equal(1, changed.Values[BackgroundDefaults.MigrationKey]);
        Assert.Null(laterTemplate.CreateMigration(changed.Values));
    }

    [Fact]
    public void ResetRemovesOnlyBackgroundKeysAndPreservesOtherData()
    {
        var user = new Dictionary<string, object>
        {
            [ConfigurationKeys.BackgroundImageOpacity] = 0.48,
            ["OtherSetting"] = "keep",
            ["Nested"] = JObject.Parse("""{"value":1}""")
        };

        var reset = BackgroundDefaults.Empty.CreateUserChange(user);

        Assert.All(BackgroundDefaults.Keys, key => Assert.DoesNotContain(key, reset.Values.Keys));
        Assert.Equal("keep", reset.Values["OtherSetting"]);
        Assert.True(JToken.DeepEquals(JObject.Parse("""{"value":1}"""), (JObject)reset.Values["Nested"]));
        Assert.Equal(1, reset.Values[BackgroundDefaults.MigrationKey]);
    }

    [Fact]
    public void ResolveAndMigrationNeverMutateInputDictionary()
    {
        var user = CompleteUserSettings(0.24, 0.44);
        user["Nested"] = JObject.Parse("""{"value":1}""");
        var before = new Dictionary<string, object>(user);
        var defaults = DefaultsWithValidHistory();

        defaults.Resolve(user);
        defaults.CreateMigration(user);
        defaults.CreateUserChange(user, ConfigurationKeys.BackgroundImageOpacity, 0.7);

        Assert.Equal(before, user);
        Assert.True(JToken.DeepEquals((JObject)before["Nested"], (JObject)user["Nested"]));
    }

    [Fact]
    public void RelativeImagePathResolvesAgainstDataRootIndependentOfCurrentDirectory()
    {
        var previous = Environment.CurrentDirectory;
        var alternate = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(alternate);
        try
        {
            Environment.CurrentDirectory = alternate;

            var actual = BackgroundDefaults.ResolveImagePath(@"Images\bg.png", DataRoot);

            Assert.Equal(Path.GetFullPath(Path.Combine(DataRoot, "Images", "bg.png")), actual);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(alternate);
        }
    }

    private static BackgroundDefaults Defaults()
        => BackgroundDefaults.FromTemplate(JObject.Parse("""
            {"BackgroundImagePath":"Templates/bg.png","BackgroundImageExtendToTitleBar":true,
             "BackgroundImageOpacity":0.65,"BackgroundImageLightOpacity":0.45,
             "BackgroundImagePlacement":"Left","BackgroundImageBlurRadius":14,
             "BackgroundImageLightMode":"CenteredBlend","BackgroundImageDarkMode":"Blurred"}
            """), DataRoot);

    private static BackgroundDefaults DefaultsWithValidHistory()
        => BackgroundDefaults.FromTemplate(TemplateWithHistory(JObject.Parse("""
            {"BackgroundImagePath":"Themes/bg.png","BackgroundImageOpacity":0.24,
             "BackgroundImageLightOpacity":0.44,"BackgroundImagePlacement":"Left",
             "BackgroundImageBlurRadius":12,"BackgroundImageExtendToTitleBar":true,
             "BackgroundImageLightMode":"CenteredBlend","BackgroundImageDarkMode":"Blurred"}
            """)), DataRoot);

    private static JObject TemplateWithHistory(JObject snapshot)
    {
        var template = (JObject)snapshot.DeepClone();
        template[BackgroundDefaults.HistoryKey] = new JArray(snapshot.DeepClone());
        return template;
    }

    private static Dictionary<string, object> CompleteUserSettings(double opacity, double lightOpacity)
        => new()
        {
            [ConfigurationKeys.BackgroundImagePath] = "Themes/bg.png",
            [ConfigurationKeys.BackgroundImageExtendToTitleBar] = true,
            [ConfigurationKeys.BackgroundImageOpacity] = opacity,
            [ConfigurationKeys.BackgroundImageLightOpacity] = lightOpacity,
            [ConfigurationKeys.BackgroundImagePlacement] = "Left",
            [ConfigurationKeys.BackgroundImageBlurRadius] = 24,
            [ConfigurationKeys.BackgroundImageLightMode] = "CenteredBlend",
            [ConfigurationKeys.BackgroundImageDarkMode] = "Blurred"
        };

    private static void AssertBuiltIns(BackgroundDefaults.Settings settings)
    {
        Assert.Equal(string.Empty, settings.ImagePath);
        Assert.False(settings.ExtendToTitleBar);
        Assert.Equal(0.2, settings.Opacity);
        Assert.Equal(0.16, settings.LightOpacity);
        Assert.Equal(BackgroundPlacement.Right, settings.Placement);
        Assert.Equal(32.0, settings.BlurRadius);
        Assert.Equal(ScenicImageMode.Blurred, settings.LightMode);
        Assert.Equal(ScenicImageMode.SideBlend, settings.DarkMode);
        Assert.False(settings.HasUserValues);
    }
}
