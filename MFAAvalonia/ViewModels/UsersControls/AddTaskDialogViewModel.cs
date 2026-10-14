using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SukiUI.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class AddTaskDialogViewModel : ViewModelBase, IDisposable
{
    private const string CommonGroupName = "__common__";
    private string _currentSearchKey = string.Empty;

    /// <summary>
    /// 已知的特殊任务 Action 名称集合，用于判断一个任务是否为特殊任务
    /// </summary>
    public static readonly HashSet<string> SpecialActionNames = new()
    {
        "CountdownAction",
        "TimedWaitAction",
        "SystemNotificationAction",
        "CustomProgramAction",
        "KillProcessAction",
        "ComputerOperationAction",
        "WebhookAction",
        "SwitchInstanceAction",
    };

    private ObservableCollection<AddTaskItemViewModel> _items;

    public ObservableCollection<AddTaskItemViewModel> Items
    {
        get => _items;
        set => SetProperty(ref _items, value);
    }

    public List<AddTaskItemViewModel> Sources { get; set; }

    public ObservableCollection<AddTaskItemGroupViewModel> Groups { get; set; } = [];

    public ObservableCollection<AddTaskItemViewModel> SpecialTasks { get; set; }

    public ISukiDialog Dialog { get; set; }

    public bool HasGroups => Groups.Count > 0;

    public bool HasUngroupedItems => Items.Count > 0;

    public AddTaskDialogViewModel(ISukiDialog dialog, ICollection<DragItemViewModel> sources, TaskQueueViewModel taskQueueViewModel)
    {
        Dialog = dialog;
        Sources = sources
            .Where(source => IsTaskAvailableForCurrentContext(source, taskQueueViewModel))
            .Select(s => new AddTaskItemViewModel(s))
            .ToList();
        _items = [];

        SpecialTasks = new ObservableCollection<AddTaskItemViewModel>
        {
            new() { Name = LangKeys.SpecialTask_Countdown.ToLocalization(), IsSpecialTask = true, SpecialActionName = "CountdownAction", SpecialIcon = "⏳" },
            new() { Name = LangKeys.SpecialTask_TimedWait.ToLocalization(), IsSpecialTask = true, SpecialActionName = "TimedWaitAction", SpecialIcon = "⏰" },
            new() { Name = LangKeys.SpecialTask_Toast.ToLocalization(), IsSpecialTask = true, SpecialActionName = "SystemNotificationAction", SpecialIcon = "💬" },
            new() { Name = LangKeys.SpecialTask_CustomProgram.ToLocalization(), IsSpecialTask = true, SpecialActionName = "CustomProgramAction", SpecialIcon = "▶️" },
            new() { Name = LangKeys.SpecialTask_KillProcess.ToLocalization(), IsSpecialTask = true, SpecialActionName = "KillProcessAction", SpecialIcon = "⛔" },
            new() { Name = LangKeys.SpecialTask_ComputerOperation.ToLocalization(), IsSpecialTask = true, SpecialActionName = "ComputerOperationAction", SpecialIcon = "⚡" },
            new() { Name = LangKeys.SpecialTask_Webhook.ToLocalization(), IsSpecialTask = true, SpecialActionName = "WebhookAction", SpecialIcon = "🔔" },
            new() { Name = LangKeys.SpecialTask_SwitchInstance.ToLocalization(), IsSpecialTask = true, SpecialActionName = "SwitchInstanceAction", SpecialIcon = "🔁" },
        };

        LanguageHelper.LanguageChanged += OnLanguageChanged;
        ApplyFilter(string.Empty);
    }

    private static bool IsTaskAvailableForCurrentContext(DragItemViewModel source, TaskQueueViewModel taskQueueViewModel)
    {
        if (source.IsResourceOptionItem)
            return false;

        var interfaceItem = source.InterfaceItem;
        if (interfaceItem == null)
            return false;

        return MaaInterfaceActivationHelper.IsTaskSupportedByResource(
                   MaaProcessor.Interface,
                   interfaceItem,
                   taskQueueViewModel.CurrentResource)
               && MaaInterfaceActivationHelper.IsTaskSupportedByController(
                   MaaProcessor.Interface,
                   interfaceItem,
                   taskQueueViewModel.GetCurrentControllerName());
    }

    [RelayCommand]
    void Add()
    {
        var vm = Instances.InstanceTabBarViewModel.ActiveTab.TaskQueueViewModel;

        // 添加普通任务
        foreach (var item in Sources)
        {
            if (item.AddCount <= 0 || item.Source == null) continue;
            for (int i = 0; i < item.AddCount; i++)
            {
                var output = item.Source.Clone();
                if (output.InterfaceItem.Option != null)
                    output.InterfaceItem.Option.ForEach(option =>
                        TaskLoader.SetDefaultOptionValue(MaaProcessor.Interface, option));
                output.OwnerViewModel = vm;
                vm.RefreshTaskSupportForCurrentContext(output);
                vm.TaskItemViewModels.Add(output);
            }
        }

        // 添加特殊任务
        foreach (var special in SpecialTasks)
        {
            if (special.AddCount <= 0) continue;
            for (int i = 0; i < special.AddCount; i++)
            {
                var task = CreateSpecialTask(special);
                if (task != null)
                {
                    task.OwnerViewModel = vm;
                    vm.RefreshTaskSupportForCurrentContext(task);
                    vm.TaskItemViewModels.Add(task);
                }
            }
        }

        vm.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems,
            vm.TaskItemViewModels.Where(m => !m.IsResourceOptionItem).Select(model => model.InterfaceItem).ToList());

        var totalAdded = Sources.Sum(s => s.AddCount) + SpecialTasks.Sum(s => s.AddCount);
        if (totalAdded > 0)
            ToastHelper.Info(LangKeys.Tip.ToLocalization(),
                LangKeys.TaskAddedToast.ToLocalizationFormatted(false, totalAdded.ToString()));

        Dialog.Dismiss();
    }

    /// <summary>
    /// 获取特殊任务的默认 custom_action_param JSON
    /// </summary>
    public static JObject GetDefaultActionParam(string actionName)
    {
        return actionName switch
        {
            "CountdownAction" => new JObject { ["seconds"] = 60 },
            "TimedWaitAction" => new JObject { ["hour"] = 0, ["minute"] = 0 },
            "SystemNotificationAction" => new JObject { ["title"] = "MFAAvalonia", ["message"] = "" },
            "CustomProgramAction" => new JObject { ["program"] = "", ["arguments"] = "", ["wait_for_exit"] = false },
            "KillProcessAction" => new JObject { ["kill_self_process"] = true, ["process_name"] = "" },
            "ComputerOperationAction" => new JObject { ["operation"] = "shutdown" },
            "WebhookAction" => new JObject { ["url"] = "", ["method"] = "GET", ["body"] = "", ["content_type"] = "application/json" },
            "SwitchInstanceAction" => new JObject { ["target_instance"] = "" },
            _ => new JObject()
        };
    }

    private static DragItemViewModel? CreateSpecialTask(AddTaskItemViewModel special)
    {
        var actionName = special.SpecialActionName;
        var defaultParam = GetDefaultActionParam(actionName);

        var pipelineOverride = new Dictionary<string, JToken>
        {
            [actionName] = new JObject
            {
                ["action"] = "Custom",
                ["custom_action"] = actionName,
                ["custom_action_param"] = defaultParam
            }
        };

        var interfaceTask = new MaaInterface.MaaInterfaceTask
        {
            Name = special.Name,
            Entry = actionName,
            PipelineOverride = pipelineOverride,
            Check = true,
            // 设置 Description 使 DragItemViewModel.IsVisible = true，从而显示设置按钮
            Description = GetSpecialTaskDescription(actionName),
        };

        return new DragItemViewModel(interfaceTask)
        {
            IsChecked = true,
        };
    }

    /// <summary>
    /// 获取特殊任务的详细描述（i18n key，markdown格式）
    /// </summary>
    public static string GetSpecialTaskDescription(string actionName)
    {
        return actionName switch
        {
            "CountdownAction" => LangKeys.SpecialTask_CountdownDesc,
            "TimedWaitAction" => LangKeys.SpecialTask_TimedWaitDesc,
            "SystemNotificationAction" => LangKeys.SpecialTask_ToastDesc,
            "CustomProgramAction" => LangKeys.SpecialTask_CustomProgramDesc,
            "KillProcessAction" => LangKeys.SpecialTask_KillProcessDesc,
            "ComputerOperationAction" => LangKeys.SpecialTask_ComputerOperationDesc,
            "WebhookAction" => LangKeys.SpecialTask_WebhookDesc,
            "SwitchInstanceAction" => LangKeys.SpecialTask_SwitchInstanceDesc,
            _ => LangKeys.SpecialTask
        };
    }

    [RelayCommand]
    void Cancel()
    {
        Dialog.Dismiss();
    }

    public void ApplyFilter(string key)
    {
        _currentSearchKey = key ?? string.Empty;
        var normalizedKey = key?.Trim().ToLowerInvariant() ?? string.Empty;
        var filteredSources = string.IsNullOrWhiteSpace(normalizedKey)
            ? Sources
            : Sources.Where(item => item.Name.ToLowerInvariant().Contains(normalizedKey)).ToList();

        var groups = BuildGroups(filteredSources);
        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);

        Items.Clear();

        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasUngroupedItems));
    }

    private static List<AddTaskItemGroupViewModel> BuildGroups(IEnumerable<AddTaskItemViewModel> items)
    {
        var filteredItems = items.ToList();
        var interfaceGroups = MaaProcessor.Interface?.Group ?? [];
        var groupDefinitions = new Dictionary<string, AddTaskItemGroupViewModel>();

        foreach (var interfaceGroup in interfaceGroups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
        {
            var description = ResolveGroupDescription(interfaceGroup.Description);
            var icon = ResolveGroupIcon(interfaceGroup.Icon);
            groupDefinitions[interfaceGroup.Name!] = new AddTaskItemGroupViewModel
            {
                Name = interfaceGroup.Name!,
                Label = LanguageHelper.GetLocalizedDisplayName(interfaceGroup.Label, interfaceGroup.Name!),
                Description = description,
                HasDescription = !string.IsNullOrWhiteSpace(description),
                Icon = icon,
                HasIcon = !string.IsNullOrWhiteSpace(icon),
                IsExpanded = interfaceGroup.DefaultExpand ?? true,
            };
        }

        foreach (var item in filteredItems.Where(i => i.GroupNames.Count > 0))
        {
            foreach (var groupName in item.GroupNames.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct())
            {
                if (!groupDefinitions.TryGetValue(groupName, out var group))
                {
                    group = new AddTaskItemGroupViewModel
                    {
                        Name = groupName,
                        Label = groupName,
                        IsExpanded = true,
                    };
                    groupDefinitions[groupName] = group;
                }

                group.Items.Add(item);
            }
        }

        var ungroupedItems = filteredItems.Where(i => i.GroupNames.Count == 0).ToList();
        if (ungroupedItems.Count > 0)
        {
            if (!groupDefinitions.TryGetValue(CommonGroupName, out var commonGroup))
            {
                commonGroup = new AddTaskItemGroupViewModel
                {
                    Name = CommonGroupName,
                    Label = LangKeys.CommonSetting.ToLocalization(),
                    IsExpanded = true,
                };
                groupDefinitions[CommonGroupName] = commonGroup;
            }

            foreach (var item in ungroupedItems)
                commonGroup.Items.Add(item);
        }

        var orderedGroups = new List<AddTaskItemGroupViewModel>();
        foreach (var interfaceGroup in interfaceGroups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
        {
            if (groupDefinitions.TryGetValue(interfaceGroup.Name!, out var group) && group.Items.Count > 0)
                orderedGroups.Add(group);
        }

        orderedGroups.AddRange(groupDefinitions.Values
            .Where(g => g.Items.Count > 0 && orderedGroups.All(existing => existing.Name != g.Name))
            .OrderBy(g => g.Label));

        return orderedGroups;
    }

    private static string ResolveGroupDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        try
        {
            return LanguageHelper.GetLocalizedString(description.ResolveContentAsync().Result);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveGroupIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return string.Empty;

        var iconValue = LanguageHelper.GetLocalizedString(icon);
        return MaaInterface.ReplacePlaceholder(iconValue, MaaProcessor.ResourceBase, true) ?? string.Empty;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyFilter(_currentSearchKey);
    }

    public void Dispose()
    {
        LanguageHelper.LanguageChanged -= OnLanguageChanged;
    }
}

