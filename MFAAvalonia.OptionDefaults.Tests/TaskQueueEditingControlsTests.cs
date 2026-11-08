using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public partial class OptionControlsTests
{
    [Fact]
    public Task ResetClearsStoredPresetAndAllowsNewTasksAfterReopeningConfiguration() => Run(() =>
    {
        using var fixture = new TaskEditFixture("""
            {"TaskItems":[{"name":"A","entry":"EntryA"}],
             "CurrentTasks":["A<|||>EntryA"],"InstancePresetKey":"daily"}
            """);

        Assert.True(fixture.ViewModel.TryApplyTaskEdit(fixture.ViewModel.TaskItemViewModels,
            resetHistory: true, setPreset: true));

        var reopened = fixture.OpenConfiguration();
        Assert.True(reopened.TryReadTaskState(out var saved, out var error), error);
        Assert.Null(saved.PresetKey);
        var catalogue = new MaaInterface
        {
            Task = [new() { Name = "A", Entry = "EntryA" }, new() { Name = "B", Entry = "EntryB" }]
        };
        var reconciled = TaskQueueReconciler.Reconcile(saved, catalogue, "next-launch", true, new HashSet<string>());
        Assert.Equal(new[] { "A", "B" }, reconciled.State.Tasks.Select(task => task.Name));
    });

    [Fact]
    public Task TaskEditPublishesOnlyAfterCompleteStateIsWritten() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        var published = false;
        fixture.ViewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName != nameof(TaskQueueViewModel.TaskItemViewModels)) return;
            Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
            Assert.Empty(saved.Tasks);
            Assert.Contains("A", saved.DeletedTaskNames);
            Assert.True(saved.KeepEmpty);
            published = true;
        };

        Assert.True(fixture.ViewModel.TryApplyTaskEdit([], deletedName: "A"));
        Assert.True(published);
        Assert.Empty(fixture.ViewModel.TaskItemViewModels);
    });

    [Fact]
    public Task FailedTaskEditKeepsOriginalCollectionAndConfiguration() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        Directory.CreateDirectory(fixture.Path + TaskConfigurationWriter.BackupSuffix);
        var originalCollection = fixture.ViewModel.TaskItemViewModels;
        var originalFile = File.ReadAllText(fixture.Path);

        Assert.False(fixture.ViewModel.TryApplyTaskEdit([], deletedName: "A"));
        Assert.Single(fixture.Errors);

        Assert.Same(originalCollection, fixture.ViewModel.TaskItemViewModels);
        Assert.Single(fixture.ViewModel.TaskItemViewModels);
        Assert.Equal(originalFile, File.ReadAllText(fixture.Path));
        Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
        Assert.Single(saved.Tasks);
        Assert.Empty(saved.DeletedTaskNames);
    });

    [Fact]
    public Task NestedPublishingScopesSuppressCheckboxWritesAndReleaseExactlyOnce() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        var originalFile = File.ReadAllText(fixture.Path);
        using (fixture.ViewModel.SuppressTaskStateSaving())
        {
            var inner = fixture.ViewModel.SuppressTaskStateSaving();
            fixture.ViewModel.TaskItemViewModels[0].IsChecked = false;
            inner.Dispose();
            inner.Dispose();
            Assert.True(fixture.ViewModel.IsPublishingTaskState);
            Assert.Equal(originalFile, File.ReadAllText(fixture.Path));
        }
        Assert.False(fixture.ViewModel.IsPublishingTaskState);
        Assert.True(fixture.ViewModel.TrySaveTaskItems());
        Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
        Assert.False(Assert.Single(saved.Tasks).Check);
    });

    [Fact]
    public Task ReplacingPublishedCollectionDoesNotImplicitlyEraseStoredQueue() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        var originalFile = File.ReadAllText(fixture.Path);
        fixture.ViewModel.TaskItemViewModels = [];
        Assert.Equal(originalFile, File.ReadAllText(fixture.Path));
        Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
        Assert.Single(saved.Tasks);
    });

    [Fact]
    public Task FailedOptionSaveRestoresSelectionInPublishedTaskModel() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        Directory.CreateDirectory(fixture.Path + TaskConfigurationWriter.BackupSuffix);
        fixture.ViewModel.TaskItemViewModels[0].InterfaceItem!.Option =
            [new() { Name = "mode", SelectedCase = "changed", Index = 3 }];
        Assert.False(fixture.ViewModel.TrySaveTaskItems());
        var restored = Assert.Single(fixture.ViewModel.TaskItemViewModels).InterfaceItem!;
        Assert.Equal("B", Assert.Single(restored.Option!).SelectedCase);
        Assert.Equal(1, restored.Option![0].Index);
    });

    [Fact]
    public Task CurrentResourceSavePreservesOtherResourceAndControllerBuckets() => Run(() =>
    {
        using var fixture = new TaskEditFixture("""
            {"TaskItems":[{"name":"A","entry":"EntryA"}],
             "ResourceOptionItems":{"R1":[{"name":"mode","selected_case":"old"}],"R2":[{"name":"mode","selected_case":"keep-resource"}]},
             "ControllerOptionItems":{"PC":[{"name":"mode","selected_case":"keep-controller"}]}}
            """);
        var resourceItem = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        resourceItem.IsResourceOptionItem = true;
        resourceItem.ResourceItem = new MaaInterface.MaaInterfaceResource
        {
            Name = "R1", SelectOptions = [new() { Name = "mode", SelectedCase = "new" }]
        };
        fixture.ViewModel.TaskItemViewModels.Insert(0, resourceItem);

        Assert.True(fixture.ViewModel.TrySaveTaskItems());
        Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
        var resources = (Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>)saved.ExtraValues[ConfigurationKeys.ResourceOptionItems]!;
        var controllers = (Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>)saved.ExtraValues[ConfigurationKeys.ControllerOptionItems]!;
        Assert.Equal("new", resources["R1"][0].SelectedCase);
        Assert.Equal("keep-resource", resources["R2"][0].SelectedCase);
        Assert.Equal("keep-controller", controllers["PC"][0].SelectedCase);
    });

    [Fact]
    public Task FailedSharedOptionSaveRestoresCurrentBucketWithoutTouchingOtherBuckets() => Run(() =>
    {
        using var fixture = new TaskEditFixture("""
            {"TaskItems":[{"name":"A","entry":"EntryA"}],
             "ResourceOptionItems":{"R1":[{"name":"mode","selected_case":"old"}],"R2":[{"name":"mode","selected_case":"keep"}]}}
            """);
        var resourceItem = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        resourceItem.IsResourceOptionItem = true;
        resourceItem.ResourceItem = new MaaInterface.MaaInterfaceResource
        {
            Name = "R1", SelectOptions = [new() { Name = "mode", SelectedCase = "unsaved" }]
        };
        fixture.ViewModel.TaskItemViewModels.Insert(0, resourceItem);
        Directory.CreateDirectory(fixture.Path + TaskConfigurationWriter.BackupSuffix);

        Assert.False(fixture.ViewModel.TrySaveTaskItems());
        Assert.Equal("old", resourceItem.ResourceItem.SelectOptions![0].SelectedCase);
        Assert.True(fixture.Configuration.TryReadTaskState(out var saved, out _));
        var resources = (Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>)saved.ExtraValues[ConfigurationKeys.ResourceOptionItems]!;
        Assert.Equal("keep", resources["R2"][0].SelectedCase);
    });

    private sealed class TaskEditFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "MFAATaskEditTests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "instance.json");
        public InstanceConfiguration Configuration { get; }
        public TaskQueueViewModel ViewModel { get; }
        public List<string?> Errors { get; } = [];
        public MaaInterface Catalogue { get; }
        public List<DragItemViewModel> Templates { get; } = [];

        public TaskEditFixture(string? content = null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path, content ?? "{\"TaskItems\":[{\"name\":\"A\",\"entry\":\"EntryA\",\"default_check\":true,\"option\":[{\"name\":\"mode\",\"index\":1,\"selected_case\":\"B\"}]}],\"CurrentTasks\":[\"A<|||>EntryA\"]}");
            Configuration = OpenConfiguration();
            ViewModel = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
            ViewModel.IsTaskConfigurationValid = true;
            var catalogue = Catalogue = new MaaInterface
            {
                Option = new()
                {
                    ["mode"] = new()
                    {
                        Type = "select", Cases = new[] { "A", "B", "old", "new", "keep", "keep-resource", "keep-controller" }
                            .Select(name => new MaaInterface.MaaInterfaceOptionCase { Name = name }).ToList()
                    }
                }
            };
            var contextType = typeof(TaskQueueViewModel).GetNestedType("TaskStateEditingContext", BindingFlags.NonPublic)!;
            var context = Activator.CreateInstance(contextType,
                (Func<InstanceConfiguration>)(() => Configuration),
                (Func<MaaInterface?>)(() => catalogue),
                (Func<bool>)(() => false),
                (Action<DragItemViewModel>)(_ => { }),
                (Action<string?>)(message => Errors.Add(message)),
                (Func<MaaInterface.MaaInterfaceTask, DragItemViewModel>)CreateRow,
                (Action<DragItemViewModel, MaaInterface.MaaInterfaceTask>)UpdateRow,
                (Func<IEnumerable<DragItemViewModel>>)(() => Templates));
            typeof(TaskQueueViewModel).GetField("_taskStateEditingContext", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ViewModel, context);
            var item = CreateRow(new MaaInterface.MaaInterfaceTask { Name = "A", Entry = "EntryA", Check = true });
            item.OwnerViewModel = ViewModel;
            using (ViewModel.SuppressTaskStateSaving())
                ViewModel.TaskItemViewModels = new ObservableCollection<DragItemViewModel> { item };
        }

        private static DragItemViewModel CreateRow(MaaInterface.MaaInterfaceTask task)
        {
            var item = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
            UpdateRow(item, task);
            return item;
        }

        public InstanceConfiguration OpenConfiguration() =>
            (InstanceConfiguration)Activator.CreateInstance(typeof(InstanceConfiguration),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { "test", Path, new MFAConfiguration("test", "unused-test-config", new()) }, null)!;

        private static void UpdateRow(DragItemViewModel item, MaaInterface.MaaInterfaceTask task)
        {
            typeof(DragItemViewModel).GetField("_interfaceItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(item, task);
            typeof(DragItemViewModel).GetField("_isInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(item, true);
            typeof(DragItemViewModel).GetField("_isCheckedWithNull", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(item, task.Check);
        }

        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(_directory);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory escaped the temporary root.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
