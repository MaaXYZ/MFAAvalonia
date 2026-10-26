using MFAAvalonia.Configuration;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class BackgroundConfigurationWriterTests
{
    private const string BackupSuffix = ".before-background-defaults-v1.bak";

    [Fact]
    public void WritesReadableConfigurationWhenTargetDoesNotExist()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");

        Assert.True(BackgroundConfigurationWriter.TryWrite(path, Values(), migration: false, out var error), error);

        var json = JObject.Parse(File.ReadAllText(path));
        Assert.Equal("resource/background.png", json["BackgroundImagePath"]!.Value<string>());
        Assert.False(File.Exists(path + BackupSuffix));
    }

    [Fact]
    public void MigrationKeepsOriginalBackupAndRoundTripsAllJsonValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var original = "{\"keep\":\"original\"}";
        File.WriteAllText(path, original);

        Assert.True(BackgroundConfigurationWriter.TryWrite(path, Values(), migration: true, out var error), error);

        var backup = path + BackupSuffix;
        Assert.Equal(original, File.ReadAllText(backup));
        var firstBackup = File.ReadAllBytes(backup);
        var json = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(JTokenType.Boolean, json["falseValue"]!.Type);
        Assert.False(json["falseValue"]!.Value<bool>());
        Assert.Equal(JTokenType.Integer, json["zeroValue"]!.Type);
        Assert.Equal(0, json["zeroValue"]!.Value<int>());
        Assert.Equal(JTokenType.String, json["emptyValue"]!.Type);
        Assert.Equal(string.Empty, json["emptyValue"]!.Value<string>());
        Assert.Equal(JTokenType.Null, json["nullValue"]!.Type);
        Assert.Equal("retain", json["unknown"]!["nested"]!["field"]!.Value<string>());

        var replacement = new Dictionary<string, object> { ["changed"] = true };
        Assert.True(BackgroundConfigurationWriter.TryWrite(path, replacement, migration: true, out error), error);
        Assert.Equal(firstBackup, File.ReadAllBytes(backup));
        Assert.True(JObject.Parse(File.ReadAllText(path))["changed"]!.Value<bool>());
    }

    [Fact]
    public void BackupPathOccupiedByDirectoryLeavesOriginalAndNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var original = System.Text.Encoding.UTF8.GetBytes("{\"keep\":1}");
        File.WriteAllBytes(path, original);
        Directory.CreateDirectory(path + BackupSuffix);

        Assert.False(BackgroundConfigurationWriter.TryWrite(path, Values(), migration: true, out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void DestinationOpenedWithoutDeleteSharingLeavesOriginalAndNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var original = System.Text.Encoding.UTF8.GetBytes("{\"keep\":2}");
        File.WriteAllBytes(path, original);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(BackgroundConfigurationWriter.TryWrite(path, Values(), migration: false, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
    }

    [Fact]
    public void SerializationFailureLeavesOriginalConfigurationUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        var original = System.Text.Encoding.UTF8.GetBytes("{\"keep\":3}");
        File.WriteAllBytes(path, original);
        var cycle = new Dictionary<string, object>();
        cycle["self"] = cycle;

        Assert.False(BackgroundConfigurationWriter.TryWrite(path, cycle, migration: true, out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + BackupSuffix));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void NonMigrationWriteDoesNotCreateBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, "{\"keep\":4}");

        Assert.True(BackgroundConfigurationWriter.TryWrite(path, Values(), migration: false, out var error), error);

        Assert.False(File.Exists(path + BackupSuffix));
        Assert.True(JObject.Parse(File.ReadAllText(path)).ContainsKey("BackgroundImagePath"));
    }

    private static Dictionary<string, object> Values() => new()
    {
        ["BackgroundImagePath"] = "resource/background.png",
        ["falseValue"] = false,
        ["zeroValue"] = 0,
        ["emptyValue"] = string.Empty,
        ["nullValue"] = null!,
        ["unknown"] = new Dictionary<string, object>
        {
            ["nested"] = new Dictionary<string, object> { ["field"] = "retain" },
        },
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
