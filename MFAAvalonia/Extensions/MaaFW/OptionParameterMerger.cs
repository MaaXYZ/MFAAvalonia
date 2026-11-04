using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>Turns option selections into pipeline overrides without depending on a running processor.</summary>
public static class OptionParameterMerger
{
    private static readonly JsonLoadSettings JsoncLoadSettings = new()
    {
        CommentHandling = CommentHandling.Ignore
    };

    public static Dictionary<string, JToken> DeserializePipelineOverrideJsonc(string json)
    {
        var token = JToken.Parse(json, JsoncLoadSettings);
        return token.ToObject<Dictionary<string, JToken>>() ?? new Dictionary<string, JToken>();
    }

    /// <summary>
    /// PI v2.3.0 合并顺序：global_option &lt; resource.option &lt; controller.option &lt; task.option。
    /// 每个作用域各自去重，空列表跳过。
    /// </summary>
    public static void MergeScopes(
        MaaToken taskModels,
        MaaInterface? maaInterface,
        string? controllerName,
        string? resourceName,
        List<MaaInterface.MaaInterfaceSelectOption>? globalOptions,
        List<MaaInterface.MaaInterfaceSelectOption>? resourceOptions,
        List<MaaInterface.MaaInterfaceSelectOption>? controllerOptions,
        List<MaaInterface.MaaInterfaceSelectOption>? taskOptions)
    {
        foreach (var options in new[] { globalOptions, resourceOptions, controllerOptions, taskOptions })
        {
            if (options is { Count: > 0 })
                Apply(taskModels, maaInterface, controllerName, resourceName, options);
        }
    }

    /// <summary>
    /// 处理 option 列表（支持 select/switch/input 类型及子配置项）
    /// </summary>
    /// <param name="taskModels">任务参数</param>
    /// <param name="maaInterface">当前 interface 定义</param>
    /// <param name="controllerName">当前控制器名，用于判断 option 是否适用</param>
    /// <param name="resourceName">当前资源名，用于判断 option 是否适用</param>
    /// <param name="allOptions">任务的所有 option 列表</param>
    /// <param name="optionNamesToProcess">要处理的 option 名称列表（null 表示处理所有直接引用的 options）</param>
    /// <param name="processedOptions">已处理的 option 名称（避免重复处理）</param>
    public static void Apply(
        MaaToken taskModels,
        MaaInterface? maaInterface,
        string? controllerName,
        string? resourceName,
        List<MaaInterface.MaaInterfaceSelectOption> allOptions,
        List<string>? optionNamesToProcess = null,
        HashSet<string>? processedOptions = null)
    {
        processedOptions ??= new HashSet<string>();

        // 确定要处理的 options
        IEnumerable<MaaInterface.MaaInterfaceSelectOption> optionsToProcess;

        if (optionNamesToProcess == null)
        {
            // 根级调用：处理所有 options（按顺序）
            optionsToProcess = allOptions;
        }
        else
        {
            // 递归调用：只处理指定名称的 options
            optionsToProcess = allOptions.Where(o => optionNamesToProcess.Contains(o.Name ?? string.Empty));
        }

        foreach (var selectOption in optionsToProcess)
        {
            var optionName = selectOption.Name ?? string.Empty;

            // 避免重复处理同一个 option
            if (processedOptions.Contains(optionName))
                continue;

            processedOptions.Add(optionName);

            if (maaInterface?.Option?.TryGetValue(optionName, out var interfaceOption) != true)
                continue;

            // v2.3.1: 不适用于当前 controller/resource 的 option 视为未激活，
            // 它自身及其子 option 都不参与 pipeline_override 合并。
            if (!MaaInterfaceActivationHelper.IsOptionApplicable(
                    maaInterface,
                    interfaceOption,
                    controllerName,
                    resourceName))
            {
                continue;
            }

            // 处理 checkbox 类型（多选，任务 10）
            if (interfaceOption.IsCheckbox && interfaceOption.Cases != null)
            {
                var selectedCases = selectOption.SelectedCases ?? new List<string>();
                // 按 cases 定义顺序合并所有选中 case 的 pipeline_override
                foreach (var caseItem in interfaceOption.Cases)
                {
                    if (caseItem.Name != null && selectedCases.Contains(caseItem.Name))
                    {
                        if (caseItem.PipelineOverride != null)
                        {
                            taskModels.Merge(caseItem.PipelineOverride);
                        }

                        // 递归处理被选中 case 的子配置项
                        ApplySubOptions(taskModels, maaInterface, controllerName, resourceName,
                            selectOption, caseItem.Option, processedOptions);
                    }
                }
            }
            // 处理 input 类型
            else if (interfaceOption.IsInput)
            {
                if ((selectOption.Data == null || selectOption.Data.Count == 0) && interfaceOption.Inputs is { Count: > 0 })
                {
                    selectOption.Data = interfaceOption.Inputs
                        .Where(input => !string.IsNullOrEmpty(input.Name))
                        .ToDictionary(input => input.Name!, input => input.Default ?? string.Empty);
                }

                // 运行前始终按最新 Data 重建，避免 UI 某些路径未刷新缓存时继续使用旧值。
                string? pipelineOverride = selectOption.PipelineOverride;
                if (selectOption.Data != null && interfaceOption.PipelineOverride != null)
                {
                    pipelineOverride = interfaceOption.GenerateProcessedPipeline(
                        selectOption.Data.Where(kv => kv.Value != null)
                            .ToDictionary(kv => kv.Key, kv => kv.Value!));
                    selectOption.PipelineOverride = pipelineOverride;
                }

                if (!string.IsNullOrWhiteSpace(pipelineOverride) && pipelineOverride != "{}")
                {
                    var param = DeserializePipelineOverrideJsonc(pipelineOverride);
                    taskModels.Merge(param);
                }
            }
            // Resolve the saved case before deriving execution parameters.
            else if (OptionSelection.Resolve(interfaceOption, selectOption) && selectOption.Index is int index
                     && interfaceOption.Cases is { } cases
                     && index >= 0
                     && index < cases.Count)
            {
                var selectedCase = cases[index];

                // 合并当前 case 的 pipeline_override
                if (selectedCase.PipelineOverride != null)
                {
                    taskModels.Merge(selectedCase.PipelineOverride);
                }

                // 只递归处理被选中 case 的子配置项（且未被处理过的）
                ApplySubOptions(taskModels, maaInterface, controllerName, resourceName,
                    selectOption, selectedCase.Option, processedOptions);
            }
        }
    }

    private static void ApplySubOptions(
        MaaToken taskModels,
        MaaInterface? maaInterface,
        string? controllerName,
        string? resourceName,
        MaaInterface.MaaInterfaceSelectOption parent,
        List<string>? subOptionNames,
        HashSet<string> processedOptions)
    {
        if (subOptionNames == null || subOptionNames.Count == 0)
            return;

        // 过滤掉已处理的
        var unprocessedSubOptionNames = subOptionNames
            .Where(name => !processedOptions.Contains(name))
            .ToList();

        if (unprocessedSubOptionNames.Count > 0 && parent.SubOptions != null)
        {
            // 从 SubOptions 中获取子选项（已保存的用户选择值）
            var subOptionsToProcess = parent.SubOptions
                .Where(s => unprocessedSubOptionNames.Contains(s.Name ?? string.Empty))
                .ToList();

            if (subOptionsToProcess.Count > 0)
            {
                Apply(taskModels, maaInterface, controllerName, resourceName,
                    subOptionsToProcess, unprocessedSubOptionNames, processedOptions);
            }
        }

        var missingSubOptionNames = unprocessedSubOptionNames
            .Where(name => !processedOptions.Contains(name))
            .ToList();

        if (missingSubOptionNames.Count > 0)
        {
            var defaultSubOptions = CreateDefaultSelectOptions(maaInterface, missingSubOptionNames);
            if (defaultSubOptions.Count > 0)
            {
                Apply(taskModels, maaInterface, controllerName, resourceName,
                    defaultSubOptions, missingSubOptionNames, processedOptions);
            }
        }
    }

    private static List<MaaInterface.MaaInterfaceSelectOption> CreateDefaultSelectOptions(
        MaaInterface? maaInterface, IEnumerable<string> optionNames)
    {
        var result = new List<MaaInterface.MaaInterfaceSelectOption>();
        foreach (var optionName in optionNames.Distinct())
        {
            if (string.IsNullOrWhiteSpace(optionName) || maaInterface?.Option?.ContainsKey(optionName) != true)
                continue;

            var option = new MaaInterface.MaaInterfaceSelectOption
            {
                Name = optionName
            };
            TaskLoader.SetDefaultOptionValue(maaInterface, option);
            result.Add(option);
        }

        return result;
    }

    /// <summary>pretask 收到的选项 JSON：键为 option 名，值为该 option 的当前取值。</summary>
    public static string BuildPreTaskOptionJson(
        MaaInterface? maaInterface,
        IReadOnlyDictionary<string, MaaInterface.MaaInterfaceSelectOption> selected,
        IEnumerable<string> optionNames,
        string? controllerName,
        string? resourceName)
    {
        var result = new JObject();
        foreach (var name in optionNames)
        {
            if (maaInterface?.Option?.TryGetValue(name, out var definition) != true) continue;
            if (!MaaInterfaceActivationHelper.IsOptionApplicable(maaInterface, definition, controllerName, resourceName)) continue;
            selected.TryGetValue(name, out var value);
            if (definition.IsCheckbox)
                result[name] = JToken.FromObject(value?.SelectedCases ?? definition.DefaultCases ?? []);
            else if (definition.IsInput || definition.IsHotkey)
            {
                var data = value?.Data ?? new Dictionary<string, string?>();
                var defaults = definition.IsHotkey
                    ? definition.Hotkeys?.ToDictionary(x => x.Name ?? string.Empty, x => x.Default ?? string.Empty)
                    : definition.Inputs?.ToDictionary(x => x.Name ?? string.Empty, x => x.Default ?? string.Empty);
                result[name] = JObject.FromObject(defaults == null
                    ? data
                    : defaults.Concat(data).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value));
            }
            else
            {
                value ??= new MaaInterface.MaaInterfaceSelectOption { Name = name };
                if (!OptionSelection.Resolve(definition, value))
                    throw new InvalidOperationException($"{name}: {value.SelectionError.ToLocalization()}");
                result[name] = value.SelectedCase ?? string.Empty;
            }
        }
        return result.ToString(Formatting.None);
    }
}
