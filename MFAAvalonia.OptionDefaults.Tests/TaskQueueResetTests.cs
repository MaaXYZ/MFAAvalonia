using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public partial class OptionControlsTests
{
    // 用户改过 A 的选项、删掉了预设里的 B，实例记着预设 daily。
    private const string PresetInstance = """
        {"TaskItems":[{"name":"A","entry":"EntryA","check":true,"option":[{"name":"mode","selected_case":"new"}]}],
         "CurrentTasks":["A<|||>EntryA"],"DeletedTaskNames":["B"],"InstancePresetKey":"daily"}
        """;

    [Fact]
    public Task ResetRebuildsFromStoredPresetAndKeepsItsKey() => Run(() =>
    {
        using var fixture = new TaskEditFixture(PresetInstance);
        SeedResetCatalogue(fixture, Preset("daily",
            PresetTask("B", option: ("mode", "B")),
            PresetTask("A", enabled: false, option: ("mode", "A"))));
        AddSpecialRow(fixture);

        Assert.True(fixture.ViewModel.TryResetTaskQueue(out var preset, out var staleKey));

        Assert.Equal("daily", preset?.Name);
        Assert.Null(staleKey);
        Assert.True(fixture.OpenConfiguration().TryReadTaskState(out var saved, out var error), error);
        Assert.Equal("daily", saved.PresetKey);
        Assert.Equal(new[] { "B", "A", "倒计时" }, saved.Tasks.Select(task => task.Name));
        Assert.Empty(saved.DeletedTaskNames);
        Assert.Equal("B", Assert.Single(saved.Tasks[0].Option!).SelectedCase);
        Assert.False(saved.Tasks[1].Check);
        // 用户原来的 new 被丢弃；预设给的值恰好等于默认，不存，跟随默认。
        var mode = Assert.Single(saved.Tasks[1].Option!);
        Assert.Null(mode.SelectedCase);
        Assert.Null(mode.Index);
        Assert.Equal(new[] { "B", "A", "倒计时" },
            fixture.ViewModel.TaskItemViewModels.Select(item => item.InterfaceItem?.Name));
    });

    [Fact]
    public Task ResetUsesTheCurrentVersionOfTheStoredPreset() => Run(() =>
    {
        using var fixture = new TaskEditFixture(PresetInstance);
        var daily = Preset("daily", PresetTask("B", option: ("mode", "B")));
        SeedResetCatalogue(fixture, daily);
        Assert.True(fixture.ViewModel.TryResetTaskQueue(out _, out _));

        daily.Task = [PresetTask("C", option: ("mode", "keep")), PresetTask("A")];
        Assert.True(fixture.ViewModel.TryResetTaskQueue(out var preset, out _));

        Assert.Same(daily, preset);
        Assert.True(fixture.OpenConfiguration().TryReadTaskState(out var saved, out var error), error);
        Assert.Equal("daily", saved.PresetKey);
        Assert.Equal(new[] { "C", "A" }, saved.Tasks.Select(task => task.Name));
        Assert.Equal("keep", Assert.Single(saved.Tasks[0].Option!).SelectedCase);
        Assert.Null(Assert.Single(saved.Tasks[1].Option!).SelectedCase);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("gone")]
    public Task ResetWithoutValidPresetRebuildsFromTaskListAndClearsKey(string? key) => Run(() =>
    {
        var keyJson = key == null ? "" : $",\"InstancePresetKey\":\"{key}\"";
        using var fixture = new TaskEditFixture(
            "{\"TaskItems\":[{\"name\":\"A\",\"entry\":\"EntryA\",\"option\":[{\"name\":\"mode\",\"selected_case\":\"new\"}]}],"
            + "\"CurrentTasks\":[\"A<|||>EntryA\"],\"DeletedTaskNames\":[\"B\"]" + keyJson + "}");
        SeedResetCatalogue(fixture, Preset("daily", PresetTask("B")));
        AddSpecialRow(fixture);

        Assert.True(fixture.ViewModel.TryResetTaskQueue(out var preset, out var staleKey));

        Assert.Null(preset);
        Assert.Equal(key, staleKey);
        Assert.True(fixture.OpenConfiguration().TryReadTaskState(out var saved, out var error), error);
        Assert.Null(saved.PresetKey);
        Assert.Equal(new[] { "A", "B", "C", "倒计时" }, saved.Tasks.Select(task => task.Name));
        Assert.Empty(saved.DeletedTaskNames);
        Assert.All(saved.Tasks.Where(task => task.Option != null).SelectMany(task => task.Option!),
            option => Assert.Null(option.SelectedCase));
    });

    [Fact]
    public Task ResetTipNamesTheStoredPresetOnlyWhileItExists() => Run(() =>
    {
        using var fixture = new TaskEditFixture(PresetInstance);
        var daily = Preset("daily");
        daily.Label = "每日方案";
        SeedResetCatalogue(fixture, daily);

        // 测试环境不加载翻译资源，格式化结果只能比对到键；文字本身由 TaskConfigurationLocalizationTests 核对。
        Assert.Equal(LangKeys.ResetTasksToPreset.ToLocalizationFormatted(false, "每日方案"), fixture.ViewModel.ResetTasksTip);
        Assert.NotEqual(LangKeys.Reset.ToLocalization(), fixture.ViewModel.ResetTasksTip);

        fixture.Catalogue.Preset = [];
        Assert.Equal(LangKeys.Reset.ToLocalization(), fixture.ViewModel.ResetTasksTip);
    });

    [Fact]
    public Task SavingWithOptionRowsNeverWritesNullTasks() => Run(() =>
    {
        // 切换实例前的保存走这里；队列顶部的选项行没有任务对象，不得写成 null 项。
        using var fixture = new TaskEditFixture();
        var optionRow = new DragItemViewModel(new MaaInterface.MaaInterfaceResource { Name = "__GlobalOption__" });
        using (fixture.ViewModel.SuppressTaskStateSaving())
            fixture.ViewModel.TaskItemViewModels.Insert(0, optionRow);

        Assert.True(fixture.ViewModel.TrySaveTaskItems());

        var written = JObject.Parse(File.ReadAllText(fixture.Path))["TaskItems"] as JArray;
        Assert.NotNull(written);
        Assert.Equal(new[] { "A" }, written.Select(task => task.Type == JTokenType.Null ? null : task["name"]?.Value<string>()));
    });

    [Fact]
    public Task SavingADeletedInstanceIsSkippedWithoutReportingAFailure() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        fixture.Configuration.DeleteConfigFile();

        Assert.True(fixture.ViewModel.TrySaveTaskItems());

        Assert.Empty(fixture.Errors);
        Assert.False(File.Exists(fixture.Path));
    });

    private static void SeedResetCatalogue(TaskEditFixture fixture, params MaaInterface.MaaInterfacePreset[] presets)
    {
        fixture.Catalogue.Task = new[] { "A", "B", "C" }
            .Select(name => new MaaInterface.MaaInterfaceTask { Name = name, Entry = "Entry" + name, Check = true })
            .ToList();
        fixture.Catalogue.Preset = presets.ToList();
        fixture.Templates.AddRange(fixture.Catalogue.Task.Select(task => new DragItemViewModel(
            new MaaInterface.MaaInterfaceTask
            {
                Name = task.Name, Entry = task.Entry, Check = true,
                Option = [new MaaInterface.MaaInterfaceSelectOption { Name = "mode" }]
            })));
    }

    private static void AddSpecialRow(TaskEditFixture fixture)
    {
        var special = new DragItemViewModel(new MaaInterface.MaaInterfaceTask
            { Name = "倒计时", Entry = "CountdownAction", Check = true }) { OwnerViewModel = fixture.ViewModel };
        using (fixture.ViewModel.SuppressTaskStateSaving())
            fixture.ViewModel.TaskItemViewModels.Add(special);
    }

    private static MaaInterface.MaaInterfacePreset Preset(string name, params MaaInterface.MaaInterfacePresetTask[] tasks) =>
        new() { Name = name, Task = tasks.ToList() };

    private static MaaInterface.MaaInterfacePresetTask PresetTask(string name, bool? enabled = null,
        (string Name, string Case)? option = null) =>
        new()
        {
            Name = name,
            Enabled = enabled,
            Option = option is { } value ? new() { [value.Name] = JToken.FromObject(value.Case) } : null
        };
}
