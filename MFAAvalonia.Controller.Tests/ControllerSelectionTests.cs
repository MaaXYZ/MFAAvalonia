using Avalonia;
using Avalonia.Headless;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using System;
using System.IO;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using Xunit;

namespace MFAAvalonia.Controller.Tests;

// Kept in a separate process from the option-control suite, whose contract excludes
// native library loading. These tests initialize the toolkit but never connect a device.
public sealed class ControllerTestApplication : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ControllerTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ControllerTestSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(ControllerTestApplication));
    public void Dispose() => Session.Dispose();
}

public sealed class ControllerSelectionTests(ControllerTestSession fixture) : IClassFixture<ControllerTestSession>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    [Theory]
    [InlineData(MaaControllerTypes.Win32, "Win32")]
    [InlineData(MaaControllerTypes.Adb, "Adb")]
    [InlineData(MaaControllerTypes.Gamepad, "Gamepad")]
    [InlineData(MaaControllerTypes.PlayCover, "PlayCover")]
    [InlineData(MaaControllerTypes.MacOS, "MacOS")]
    [InlineData(MaaControllerTypes.WlRoots, "WlRoots")]
    public void NamedControllerResolutionPrefersSavedNameForEveryBackend(MaaControllerTypes type, string key)
    {
        var catalogue = new MaaInterface
        {
            Controller = [new() { Name = "First", Type = key }, new() { Name = "Second", Type = key }]
        };
        Assert.Equal("Second", TaskLoader.GetControllerName(type, catalogue, " second "));
        catalogue.Controller.Reverse();
        Assert.Equal("Second", TaskLoader.GetControllerName(type, catalogue, "Second"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Removed")]
    [InlineData("AdbOnly")]
    public void MissingOrWrongBackendNameFallsBackToFirstMatchingBackend(string? savedName)
    {
        var catalogue = new MaaInterface
        {
            Controller = [new() { Name = "AdbOnly", Type = "Adb" },
                new() { Name = "First", Type = "Win32" }, new() { Name = "Second", Type = "Win32" }]
        };
        Assert.Equal("First", TaskLoader.GetControllerName(MaaControllerTypes.Win32, catalogue, savedName));
        Assert.Null(TaskLoader.GetControllerName(MaaControllerTypes.PlayCover, catalogue, savedName));
    }

    [Fact]
    public Task PlaceholderSelectionPreservesSavedNameInMemoryAndOnDisk() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        fixture.SetCatalogue(null);
        fixture.ViewModel.InitializeControllerOptions();
        Assert.Equal("Win32", fixture.ViewModel.SelectedController?.Name);
        fixture.AssertName("Second");

        fixture.SetCatalogue(fixture.Catalogue);
        fixture.SynchronizeOptions();
        Assert.Equal("Second", fixture.ViewModel.SelectedController?.Name);
        fixture.AssertName("Second");
    });

    [Fact]
    public Task PlaceholderTypeSynchronizationDoesNotDiscardSavedIdentity() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        fixture.SetCatalogue(null);
        NamedControllerFixture.Set(fixture.ViewModel, "_isSyncing", true);
        fixture.ViewModel.SelectedController = new() { Name = "Adb", Type = "Adb" };
        Assert.Equal(MaaControllerTypes.Adb, fixture.ViewModel.CurrentController);
        fixture.AssertName("Second");
    });

    [Fact]
    public Task RebuiltAndReorderedListKeepsNameAndSavedSettingsKey() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        var key = (MaaControllerTypes.Win32, (string?)" second ".Trim());
        NamedControllerFixture.Set(fixture.ViewModel, "_lastAppliedControllerSettingsKey", key);
        fixture.SynchronizeOptions();
        fixture.Catalogue.Controller!.Reverse();
        fixture.SynchronizeOptions();
        fixture.AssertName("Second");
        // A case-only canonicalization must not reapply defaults over saved input methods.
        fixture.Call("HandleControllerSettings", MaaControllerTypes.Win32);
        Assert.Equal(key, NamedControllerFixture.Get(fixture.ViewModel, "_lastAppliedControllerSettingsKey"));
    });

    [Fact]
    public Task RemovedSavedNameUsesAndPersistsExplicitFallback() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        fixture.Catalogue.Controller!.RemoveAt(1);
        fixture.SynchronizeOptions();
        fixture.AssertName("First");
        Assert.Equal("First", fixture.ViewModel.SelectedController?.Name);
    });

    [Fact]
    public Task LoaderOptionPanelAndTaskRowUseTheirOwningInstancesName() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        fixture.SynchronizeOptions();
        var loader = new TaskLoader(fixture.Catalogue, fixture.ViewModel);
        Assert.Equal("Second", loader.GetCurrentControllerName());
        var resolved = typeof(TaskLoader).GetMethod("GetCurrentControllerObject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(loader, null);
        Assert.Same(fixture.Catalogue.Controller![1], resolved);
        var generator = new TaskOptionGenerator(fixture.ViewModel, () => { });
        var applicable = typeof(TaskOptionGenerator).GetMethod("IsOptionApplicable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)applicable.Invoke(generator, [new MaaInterface.MaaInterfaceOption { Controller = ["Second"] }])!);
        Assert.False((bool)applicable.Invoke(generator, [new MaaInterface.MaaInterfaceOption { Controller = ["First"] }])!);
        var row = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        row.OwnerViewModel = fixture.ViewModel;
        Assert.Equal("Second", typeof(DragItemViewModel).GetMethod("GetCurrentControllerName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(row, null));
        NamedControllerFixture.Set(fixture.ViewModel, "_savedControllerName", "First");
        Assert.Equal("First", loader.GetCurrentControllerName());
    });

    [Fact]
    public Task SameResourceRefreshUpdatesTaskSupportInBothDirections() => Run(() =>
    {
        using var fixture = new NamedControllerFixture();
        fixture.SynchronizeOptions();
        var task = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        NamedControllerFixture.Set(task, "_interfaceItem", new MaaInterface.MaaInterfaceTask { Name = "C", Controller = ["Second"] });
        NamedControllerFixture.Set(task, "_isDefinitionAvailable", true);
        task.OwnerViewModel = fixture.ViewModel;
        fixture.ViewModel.TaskItemViewModels.Add(task);
        fixture.ViewModel.UpdateResourcesForController("Shared", controllerChanged: true);
        Assert.True(task.IsTaskSupported);
        NamedControllerFixture.Set(fixture.ViewModel, "_savedControllerName", "First");
        fixture.ViewModel.UpdateResourcesForController("Shared", controllerChanged: true);
        Assert.False(task.IsTaskSupported);
        Assert.Equal("Shared", fixture.ViewModel.CurrentResource);
    });

    private sealed class NamedControllerFixture : IDisposable
    {
        private readonly ControllerStorage _storage = new();
        private readonly MaaInterface? _previousCatalogue = MaaProcessor.Interface;
        public TaskQueueViewModel ViewModel => _storage.ViewModel;
        public MaaInterface Catalogue { get; } = new()
        {
            Controller = [new() { Name = "First", Type = "Win32" }, new() { Name = "Second", Type = "Win32" }],
            Resource = [new() { Name = "Shared", Path = [] }]
        };

        public NamedControllerFixture()
        {
            var processor = (MaaProcessor)RuntimeHelpers.GetUninitializedObject(typeof(MaaProcessor));
            Set(processor, "<InstanceConfiguration>k__BackingField", _storage.Configuration);
            Set(ViewModel, "_processorField", processor);
            Set(ViewModel, "_currentController", MaaControllerTypes.Win32);
            Set(ViewModel, "_savedControllerName", "Second");
            Set(ViewModel, "_currentResource", "Shared");
            Set(ViewModel, "_controllerOptions", new ObservableCollection<MaaInterface.MaaResourceController>());
            Set(ViewModel, "_currentResources", new ObservableCollection<MaaInterface.MaaInterfaceResource>());
            Set(ViewModel, "_isSyncing", true);
            SetCatalogue(Catalogue);
        }

        public void SynchronizeOptions()
        {
            Set(ViewModel, "_isSyncing", true);
            ViewModel.InitializeControllerOptions();
        }

        public void AssertName(string expected)
        {
            Assert.Equal(expected, Get(ViewModel, "_savedControllerName"));
            Assert.Equal(expected, ViewModel.GetCurrentControllerName());
            Assert.Equal(expected, _storage.OpenConfiguration().GetValue(ConfigurationKeys.CurrentControllerName, ""));
        }

        public void SetCatalogue(MaaInterface? catalogue) => typeof(MaaProcessor).GetProperty(nameof(MaaProcessor.Interface))!.SetValue(null, catalogue);
        public object? Call(string method, params object[] args) => typeof(TaskQueueViewModel)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ViewModel, args);
        public static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        public static object? Get(object target, string field) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
        public void Dispose()
        {
            SetCatalogue(_previousCatalogue);
            _storage.Dispose();
        }
    }

    private sealed class ControllerStorage : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MFAAControllerTests-" + Guid.NewGuid().ToString("N"));
        private string ConfigPath => Path.Combine(_directory, "instance.json");
        public InstanceConfiguration Configuration { get; }
        public TaskQueueViewModel ViewModel { get; } = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        public ControllerStorage()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, "{\"CurrentControllerName\":\"Second\",\"Win32ControlMouseType\":32,\"Win32ControlKeyboardType\":64}");
            Configuration = OpenConfiguration();
            NamedControllerFixture.Set(ViewModel, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>());
        }
        public InstanceConfiguration OpenConfiguration() => (InstanceConfiguration)Activator.CreateInstance(typeof(InstanceConfiguration),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { "controller-test", ConfigPath, new MFAConfiguration("controller-test", "unused", new()) }, null)!;
        public void Dispose()
        {
            if (!Path.GetFullPath(_directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test directory escaped temporary root.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}
