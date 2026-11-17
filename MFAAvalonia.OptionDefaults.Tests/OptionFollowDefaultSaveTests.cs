using MFAAvalonia.Extensions.MaaFW;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

public partial class OptionControlsTests
{
    [Fact]
    public Task SavingDropsValuesEqualToTheDefaultButLeavesTheOnScreenModel() => Run(() =>
    {
        using var fixture = new TaskEditFixture();
        var task = fixture.ViewModel.TaskItemViewModels[0].InterfaceItem!;
        task.Option =
        [
            new MaaInterface.MaaInterfaceSelectOption { Name = "mode", SelectedCase = "A", Index = 0 },
            new MaaInterface.MaaInterfaceSelectOption { Name = "unknown", SelectedCase = "kept" }
        ];

        Assert.True(fixture.ViewModel.TrySaveTaskItems());

        var saved = JObject.Parse(File.ReadAllText(fixture.Path));
        Assert.Equal("""[{"name":"mode"},{"name":"unknown","selected_case":"kept"}]""",
            saved["TaskItems"]![0]!["option"]!.ToString(Formatting.None));
        Assert.Equal(2, saved["OptionStorageVersion"]!.Value<int>());
        Assert.Equal("A", task.Option[0].SelectedCase);

        task.Option[0].SelectedCase = "B";
        task.Option[0].Index = 1;
        Assert.True(fixture.ViewModel.TrySaveTaskItems());

        var mode = JObject.Parse(File.ReadAllText(fixture.Path))["TaskItems"]![0]!["option"]![0]!;
        Assert.Equal("B", mode["selected_case"]!.Value<string>());
        Assert.Equal(1, mode["index"]!.Value<int>());
    });
}
