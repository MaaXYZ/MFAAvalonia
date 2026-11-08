using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace MFAAvalonia.OptionDefaults.Tests;

/// <summary>
/// Exercises legacy inputs and the corrected upgrade behavior.
/// Regression assertions use the production model and loader.
/// These tests use real model serialization and default resolution, with no UI,
/// processor creation, device discovery, or user configuration files.
/// </summary>
public sealed class TaskConfigurationCharacterizationTests
{
    [Fact]
    public void SingleSelectionSerializationStoresOrdinalWithoutCaseIdentity()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption { Name = "choice", Index = 1 };

        var json = JObject.Parse(JsonConvert.SerializeObject(selection));

        Assert.Equal(1, json["index"]!.Value<int>());
        Assert.Null(json["selected_case"]);
        Assert.Null(json["case_name"]);
    }

    [Fact]
    public void SavedCaseKeepsItsMeaningAfterDefinitionReorder()
    {
        var schema = Schema("A", "B", "C");
        schema.Option!["choice"].DefaultCases = new() { "B" };
        var chosen = new MaaInterface.MaaInterfaceSelectOption { Name = "choice" };
        OptionSelection.SelectIndex(schema.Option["choice"], chosen, 2);
        var following = new MaaInterface.MaaInterfaceSelectOption { Name = "choice" };
        TaskLoader.PrepareOption(schema, chosen);
        TaskLoader.PrepareOption(schema, following);
        Assert.Null(following.Index);

        var updated = Schema("B", "C", "A");
        updated.Option!["choice"].DefaultCases = new() { "B" };
        var restoredChosen = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(
            JsonConvert.SerializeObject(chosen))!;
        var restoredFollowing = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(
            JsonConvert.SerializeObject(following))!;
        TaskLoader.PrepareOption(updated, restoredChosen);
        TaskLoader.PrepareOption(updated, restoredFollowing);

        Assert.Equal("C", restoredChosen.SelectedCase);
        Assert.Equal(1, restoredChosen.Index);
        Assert.True(OptionValues.TryGetCaseIndex(updated.Option["choice"], restoredFollowing, out var index, out _));
        Assert.Equal("B", updated.Option["choice"].Cases![index].Name);
    }

    [Fact]
    public void CheckboxSelectionAlreadyPreservesCaseNameAcrossReorder()
    {
        var selection = new MaaInterface.MaaInterfaceSelectOption
        {
            Name = "choice", SelectedCases = new() { "B" }
        };
        var restored = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceSelectOption>(
            JsonConvert.SerializeObject(selection))!;
        var updated = Schema("B", "A", "C");
        updated.Option!["choice"].Type = "checkbox";
        TaskLoader.PrepareOption(updated, restored);

        Assert.Equal(new[] { "B" }, restored.SelectedCases);
    }

    [Fact]
    public void InMemoryLazyTaskSequenceCannotBeReadBackAsListButMaterializedListCan()
    {
        var tasks = new List<MaaInterface.MaaInterfaceTask> { new() { Name = "A", Entry = "A" } };
        var lazySequence = tasks.Where(task => task.Name != null).Select(task => task);
        var convert = typeof(InstanceConfiguration)
            .GetMethod("ConvertValue", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(List<MaaInterface.MaaInterfaceTask>));

        Assert.Null(convert.Invoke(null, new object[] { lazySequence }));
        Assert.Same(tasks, convert.Invoke(null, new object[] { tasks }));
        var fromJson = Assert.IsType<List<MaaInterface.MaaInterfaceTask>>(
            convert.Invoke(null, new object[] { JArray.FromObject(lazySequence) }));
        Assert.Equal("A", Assert.Single(fromJson).Name);
    }

    [Fact]
    public void RenamedTaskOptionKeyDropsOldPayloadAndInitializesNewInputDefault()
    {
        var schema = new MaaInterface
        {
            Option = new()
            {
                ["new_key"] = new()
                {
                    Type = "input", Label = "Same display text",
                    Inputs = new() { new() { Name = "amount", Default = "42" } }
                }
            }
        };
        var oldTask = new MaaInterface.MaaInterfaceTask
        {
            Name = "task", Entry = "entry",
            Option = new() { new() { Name = "old_key", Data = new() { ["amount"] = "7" } } }
        };
        var nextTask = new MaaInterface.MaaInterfaceTask
        {
            Name = "task", Entry = "entry", Option = new() { new() { Name = "new_key" } }
        };

        ApplyTaskOptions(schema, oldTask, nextTask);

        var current = Assert.Single(oldTask.Option!);
        Assert.Equal("new_key", current.Name);
        // The renamed key is a new option: nothing is saved and it follows the current default.
        Assert.Null(current.Data);
        Assert.Equal("42", OptionValues.GetFieldValues(schema.Option["new_key"], current)["amount"]);
        Assert.DoesNotContain("old_key", JsonConvert.SerializeObject(oldTask));
    }

    [Fact]
    public void StableTaskOptionKeysKeepValuesWhenLabelsAndOptionOrderChange()
    {
        var schema = new MaaInterface
        {
            Option = new()
            {
                ["first"] = new() { Type = "input", Label = "Renamed first label", Inputs = new() { new() { Name = "amount", Default = "42" } } },
                ["second"] = new() { Type = "input", Label = "Renamed second label", Inputs = new() { new() { Name = "amount", Default = "99" } } }
            }
        };
        var oldTask = new MaaInterface.MaaInterfaceTask
        {
            Name = "task", Entry = "entry", Option = new()
            {
                new() { Name = "first", Data = new() { ["amount"] = "7" } },
                new() { Name = "second", Data = new() { ["amount"] = "8" } }
            }
        };
        var nextTask = new MaaInterface.MaaInterfaceTask
        {
            Name = "task", Entry = "entry",
            Option = new() { new() { Name = "second" }, new() { Name = "first" } }
        };

        ApplyTaskOptions(schema, oldTask, nextTask);

        Assert.Equal(new[] { "second", "first" }, oldTask.Option!.Select(option => option.Name));
        Assert.Equal(new[] { "8", "7" }, oldTask.Option!.Select(option => option.Data!["amount"]));
    }

    private static void ApplyTaskOptions(MaaInterface schema, MaaInterface.MaaInterfaceTask oldTask, MaaInterface.MaaInterfaceTask nextTask)
    {
        oldTask.Option = TaskQueueReconciler.MergeDefinition(oldTask, nextTask, schema).Option;
    }

    private static MaaInterface Schema(params string[] cases)
    {
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "select", Cases = new List<MaaInterface.MaaInterfaceOptionCase>()
        };
        foreach (var name in cases)
            definition.Cases.Add(new MaaInterface.MaaInterfaceOptionCase { Name = name });
        return new MaaInterface
        {
            Option = new Dictionary<string, MaaInterface.MaaInterfaceOption> { ["choice"] = definition }
        };
    }
}
