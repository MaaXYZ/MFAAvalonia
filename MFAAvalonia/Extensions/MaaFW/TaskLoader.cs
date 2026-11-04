using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// 任务加载器
/// </summary>
public class TaskLoader(MaaInterface? maaInterface, TaskQueueViewModel taskQueueViewModel)
{
    public const string NEW_SEPARATOR = "<|||>";
    public const string OLD_SEPARATOR = ":";


    /// <summary>
    /// 加载任务列表
    /// </summary>
    private static readonly string ClientLaunchId = Guid.NewGuid().ToString("N");
    private bool _initialLoadAttempted;

    public void UpdateInterface(MaaInterface? value) => maaInterface = value;

    public bool LoadTasks(
        List<MaaInterface.MaaInterfaceTask> tasks,
        ObservableCollection<DragItemViewModel> tasksSource,
        ref bool firstTask,
        IList<DragItemViewModel>? oldDrags = null)
    {
        var allowRemoval = !_initialLoadAttempted;
        _initialLoadAttempted = true;
        var config = taskQueueViewModel.Processor.InstanceConfiguration;
        using var publication = taskQueueViewModel.SuppressTaskStateSaving();
        var failureKey = LangKeys.TaskConfigurationReadFailed;
        try
        {
            if (!config.TryReadTaskState(out var saved, out var readError))
                throw new InvalidOperationException(readError);
            failureKey = LangKeys.TaskConfigurationDefinitionsNotLoaded;
            if (maaInterface?.Task == null)
                throw new InvalidOperationException("资源任务定义未完整加载，已保留原任务配置。");
            if (oldDrags != null)
                saved.Tasks = oldDrags.Where(item => !item.IsResourceOptionItem && item.InterfaceItem != null)
                    .Select(item => item.InterfaceItem!.Clone()).ToList();

            failureKey = LangKeys.TaskConfigurationDefinitionsInvalid;
            var result = TaskQueueReconciler.Reconcile(saved, maaInterface, ClientLaunchId,
                allowRemoval, AddTaskDialogViewModel.SpecialActionNames);
            failureKey = LangKeys.TaskConfigurationSaveFailed;
            if (!config.TryCommitTaskState(result.State, out var saveError))
            {
                failureKey = TaskConfigurationFeedback.GetSaveFailureKey(config);
                throw new InvalidOperationException(saveError);
            }
            failureKey = LangKeys.TaskConfigurationSyncFailed;
            taskQueueViewModel.IsTaskConfigurationValid = true;

            if (firstTask)
            {
                InitializeResources();
                firstTask = false;
            }
            var previousRows = (oldDrags ?? taskQueueViewModel.TaskItemViewModels)
                .Where(item => !item.IsResourceOptionItem)
                .GroupBy(item => (item.InterfaceItem?.Name,
                    item.InterfaceItem?.Entry != null && AddTaskDialogViewModel.SpecialActionNames.Contains(item.InterfaceItem.Entry)))
                .ToDictionary(group => group.Key, group => new Queue<DragItemViewModel>(group));
            var drags = result.State.Tasks.Select(task =>
            {
                var item = new DragItemViewModel(task) { OwnerViewModel = taskQueueViewModel };
                var isSpecial = task.Entry != null && AddTaskDialogViewModel.SpecialActionNames.Contains(task.Entry);
                if (previousRows.TryGetValue((task.Name, isSpecial), out var previous) && previous.Count > 0)
                    item.CopyRuntimeStateFrom(previous.Dequeue());
                item.IsDefinitionAvailable = isSpecial || !result.MissingNames.Contains(task.Name ?? string.Empty);
                item.UnavailableReasonKey = item.IsDefinitionAvailable ? null : result.CircuitBroken
                    ? LangKeys.TaskCleanupPaused
                    : LangKeys.TaskMissingFromResource;
                if (isSpecial) task.Description = AddTaskDialogViewModel.GetSpecialTaskDescription(task.Entry!);
                return item;
            }).ToList();
            UpdateViewModels(drags, tasks, tasksSource);
            if (result.CircuitBroken)
                ToastHelper.Warn(LangKeys.TaskConfigurationTitle.ToLocalization(), LangKeys.TaskCleanupPaused.ToLocalization());
            foreach (var group in result.RemovedNames.GroupBy(name => name))
                LoggerHelper.Info($"自动移除仍缺失的资源任务：{group.Key}，数量={group.Count()}");
            return true;
        }
        catch (Exception ex)
        {
            taskQueueViewModel.IsTaskConfigurationValid = false;
            LoggerHelper.Error("任务配置同步失败，未初始化或覆盖原任务队列。", ex);
            ToastHelper.Warn(LangKeys.TaskConfigurationTitle.ToLocalization(), failureKey.ToLocalization());
            return false;
        }
    }

    private void InitializeResources()
    {
        var allResources = maaInterface?.Resources.Values.Count > 0
            ? maaInterface.Resources.Values.ToList()
            :
            [
                new()
                {
                    Name = "Default",
                    Path = [MaaProcessor.ResourceBase]
                }
            ];

        // 获取当前控制器的名称
        var currentControllerName = GetCurrentControllerName();

        // 根据控制器过滤资源
        var filteredResources = FilterResourcesByController(allResources, currentControllerName);

        foreach (var resource in filteredResources)
        {
            resource.InitializeDisplayName();
            // 初始化资源的 SelectOptions
            InitializeResourceSelectOptions(resource, maaInterface, taskQueueViewModel.Processor.InstanceConfiguration);
        }
        taskQueueViewModel.CurrentResources = new ObservableCollection<MaaInterface.MaaInterfaceResource>(filteredResources);
        taskQueueViewModel.CurrentResource = taskQueueViewModel.Processor.InstanceConfiguration.GetValue(ConfigurationKeys.Resource, string.Empty);
        if (taskQueueViewModel.CurrentResources.Count > 0 && taskQueueViewModel.CurrentResources.All(r => r.Name != taskQueueViewModel.CurrentResource))
            taskQueueViewModel.CurrentResource = taskQueueViewModel.CurrentResources[0].Name ?? "Default";

        // 初始化 global_option 的 SelectOptions（同时填充 Interface.GlobalSelectOptions 供 OptionParameterMerger.MergeScopes 使用）
        InitializeGlobalSelectOptions();

        // 初始化当前控制器的 SelectOptions（供 OptionParameterMerger.MergeScopes 使用）
        InitializeControllerSelectOptions();
    }

    /// <summary>
    /// 初始化 global_option 的 SelectOptions，并同步到 Interface.GlobalSelectOptions
    /// </summary>
    private void InitializeGlobalSelectOptions()
    {
        if (maaInterface == null || maaInterface.GlobalOption == null || maaInterface.GlobalOption.Count == 0)
        {
            if (maaInterface != null)
                maaInterface.GlobalSelectOptions = null;
            return;
        }

        var config = taskQueueViewModel.Processor.InstanceConfiguration;
        var savedGlobalOptions = config.GetValue(
            ConfigurationKeys.GlobalOptionItems,
            new List<MaaInterface.MaaInterfaceSelectOption>());
        var savedDict = savedGlobalOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        var existingDict = maaInterface.GlobalSelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        maaInterface.GlobalSelectOptions = maaInterface.GlobalOption.Select(optionName =>
        {
            if (savedDict.TryGetValue(optionName, out var saved))
            {
                var savedOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = saved.Name,
                    Index = saved.Index,
                    SelectedCases = saved.SelectedCases != null ? new List<string>(saved.SelectedCases) : null,
                    SubOptions = saved.SubOptions != null ? CloneSubOptions(saved.SubOptions) : null,
                };
                savedOption.CopySelectionFrom(saved);
                savedOption.CopyInputDataFrom(saved);
                SetDefaultOptionValue(maaInterface, savedOption);
                return savedOption;
            }
            if (existingDict.TryGetValue(optionName, out var existing))
            {
                SetDefaultOptionValue(maaInterface, existing);
                return existing;
            }
            var opt = new MaaInterface.MaaInterfaceSelectOption { Name = optionName };
            SetDefaultOptionValue(maaInterface, opt);
            return opt;
        }).ToList();
    }

    /// <summary>
    /// 初始化资源的 SelectOptions（从 Option 字符串列表转换为 MaaInterfaceSelectOption 列表）
    /// 只初始化顶级选项，子选项会在运行时由 UpdateSubOptions 动态创建
    /// 会保留已有的值并从配置中恢复保存的值
    /// </summary>
    public static void InitializeResourceSelectOptions(MaaInterface.MaaInterfaceResource resource, MaaInterface? maaInterface, InstanceConfiguration config)
    {
        if (resource.Option == null || resource.Option.Count == 0)
        {
            resource.SelectOptions = null;
            return;
        }

        // 收集所有子选项名称（这些选项不应该在顶级初始化）
        var subOptionNames = new HashSet<string>();
        foreach (var optionName in resource.Option)
        {
            if (maaInterface?.Option?.TryGetValue(optionName, out var interfaceOption) == true)
            {
                if (interfaceOption.Cases != null)
                {
                    foreach (var caseOption in interfaceOption.Cases)
                    {
                        if (caseOption.Option != null)
                        {
                            foreach (var subOptionName in caseOption.Option)
                            {
                                subOptionNames.Add(subOptionName);
                            }
                        }
                    }
                }
            }
        }

        // 获取已保存的配置
        var savedResourceOptions = config.GetValue(
            ConfigurationKeys.ResourceOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());

        Dictionary<string, MaaInterface.MaaInterfaceSelectOption>? savedDict = null;
        if (savedResourceOptions.TryGetValue(resource.Name ?? string.Empty, out var savedOptions) && savedOptions != null)
        {
            savedDict = savedOptions.ToDictionary(o => o.Name ?? string.Empty);
        }

        // 保留已有的 SelectOptions 值
        var existingDict = resource.SelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        // 只初始化顶级选项（不是子选项的选项）
        resource.SelectOptions = resource.Option
            .Where(optionName => !subOptionNames.Contains(optionName))
            .Select(optionName =>
            {
                // 配置文件中的值属于当前实例，应优先于 Interface 上其他实例留下的运行时状态。
                if (savedDict?.TryGetValue(optionName, out var savedOpt) == true)
                {
                    // 克隆保存的选项，避免引用问题
                    var clonedOpt = new MaaInterface.MaaInterfaceSelectOption
                    {
                        Name = savedOpt.Name,
                        Index = savedOpt.Index,
                        SubOptions = savedOpt.SubOptions != null ? CloneSubOptions(savedOpt.SubOptions) : null,
                        SelectedCases = savedOpt.SelectedCases != null ? new List<string>(savedOpt.SelectedCases) : null,
                    };
                    clonedOpt.CopySelectionFrom(savedOpt);
                    clonedOpt.CopyInputDataFrom(savedOpt);
                    SetDefaultOptionValue(maaInterface, clonedOpt);
                    return clonedOpt;
                }
                if (existingDict.TryGetValue(optionName, out var existingOpt))
                {
                    SetDefaultOptionValue(maaInterface, existingOpt);
                    return existingOpt;
                }
                // 最后创建新的并设置默认值
                var selectOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = optionName
                };
                SetDefaultOptionValue(maaInterface, selectOption);
                return selectOption;
            }).ToList();
    }

    /// <summary>
    /// 克隆子选项列表
    /// </summary>
    private static List<MaaInterface.MaaInterfaceSelectOption> CloneSubOptions(List<MaaInterface.MaaInterfaceSelectOption> subOptions)
    {
        return subOptions.Select(opt =>
        {
            var clone = new MaaInterface.MaaInterfaceSelectOption
            {
                Name = opt.Name,
                Index = opt.Index,
                SubOptions = opt.SubOptions != null ? CloneSubOptions(opt.SubOptions) : null,
                SelectedCases = opt.SelectedCases != null ? new List<string>(opt.SelectedCases) : null,
            };
            clone.CopySelectionFrom(opt);
            clone.CopyInputDataFrom(opt);
            return clone;
        }).ToList();
    }
    /// <summary>
    /// 获取当前控制器的名称
    /// </summary>
    public string? GetCurrentControllerName()
    {
        return GetControllerName(taskQueueViewModel.CurrentController, maaInterface);
    }

    public static string? GetControllerName(MaaControllerTypes currentControllerType, MaaInterface? maaInterface)
    {
        var controllerTypeKey = currentControllerType.ToJsonKey();

        // 从 interface 的 controller 配置中查找匹配的控制器
        var controller = maaInterface?.Controller?.Find(c =>
            c.Type != null && c.Type.Equals(controllerTypeKey, StringComparison.OrdinalIgnoreCase));

        return controller?.Name;
    }

    /// <summary>
    /// 根据控制器过滤资源
    /// </summary>
    /// <param name="resources">所有资源列表</param>
    /// <param name="controllerName">当前控制器名称</param>
    /// <returns>过滤后的资源列表</returns>
    public static List<MaaInterface.MaaInterfaceResource> FilterResourcesByController(
        List<MaaInterface.MaaInterfaceResource> resources,
        string? controllerName)
    {
        return resources.Where(r =>
        {
            // 如果资源没有指定 controller，则支持所有控制器
            if (r.Controller == null || r.Controller.Count == 0)
                return true;

            // 如果当前控制器名称为空，则显示所有资源
            if (string.IsNullOrWhiteSpace(controllerName))
                return true;

            // 检查资源是否支持当前控制器
            return r.Controller.Any(c =>
                c.Equals(controllerName, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    public static void SetDefaultOptionValue(MaaInterface? @interface, MaaInterface.MaaInterfaceSelectOption option)
    {
        // Migrate saved passwords in inactive branches without activating those branches.
        if (@interface?.Option != null) option.ProtectInputData(@interface.Option);
        if (@interface?.Option != null) OptionSelection.ResolveTree(@interface.Option, [option]);
        SetDefaultOptionValue(@interface, option, new HashSet<string>(StringComparer.Ordinal));
    }

    private static void SetDefaultOptionValue(
        MaaInterface? @interface,
        MaaInterface.MaaInterfaceSelectOption option,
        HashSet<string> optionPath)
    {
        if (!(@interface?.Option?.TryGetValue(option.Name ?? string.Empty, out var io) ?? false)) return;
        var hasName = !string.IsNullOrEmpty(option.Name);
        if (hasName && !optionPath.Add(option.Name!))
        {
            LoggerHelper.Warning($"检测到循环嵌套的 option，已跳过默认子选项展开：{string.Join(" -> ", optionPath)} -> {option.Name}");
            return;
        }

        try
        {
            if (!OptionSelection.Resolve(io, option)) return;
            if (io.IsInput && io.Inputs != null)
            {
                option.InitializeInputData(io);
            }
            if (io.IsHotkey && io.Hotkeys != null)
            {
                option.Data ??= new Dictionary<string, string?>();
                foreach (var hotkey in io.Hotkeys)
                    if (!string.IsNullOrEmpty(hotkey.Name) && !option.Data.ContainsKey(hotkey.Name))
                        option.Data[hotkey.Name] = hotkey.Default ?? string.Empty;
            }
            // checkbox 类型：从 DefaultCases 初始化 SelectedCases
            if (io.IsCheckbox)
            {
                option.SelectedCases ??= new List<string>(io.DefaultCases ?? new List<string>());
            }

            EnsureDefaultSubOptions(@interface, option, io, optionPath);
        }
        finally
        {
            if (hasName)
            {
                optionPath.Remove(option.Name!);
            }
        }
    }

    private static void EnsureDefaultSubOptions(
        MaaInterface? @interface,
        MaaInterface.MaaInterfaceSelectOption option,
        MaaInterface.MaaInterfaceOption interfaceOption,
        HashSet<string> optionPath)
    {
        if (@interface?.Option == null || interfaceOption.Cases == null || interfaceOption.Cases.Count == 0)
            return;

        option.SubOptions ??= new List<MaaInterface.MaaInterfaceSelectOption>();

        IEnumerable<string> activeSubOptionNames = Array.Empty<string>();
        if (interfaceOption.IsCheckbox)
        {
            var selectedCases = option.SelectedCases ?? new List<string>();
            activeSubOptionNames = interfaceOption.Cases
                .Where(caseItem => !string.IsNullOrEmpty(caseItem.Name) && selectedCases.Contains(caseItem.Name!))
                .SelectMany(caseItem => caseItem.Option ?? Enumerable.Empty<string>());
        }
        else if (option.Index is int index && index >= 0 && index < interfaceOption.Cases.Count)
        {
            activeSubOptionNames = interfaceOption.Cases[index].Option ?? Enumerable.Empty<string>();
        }

        foreach (var subOptionName in activeSubOptionNames.Distinct())
        {
            if (string.IsNullOrEmpty(subOptionName))
            {
                continue;
            }

            if (optionPath.Contains(subOptionName))
            {
                LoggerHelper.Warning($"检测到循环嵌套的 option，已跳过默认子选项展开：{string.Join(" -> ", optionPath)} -> {subOptionName}");
                option.SubOptions.RemoveAll(o => o.Name == subOptionName);
                continue;
            }

            var subOption = option.SubOptions.FirstOrDefault(o => o.Name == subOptionName);
            if (subOption == null)
            {
                subOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = subOptionName
                };
                option.SubOptions.Add(subOption);
            }

            SetDefaultOptionValue(@interface, subOption, optionPath);
        }
    }

    private void UpdateViewModels(IList<DragItemViewModel> drags, List<MaaInterface.MaaInterfaceTask> tasks, ObservableCollection<DragItemViewModel> tasksSource)
    {
        // 克隆任务对象，避免多实例共享同一个 MaaInterfaceTask 对象
        var newItems = tasks.Select(t => new DragItemViewModel(t.Clone()) { OwnerViewModel = taskQueueViewModel }).ToList();
        foreach (var item in newItems)
        {
            if (item.InterfaceItem?.Option != null && !drags.Any())
                item.InterfaceItem.Option.ForEach(option => SetDefaultOptionValue(maaInterface, option));
        }

        // 检查当前资源是否有全局选项配置
        var currentResourceName = taskQueueViewModel.CurrentResource;
        var currentResource = taskQueueViewModel.CurrentResources
            .FirstOrDefault(r => r.Name == currentResourceName);

        // 创建最终的任务列表
        var finalItems = new List<DragItemViewModel>();

        // PI v2.8.0 setting：复用现有 option 渲染器生成独立设置分区。
        foreach (var setting in maaInterface?.Setting ?? [])
        {
            var settingItem = CreateSettingItem(setting, drags);
            if (settingItem != null) finalItems.Add(settingItem);
        }

        // 如果有 global_option，置顶显示一个全局设置项（包含所有全局选项）
        if (maaInterface?.GlobalOption is { Count: > 0 } && maaInterface.GlobalSelectOptions is { Count: > 0 })
        {
            var globalOptionItem = CreateGlobalOptionItem(drags);
            if (globalOptionItem != null)
                finalItems.Add(globalOptionItem);
        }

        // 如果当前资源有 option 配置，在全局选项后添加资源设置项
        if (currentResource?.Option is {Count: > 0})
        {
            var resourceOptionItem = CreateResourceOptionItem(currentResource, drags);
            if (resourceOptionItem != null)
            {
                finalItems.Add(resourceOptionItem);
            }
        }

        // 如果当前控制器有 option 配置，在资源选项后添加控制器设置项
        var currentControllerObj = GetCurrentControllerObject();
        if (currentControllerObj?.Option is { Count: > 0 } && currentControllerObj.SelectOptions is { Count: > 0 })
        {
            var controllerOptionItem = CreateControllerOptionItem(currentControllerObj, drags);
            if (controllerOptionItem != null)
                finalItems.Add(controllerOptionItem);
        }

        // An explicitly empty queue stays empty; seeding is owned by the reconciler.
        finalItems.AddRange(drags.Where(item => !item.IsResourceOptionItem));

        // UI 线程更新集合，确保 TaskList 刷新
        DispatcherHelper.RunOnMainThread(() =>
        {
            using var publication = taskQueueViewModel.SuppressTaskStateSaving();
            tasksSource.Clear();
            foreach (var item in newItems) tasksSource.Add(item);

            taskQueueViewModel.TaskItemViewModels.Clear();
            foreach (var item in finalItems)
            {
                taskQueueViewModel.TaskItemViewModels.Add(item);
            }

            // 根据当前资源更新任务的可见性
            taskQueueViewModel.UpdateTasksForResource(currentResourceName);
        });
    }

    private DragItemViewModel? CreateSettingItem(MaaInterface.MaaInterfaceSetting setting, IList<DragItemViewModel>? existingDrags)
    {
        if (setting.Option is not { Count: > 0 }) return null;
        var syntheticName = $"__Setting__{setting.Name}";
        var existing = existingDrags?.FirstOrDefault(d => d.IsResourceOptionItem && d.ResourceItem?.Name == syntheticName);
        if (existing != null) return existing;

        var saved = taskQueueViewModel.Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.ResourceOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());
        var savedOptions = saved.GetValueOrDefault(syntheticName)?.ToDictionary(x => x.Name ?? string.Empty);
        var options = setting.Option.Select(name =>
        {
            var value = savedOptions?.GetValueOrDefault(name) ?? new MaaInterface.MaaInterfaceSelectOption { Name = name };
            SetDefaultOptionValue(maaInterface, value);
            return value;
        }).ToList();
        var synthetic = new MaaInterface.MaaInterfaceResource
        {
            Name = syntheticName,
            Label = setting.Label ?? setting.Name,
            Description = setting.Description,
            Icon = setting.Icon,
            SelectOptions = options
        };
        synthetic.InitializeDisplayName();
        return new DragItemViewModel(synthetic)
        {
            OwnerViewModel = taskQueueViewModel,
            IsVisible = true,
            EnableSetting = setting.DefaultExpand ?? true
        };
    }

    /// <summary>
    /// 创建 global_option 的置顶任务项（包含所有全局选项，与资源选项项行为一致）
    /// </summary>
    private DragItemViewModel? CreateGlobalOptionItem(IList<DragItemViewModel>? existingDrags)
    {
        if (maaInterface?.GlobalSelectOptions == null || maaInterface.GlobalSelectOptions.Count == 0)
            return null;

        // 检查是否已存在全局选项项
        var existing = existingDrags?.FirstOrDefault(d =>
            d.IsResourceOptionItem && d.ResourceItem?.Name == "__GlobalOption__");
        if (existing != null)
            return existing;

        // 创建合成资源，Name 固定为 "__GlobalOption__"，由 DragItemViewModel 按 Name 处理 i18n 显示
        var syntheticResource = new MaaInterface.MaaInterfaceResource
        {
            Name = "__GlobalOption__",
            SelectOptions = maaInterface.GlobalSelectOptions,
        };
        syntheticResource.InitializeDisplayName();

        var item = new DragItemViewModel(syntheticResource) { OwnerViewModel = taskQueueViewModel };
        item.IsVisible = true;
        return item;
    }

    /// <summary>
    /// 获取当前控制器对象
    /// </summary>
    private MaaInterface.MaaResourceController? GetCurrentControllerObject()
    {
        var controllerName = GetCurrentControllerName();
        if (string.IsNullOrWhiteSpace(controllerName)) return null;
        return maaInterface?.Controller?.Find(c =>
            c.Name != null && c.Name.Equals(controllerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 初始化当前控制器的 SelectOptions，供 OptionParameterMerger.MergeScopes 使用
    /// </summary>
    private void InitializeControllerSelectOptions()
    {
        var controller = GetCurrentControllerObject();
        if (controller?.Option == null || controller.Option.Count == 0)
        {
            if (controller != null) controller.SelectOptions = null;
            return;
        }

        var config = taskQueueViewModel.Processor.InstanceConfiguration;
        var savedOptions = config.GetValue(
            ConfigurationKeys.ControllerOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());

        Dictionary<string, MaaInterface.MaaInterfaceSelectOption>? savedDict = null;
        if (savedOptions.TryGetValue(controller.Name ?? string.Empty, out var savedList) && savedList != null)
            savedDict = savedList.ToDictionary(o => o.Name ?? string.Empty);

        var existingDict = controller.SelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        controller.SelectOptions = controller.Option.Select(optionName =>
        {
            if (savedDict?.TryGetValue(optionName, out var saved) == true)
            {
                var savedOption = new MaaInterface.MaaInterfaceSelectOption
                {
                    Name = saved.Name,
                    Index = saved.Index,
                    SelectedCases = saved.SelectedCases != null ? new List<string>(saved.SelectedCases) : null,
                    SubOptions = saved.SubOptions != null ? CloneSubOptions(saved.SubOptions) : null,
                };
                savedOption.CopySelectionFrom(saved);
                savedOption.CopyInputDataFrom(saved);
                SetDefaultOptionValue(maaInterface, savedOption);
                return savedOption;
            }
            if (existingDict.TryGetValue(optionName, out var existing))
            {
                SetDefaultOptionValue(maaInterface, existing);
                return existing;
            }
            var opt = new MaaInterface.MaaInterfaceSelectOption { Name = optionName };
            SetDefaultOptionValue(maaInterface, opt);
            return opt;
        }).ToList();
    }

    /// <summary>
    /// 创建控制器级别选项的置顶任务项
    /// </summary>
    private DragItemViewModel? CreateControllerOptionItem(MaaInterface.MaaResourceController controller, IList<DragItemViewModel>? existingDrags)
    {
        if (controller.SelectOptions == null || controller.SelectOptions.Count == 0)
            return null;

        var syntheticName = $"__ControllerOption__{controller.Name}";
        var existing = existingDrags?.FirstOrDefault(d =>
            d.IsResourceOptionItem && d.ResourceItem?.Name == syntheticName);
        if (existing != null)
            return existing;

        // Name 固定为 "__ControllerOption__{controllerName}"，由 DragItemViewModel 按 Name 处理 i18n 显示
        var syntheticResource = new MaaInterface.MaaInterfaceResource
        {
            Name = syntheticName,
            SelectOptions = controller.SelectOptions,
        };
        syntheticResource.InitializeDisplayName();

        var item = new DragItemViewModel(syntheticResource) { OwnerViewModel = taskQueueViewModel };
        item.IsVisible = true;
        return item;
    }

    /// <summary>
    /// 创建资源全局选项的任务项
    /// </summary>
    private DragItemViewModel? CreateResourceOptionItem(MaaInterface.MaaInterfaceResource resource, IList<DragItemViewModel>? existingDrags)
    {
        if (resource.Option == null || resource.Option.Count == 0)
            return null;

        // 从配置中加载已保存的资源选项
        var savedResourceOptions = taskQueueViewModel.Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.ResourceOptionItems,
            new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>());

        // 检查是否已经存在对应的资源设置项
        var existingResourceItem = existingDrags?.FirstOrDefault(d =>
            d.IsResourceOptionItem && d.ResourceItem?.Name == resource.Name);

        if (existingResourceItem != null)
        {
            // 更新已存在的资源设置项的 SelectOptions
            if (resource.SelectOptions != null && existingResourceItem.ResourceItem != null)
            {
                // 合并已保存的选项值
                MergeResourceSelectOptions(existingResourceItem.ResourceItem, resource);
            }
            return existingResourceItem;
        }

        // 如果配置中有保存的选项值，恢复它们
        if (savedResourceOptions.TryGetValue(resource.Name ?? string.Empty, out var savedOptions) && savedOptions != null)
        {
            // 恢复配置中保存的选项值到 resource.SelectOptions
            if (resource.SelectOptions != null)
            {
                var savedDict = savedOptions.ToDictionary(o => o.Name ?? string.Empty);
                foreach (var opt in resource.SelectOptions)
                {
                    if (savedDict.TryGetValue(opt.Name ?? string.Empty, out var savedOpt))
                    {
                        opt.CopySelectionFrom(savedOpt);
                        opt.CopyInputDataFrom(savedOpt);
                        opt.SubOptions = savedOpt.SubOptions == null ? null : CloneSubOptions(savedOpt.SubOptions);
                        opt.SelectedCases = savedOpt.SelectedCases != null ? new List<string>(savedOpt.SelectedCases) : null;
                        SetDefaultOptionValue(maaInterface, opt);
                    }
                }
            }
        }

        // 创建新的资源设置项
        var resourceItem = new DragItemViewModel(resource) { OwnerViewModel = taskQueueViewModel };

        // 设置 IsVisible 为 true，因为资源设置项有选项需要显示
        resourceItem.IsVisible = true;

        return resourceItem;
    }

    /// <summary>
    /// 合并资源的 SelectOptions（保留用户已选择的值）
    /// </summary>
    private void MergeResourceSelectOptions(MaaInterface.MaaInterfaceResource existingResource, MaaInterface.MaaInterfaceResource newResource)
    {
        if (newResource.SelectOptions == null)
        {
            existingResource.SelectOptions = null;
            return;
        }

        var existingDict = existingResource.SelectOptions?.ToDictionary(o => o.Name ?? string.Empty)
            ?? new Dictionary<string, MaaInterface.MaaInterfaceSelectOption>();

        existingResource.SelectOptions = newResource.SelectOptions.Select(newOpt =>
        {
            if (existingDict.TryGetValue(newOpt.Name ?? string.Empty, out var existingOpt))
            {
                // 保留用户选择的值
                newOpt.CopySelectionFrom(existingOpt);
                newOpt.CopyInputDataFrom(existingOpt);
                newOpt.SelectedCases = existingOpt.SelectedCases == null ? null : new(existingOpt.SelectedCases);
                newOpt.SubOptions = existingOpt.SubOptions == null ? null : CloneSubOptions(existingOpt.SubOptions);
                SetDefaultOptionValue(maaInterface, newOpt);
            }
            return newOpt;
        }).ToList();
    }
}
