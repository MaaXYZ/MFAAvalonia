using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public sealed class TaskConfigurationStorageTests
{
    [Fact]
    public void MissingStateDoesNotInitializeOrWriteDuringRead()
    {
        using var fixture = new Fixture();
        var config = fixture.Open();

        Assert.True(config.TryReadTaskState(out var state, out var error), error);
        Assert.False(state.IsInitialized);
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public void ExistingEmptyListIsInitializedAndDefaultsToKeepingEmpty()
    {
        using var fixture = new Fixture("{\"TaskItems\":[]}");
        var config = fixture.Open();

        Assert.True(config.TryReadTaskState(out var state, out var error), error);
        Assert.True(state.IsInitialized);
        Assert.True(state.KeepEmpty);
        Assert.False(state.HasExplicitDeletionState);
        Assert.Empty(state.Tasks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnumerableIsMaterializedForSingleAndBatchWrites(bool batch)
    {
        using var fixture = new Fixture();
        var config = fixture.Open();
        var source = new List<MaaInterface.MaaInterfaceTask> { Task("A"), Task("B") };
        var query = source.Where(task => task.Name == "B").Select(task => task);

        if (batch)
            config.SetValues(new() { [ConfigurationKeys.TaskItems] = query, ["unknown"] = "preserve" });
        else
            config.SetValue(ConfigurationKeys.TaskItems, query);
        source.Clear();

        var restored = config.GetValue<List<MaaInterface.MaaInterfaceTask>?>(ConfigurationKeys.TaskItems, null);
        Assert.NotNull(restored);
        Assert.IsType<List<MaaInterface.MaaInterfaceTask>>(restored);
        Assert.Equal("B", Assert.Single(restored).Name);
        Assert.True(fixture.Open().TryReadTaskState(out var disk, out var error), error);
        Assert.Equal("B", Assert.Single(disk.Tasks).Name);
    }

    [Fact]
    public void CorruptLocalValueNeverFallsBackToLegacyOrOverwritesLocalFile()
    {
        const string original = "{\"TaskItems\":\"invalid\",\"unknown\":7}";
        using var fixture = new Fixture(original);
        fixture.Legacy.Config["Instance.test.TaskItems"] = new List<MaaInterface.MaaInterfaceTask> { Task("Legacy") };
        var config = fixture.Open();

        Assert.Null(config.GetValue<List<MaaInterface.MaaInterfaceTask>?>(ConfigurationKeys.TaskItems, null));
        Assert.False(config.TryGetValue<List<MaaInterface.MaaInterfaceTask>>(ConfigurationKeys.TaskItems, out _));
        Assert.False(config.TryReadTaskState(out _, out var error));
        Assert.NotNull(error);
        Assert.False(config.IsTaskStateValid);
        config.SetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>());
        config.SetValues(new() { [ConfigurationKeys.TaskItems] = new List<MaaInterface.MaaInterfaceTask>() });
        Assert.False(config.TryCommitTaskState(Initialized(Task("new")), out _));
        Assert.Equal(original, File.ReadAllText(fixture.Path));
        Assert.True(fixture.Legacy.Config.ContainsKey("Instance.test.TaskItems"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    public void DamagedFileCannotBeTreatedAsMissingOrSaved(string original)
    {
        using var fixture = new Fixture(original);
        var config = fixture.Open();

        Assert.False(config.TryReadTaskState(out _, out _));
        config.SetValue("unrelated", true);
        config.SetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>());
        Assert.False(config.TryCommitTaskState(Initialized(), out _));
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    [Theory]
    [InlineData("TaskItems", "null")]
    [InlineData("TaskItems", "[null]")]
    [InlineData("TaskItems", "[{\"entry\":\"A\"}]")]
    [InlineData("CurrentTasks", "[17]")]
    [InlineData("DeletedTaskNames", "\"A\"")]
    [InlineData("MissingTaskSessions", "[]")]
    [InlineData("MissingTaskSessions", "{\"A\":12}")]
    [InlineData("KeepEmptyTaskQueue", "\"true\"")]
    [InlineData("InstancePresetKey", "[]")]
    public void InvalidStateFieldsAreRejectedWithoutWriting(string key, string value)
    {
        var json = new JObject { [ConfigurationKeys.TaskItems] = new JArray() };
        json[key] = JToken.Parse(value);
        var original = json.ToString();
        using var fixture = new Fixture(original);

        Assert.False(fixture.Open().TryReadTaskState(out _, out var error));
        Assert.Contains(key, error);
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void LegacyReadIsDetachedAndCommitsCompleteStateWithoutDeletingSource()
    {
        using var fixture = new Fixture();
        var legacyTasks = new List<MaaInterface.MaaInterfaceTask> { Task("A") };
        fixture.Legacy.Config["Instance.test.TaskItems"] = legacyTasks;
        fixture.Legacy.Config["Instance.test.CurrentTasks"] = new List<string> { "A\u0000EntryA" };
        fixture.Legacy.Config["Instance.default.CurrentTasks"] = new List<string> { "WrongScope" };
        var config = fixture.Open();

        Assert.True(config.TryReadTaskState(out var state, out var error), error);
        Assert.True(state.IsInitialized);
        Assert.False(state.HasExplicitDeletionState);
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal("A\u0000EntryA", Assert.Single(state.CurrentTasks));
        state.Tasks[0].Remark = "new remark";
        state.HasExplicitDeletionState = true;
        state.DeletedTaskNames.Add("Deleted");
        state.MissingTaskSessions["Missing"] = "session-one";
        state.HasPresetKey = true;
        state.PresetKey = "preset-one";
        Assert.True(config.TryCommitTaskState(state, out error), error);

        Assert.Null(legacyTasks[0].Remark);
        Assert.True(fixture.Legacy.Config.ContainsKey("Instance.test.TaskItems"));
        Assert.True(fixture.Open().TryReadTaskState(out var saved, out error), error);
        Assert.True(saved.HasExplicitDeletionState);
        Assert.Contains("Deleted", saved.DeletedTaskNames);
        Assert.Equal("session-one", saved.MissingTaskSessions["Missing"]);
        Assert.Equal("preset-one", saved.PresetKey);
    }

    [Fact]
    public void CommitPreservesUnknownValuesBackupAndDetachesPublishedState()
    {
        const string original = "{\"TaskItems\":[],\"unknown\":{\"flag\":false,\"zero\":0,\"empty\":\"\"}}";
        using var fixture = new Fixture(original);
        var config = fixture.Open();
        var state = Initialized(Task("A"));

        Assert.True(config.TryCommitTaskState(state, out var error), error);
        state.Tasks[0].Name = "changed after commit";
        Assert.True(config.TryReadTaskState(out var saved, out error), error);
        Assert.Equal("A", saved.Tasks[0].Name);
        Assert.Equal(original, File.ReadAllText(fixture.Path + TaskConfigurationWriter.BackupSuffix));
        Assert.True(JToken.DeepEquals(JObject.Parse(original)["unknown"],
            JObject.Parse(File.ReadAllText(fixture.Path))["unknown"]));
        Assert.True(config.TryCommitTaskState(Initialized(Task("B")), out error), error);
        Assert.Equal(original, File.ReadAllText(fixture.Path + TaskConfigurationWriter.BackupSuffix));
    }

    [Fact]
    public void FailedCommitKeepsDiskAndMemoryUnchanged()
    {
        const string original = "{\"TaskItems\":[{\"name\":\"A\",\"entry\":\"EntryA\"}]}";
        using var fixture = new Fixture(original);
        Directory.CreateDirectory(fixture.Path + TaskConfigurationWriter.BackupSuffix);
        var config = fixture.Open();

        Assert.False(config.TryCommitTaskState(Initialized(Task("B")), out var error));
        Assert.NotNull(error);
        Assert.Equal(original, File.ReadAllText(fixture.Path));
        Assert.True(config.TryReadTaskState(out var state, out error), error);
        Assert.Equal("A", Assert.Single(state.Tasks).Name);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void DestinationWithoutDeleteSharingRejectsCommitWithoutPublishingMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string original = "{\"TaskItems\":[],\"DeletedTaskNames\":[]}";
        using var fixture = new Fixture(original);
        var config = fixture.Open();
        using (new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(config.TryCommitTaskState(Initialized(Task("B")), out _));
            Assert.Equal(original, File.ReadAllText(fixture.Path));
            Assert.True(config.TryReadTaskState(out var state, out var error), error);
            Assert.Empty(state.Tasks);
        }
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void SerializationFailureDoesNotTouchOriginalFile()
    {
        using var fixture = new Fixture("{\"keep\":1}");
        var cycle = new Dictionary<string, object>();
        cycle["self"] = cycle;

        Assert.False(TaskConfigurationWriter.TryWrite(fixture.Path, cycle, true, out var error));
        Assert.NotNull(error);
        Assert.Equal("{\"keep\":1}", File.ReadAllText(fixture.Path));
        Assert.False(File.Exists(fixture.Path + TaskConfigurationWriter.BackupSuffix));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void CloneDoesNotShareNestedOptionOrLifecycleData()
    {
        var state = Initialized(Task("A"));
        state.Tasks[0].Option = new() { new() { Name = "option", Index = 0, Data = new() { ["n"] = "7" } } };
        state.CurrentTasks.Add("seen");
        state.DeletedTaskNames.Add("deleted");
        state.MissingTaskSessions["missing"] = "first";

        var clone = state.Clone();
        clone.Tasks[0].Option![0].Data!["n"] = "42";
        clone.CurrentTasks.Clear();
        clone.DeletedTaskNames.Clear();
        clone.MissingTaskSessions.Clear();

        Assert.Equal("7", state.Tasks[0].Option![0].Data!["n"]);
        Assert.Single(state.CurrentTasks);
        Assert.Single(state.DeletedTaskNames);
        Assert.Single(state.MissingTaskSessions);
    }

    [Fact]
    public void MissingLifecycleSurvivesSaveRestartAndRestoresAsNewAfterAutomaticRemoval()
    {
        using var fixture = new Fixture();
        var initial = Initialized(Task("A"), Task("Gone"));
        initial.Tasks[1].Remark = "old user parameters";
        initial.CurrentTasks = initial.Tasks.Select(TaskQueueReconciler.HistoryKey).ToList();
        var catalogue = new MaaInterface { Task = new() { Task("A") } };
        var specials = new HashSet<string>();
        var config = fixture.Open();
        var first = TaskQueueReconciler.Reconcile(initial, catalogue, "launch-one", true, specials);
        Assert.Equal("launch-one", first.State.MissingTaskSessions["Gone"]);
        Assert.True(config.TryCommitTaskState(first.State, out var error), error);

        config = fixture.Open();
        Assert.True(config.TryReadTaskState(out var reopened, out error), error);
        var sameLaunch = TaskQueueReconciler.Reconcile(reopened, catalogue, "launch-one", true, specials);
        Assert.Equal(2, sameLaunch.State.Tasks.Count);
        var nextLaunch = TaskQueueReconciler.Reconcile(reopened, catalogue, "launch-two", true, specials);
        Assert.Equal("Gone", Assert.Single(nextLaunch.RemovedNames));
        Assert.DoesNotContain("Gone", nextLaunch.State.DeletedTaskNames);
        Assert.DoesNotContain(nextLaunch.State.CurrentTasks, history => history.StartsWith("Gone<|||>"));
        Assert.True(config.TryCommitTaskState(nextLaunch.State, out error), error);

        config = fixture.Open();
        Assert.True(config.TryReadTaskState(out reopened, out error), error);
        catalogue.Task.Add(Task("Gone"));
        var restored = TaskQueueReconciler.Reconcile(reopened, catalogue, "launch-three", true, specials);
        Assert.Equal(new[] { "A", "Gone" }, restored.State.Tasks.Select(task => task.Name));
        Assert.Null(restored.State.Tasks[1].Remark);
        Assert.Equal("old user parameters", initial.Tasks[1].Remark);
        Assert.Empty(initial.MissingTaskSessions);
    }

    [Fact]
    public void AutomaticallyEmptyQueueCanAcceptReturnedTasksAfterDiskRoundTrip()
    {
        using var fixture = new Fixture();
        var catalogue = new MaaInterface { Task = new() };
        var specials = new HashSet<string>();
        var initial = Initialized(Task("Gone"));
        var marked = TaskQueueReconciler.Reconcile(initial, catalogue, "first", true, specials);
        var removed = TaskQueueReconciler.Reconcile(marked.State, catalogue, "second", true, specials);
        Assert.Empty(removed.State.Tasks);
        Assert.False(removed.State.KeepEmpty);
        Assert.True(fixture.Open().TryCommitTaskState(removed.State, out var error), error);
        Assert.True(fixture.Open().TryReadTaskState(out var reopened, out error), error);
        Assert.False(reopened.KeepEmpty);

        catalogue.Task.Add(Task("Gone"));
        Assert.Single(TaskQueueReconciler.Reconcile(reopened, catalogue, "third", true, specials).State.Tasks);
        reopened.KeepEmpty = true;
        Assert.Empty(TaskQueueReconciler.Reconcile(reopened, catalogue, "third", true, specials).State.Tasks);
    }

    [Fact]
    public void UnknownHistoryIsPreservedWithoutCreatingDeletionIntent()
    {
        using var fixture = new Fixture("{\"TaskItems\":[],\"CurrentTasks\":[\"A\"],\"KeepEmptyTaskQueue\":false}");
        Assert.True(fixture.Open().TryReadTaskState(out var state, out var error), error);
        var catalogue = new MaaInterface { Task = new() { Task("A") } };

        var result = TaskQueueReconciler.Reconcile(state, catalogue, "launch", true, new HashSet<string>());

        Assert.Equal("A", Assert.Single(result.State.Tasks).Name);
        Assert.DoesNotContain("A", result.State.DeletedTaskNames);
        Assert.Contains("A", result.State.CurrentTasks);
    }

    [Fact]
    public void BuiltInTaskWithSameNameDoesNotSuppressNewResourceTask()
    {
        const string builtInEntry = "__built_in";
        var builtIn = Task("A");
        builtIn.Entry = builtInEntry;
        var state = Initialized(builtIn);
        state.CurrentTasks.Add(TaskQueueReconciler.HistoryKey(builtIn));
        var catalogue = new MaaInterface { Task = new() { Task("A") } };

        var result = TaskQueueReconciler.Reconcile(state, catalogue, "launch", true,
            new HashSet<string> { builtInEntry });

        Assert.Equal(new[] { builtInEntry, "EntryA" }, result.State.Tasks.Select(task => task.Entry));
        Assert.Empty(result.State.DeletedTaskNames);
        Assert.Equal(builtInEntry, Assert.Single(state.Tasks).Entry);
    }

    [Theory]
    [InlineData("GlobalOptionItems", "null")]
    [InlineData("GlobalOptionItems", "{}")]
    [InlineData("GlobalOptionItems", "[null]")]
    [InlineData("GlobalOptionItems", "[{\"name\":\"choice\",\"sub_options\":{}}]")]
    [InlineData("ResourceOptionItems", "[]")]
    [InlineData("ResourceOptionItems", "{\"hidden\":null}")]
    [InlineData("ControllerOptionItems", "{\"adb\":[{}]}")]
    public void InvalidScopedOptionsDoNotGetReplacedByEmptyValues(string key, string value)
    {
        var original = new JObject { [ConfigurationKeys.TaskItems] = new JArray(), [key] = JToken.Parse(value) }.ToString();
        using var fixture = new Fixture(original);
        var config = fixture.Open();

        Assert.False(config.TryReadTaskState(out _, out var error));
        Assert.Contains(key, error);
        Assert.False(config.TryCommitTaskState(Initialized(), out _));
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void ScopedOptionsWithoutTaskStateAreReadWithoutInitializingOrWriting()
    {
        const string original = "{\"GlobalOptionItems\":[{\"name\":\"choice\",\"index\":1}]}";
        using var fixture = new Fixture(original);

        Assert.True(fixture.Open().TryReadTaskState(out var state, out var error), error);
        Assert.False(state.IsInitialized);
        var options = Assert.IsType<List<MaaInterface.MaaInterfaceSelectOption>>(state.ExtraValues[ConfigurationKeys.GlobalOptionItems]);
        Assert.Equal(1, Assert.Single(options).Index);
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void ScopedOptionCommitRetainsHiddenBucketsAndDetachesControlBoundObjects()
    {
        const string original = "{\"TaskItems\":[],\"ControllerOptionItems\":{\"adb\":[{\"name\":\"choice\",\"index\":0}],\"win32\":[{\"name\":\"hidden\",\"index\":1}]}}";
        using var fixture = new Fixture(original);
        var config = fixture.Open();
        Assert.True(config.TryReadTaskState(out var state, out var error), error);
        var buckets = Assert.IsType<Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>>(
            state.ExtraValues[ConfigurationKeys.ControllerOptionItems]);
        var boundOptions = new List<MaaInterface.MaaInterfaceSelectOption>
        {
            new() { Name = "choice", Index = 2, Data = new() { ["amount"] = "7" } },
        };
        buckets["adb"] = boundOptions;

        Assert.True(config.TryCommitTaskState(state, out error), error);
        boundOptions[0].Index = 3;
        boundOptions[0].Data!["amount"] = "42";
        Assert.True(config.TryReadTaskState(out var saved, out error), error);
        var savedBuckets = Assert.IsType<Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>>(
            saved.ExtraValues[ConfigurationKeys.ControllerOptionItems]);
        Assert.Equal(1, savedBuckets["win32"][0].Index);
        Assert.Equal(2, savedBuckets["adb"][0].Index);
        Assert.Equal("7", savedBuckets["adb"][0].Data!["amount"]);
        Assert.True(fixture.Open().TryReadTaskState(out var reopened, out error), error);
        Assert.Equal(2, ((Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>)
            reopened.ExtraValues[ConfigurationKeys.ControllerOptionItems]!)["adb"][0].Index);
    }

    [Fact]
    public void ScopedOptionFailureDoesNotPublishNewQueueOrOptions()
    {
        const string original = "{\"TaskItems\":[{\"name\":\"A\",\"entry\":\"A\"}],\"GlobalOptionItems\":[{\"name\":\"choice\",\"index\":0}]}";
        using var fixture = new Fixture(original);
        Directory.CreateDirectory(fixture.Path + TaskConfigurationWriter.BackupSuffix);
        var config = fixture.Open();
        Assert.True(config.TryReadTaskState(out var candidate, out var error), error);
        candidate.Tasks[0].Name = "B";
        ((List<MaaInterface.MaaInterfaceSelectOption>)candidate.ExtraValues[ConfigurationKeys.GlobalOptionItems]!)[0].Index = 1;

        Assert.False(config.TryCommitTaskState(candidate, out _));

        Assert.Equal(original, File.ReadAllText(fixture.Path));
        Assert.True(config.TryReadTaskState(out var saved, out error), error);
        Assert.Equal("A", Assert.Single(saved.Tasks).Name);
        Assert.Equal(0, ((List<MaaInterface.MaaInterfaceSelectOption>)saved.ExtraValues[ConfigurationKeys.GlobalOptionItems]!)[0].Index);
    }

    [Fact]
    public void ExtraValuesCannotWriteArbitraryConfigurationKeys()
    {
        const string original = "{\"TaskItems\":[],\"unknown\":\"original\"}";
        using var fixture = new Fixture(original);
        var candidate = Initialized();
        candidate.ExtraValues["unknown"] = "overwrite";

        Assert.False(fixture.Open().TryCommitTaskState(candidate, out var error));
        Assert.NotNull(error);
        Assert.Equal(original, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void ConfigurationFeedbackDistinguishesReadSaveAndDeletedInstanceFailures()
    {
        using var unreadable = new Fixture("not-json");
        var readConfig = unreadable.Open();
        Assert.False(readConfig.TryReadTaskState(out _, out _));
        Assert.Equal(MFAAvalonia.Helper.LangKeys.TaskConfigurationReadFailed,
            MFAAvalonia.Helper.TaskConfigurationFeedback.GetSaveFailureKey(readConfig));

        using var unwritable = new Fixture("{\"TaskItems\":[]}");
        Directory.CreateDirectory(unwritable.Path + TaskConfigurationWriter.BackupSuffix);
        var saveConfig = unwritable.Open();
        Assert.False(saveConfig.TryCommitTaskState(Initialized(), out _));
        Assert.Equal(MFAAvalonia.Helper.LangKeys.TaskConfigurationSaveFailed,
            MFAAvalonia.Helper.TaskConfigurationFeedback.GetSaveFailureKey(saveConfig));

        saveConfig.DeleteConfigFile();
        Assert.False(saveConfig.TryCommitTaskState(Initialized(), out _));
        Assert.Equal(MFAAvalonia.Helper.LangKeys.TaskConfigurationInstanceDeleted,
            MFAAvalonia.Helper.TaskConfigurationFeedback.GetSaveFailureKey(saveConfig));
    }

    private static MaaInterface.MaaInterfaceTask Task(string name) => new() { Name = name, Entry = "Entry" + name };
    private static TaskQueueState Initialized(params MaaInterface.MaaInterfaceTask[] tasks) => new()
    {
        IsInitialized = true, Tasks = tasks.ToList(), HasExplicitDeletionState = true,
    };

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "MFAATaskStorageTests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "instance.json");
        public MFAConfiguration Legacy { get; } = new("test", "unused-test-config", new());

        public Fixture(string? content = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (content != null) File.WriteAllText(Path, content);
        }

        public InstanceConfiguration Open() => (InstanceConfiguration)Activator.CreateInstance(
            typeof(InstanceConfiguration), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "test", Path, Legacy }, null)!;

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(DirectoryPath);
            var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory escaped the temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
