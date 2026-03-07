using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using Avalonia.Controls;
using MFAAvalonia.Views.Pages;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace MFAAvalonia.ViewModels.Other;

public partial class InstanceTabViewModel : ViewModelBase
{
    public readonly MaaProcessor Processor;

    public TaskQueueViewModel TaskQueueViewModel => MaaProcessorManager.Instance.GetViewModel(InstanceId);

    private Control? _view;

    public Control View
    {
        get
        {
            if (_view == null)
            {
                _view = new TaskQueueView
                {
                    DataContext = TaskQueueViewModel
                };
            }
            return _view;
        }
    }

    public InstanceTabViewModel(MaaProcessor processor)
    {
        Processor = processor;
        InstanceId = processor.InstanceId;
        UpdateName();
        LanguageHelper.LanguageChanged += OnLanguageChanged;

        IsRunning = processor.TaskQueue.Count > 0;
        processor.TaskQueue.CountChanged += OnTaskCountChanged;

        if (TaskQueueViewModel != null)
        {
            TaskQueueViewModel.PropertyChanged += OnTaskQueueViewModelPropertyChanged;
            SubscribeTaskItems(TaskQueueViewModel.TaskItemViewModels);
        }

        RefreshBadges();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        DispatcherHelper.RunOnMainThread(RefreshBadges);
    }

    private void OnTaskCountChanged(object? sender, ObservableQueue<MFATask>.CountChangedEventArgs e)
    {
        DispatcherHelper.RunOnMainThread(() =>
        {
            IsRunning = e.NewValue > 0;
            RefreshBadges();
        });
    }

    private void OnTaskQueueViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskQueueViewModel.CurrentController)
            or nameof(TaskQueueViewModel.CurrentResource)
            or nameof(TaskQueueViewModel.IsConnected))
        {
            DispatcherHelper.RunOnMainThread(RefreshBadges);
        }

        if (e.PropertyName is nameof(TaskQueueViewModel.TaskItemViewModels) && TaskQueueViewModel != null)
        {
            SubscribeTaskItems(TaskQueueViewModel.TaskItemViewModels);
            DispatcherHelper.RunOnMainThread(RefreshBadges);
        }
    }

    private void SubscribeTaskItems(System.Collections.ObjectModel.ObservableCollection<DragItemViewModel> taskItems)
    {
        taskItems.CollectionChanged -= OnTaskItemsCollectionChanged;
        taskItems.CollectionChanged += OnTaskItemsCollectionChanged;

        foreach (var item in taskItems)
        {
            item.PropertyChanged -= OnTaskItemPropertyChanged;
            item.PropertyChanged += OnTaskItemPropertyChanged;
        }
    }

    private void OnTaskItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DragItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnTaskItemPropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (DragItemViewModel item in e.NewItems)
            {
                item.PropertyChanged -= OnTaskItemPropertyChanged;
                item.PropertyChanged += OnTaskItemPropertyChanged;
            }
        }

        DispatcherHelper.RunOnMainThread(RefreshBadges);
    }

    private void OnTaskItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DragItemViewModel.IsCheckedWithNull)
            or nameof(DragItemViewModel.InterfaceItem))
        {
            DispatcherHelper.RunOnMainThread(RefreshBadges);
        }
    }

    private (int enabledCount, int totalCount) GetTaskCounts()
    {
        var taskItems = TaskQueueViewModel?.TaskItemViewModels
            .Where(item => !item.IsResourceOptionItem)
            .Select(item => item.InterfaceItem)
            .Where(item => item != null)
            .ToList();

        if (taskItems is not { Count: > 0 })
        {
            taskItems = Processor.InstanceConfiguration
                .GetValue(Configuration.ConfigurationKeys.TaskItems, new System.Collections.Generic.List<MaaInterface.MaaInterfaceTask>())?
                .Where(item => item != null)
                .ToList();
        }

        var totalCount = taskItems?.Count ?? 0;
        var enabledCount = taskItems?.Count(item => item?.Check != false) ?? 0;
        return (enabledCount, totalCount);
    }

    public string InstanceId { get; }

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private string _taskCountText = string.Empty;

    [ObservableProperty] private string _controllerBadgeText = string.Empty;

    [ObservableProperty] private string _resourceBadgeText = string.Empty;

    [ObservableProperty] private string _statusBadgeText = string.Empty;
    [ObservableProperty] private string _dropdownMetaText = string.Empty;

    [ObservableProperty] private bool _isStatusConnectedVisible;

    [ObservableProperty] private bool _isStatusDisconnectedVisible;

    [ObservableProperty] private bool _isActive;

    private void RefreshBadges()
    {
        var vm = TaskQueueViewModel;
        var (enabledCount, totalCount) = GetTaskCounts();

        IsConnected = vm?.IsConnected ?? false;
        TaskCountText = LangKeys.InstancePresetTaskCountFormat.ToLocalizationFormatted(false, enabledCount.ToString(), totalCount.ToString());

        ControllerBadgeText = vm?.CurrentController switch
        {
            MaaControllerTypes.Adb => LangKeys.Emulator.ToLocalization(),
            MaaControllerTypes.Win32 => LangKeys.Window.ToLocalization(),
            MaaControllerTypes.PlayCover => LangKeys.TabPlayCover.ToLocalization(),
            _ => vm?.CurrentController.ToString() ?? "-"
        };

        ResourceBadgeText = string.IsNullOrWhiteSpace(vm?.CurrentResource)
            ? "-"
            : vm.CurrentResource;

        StatusBadgeText = IsRunning
            ? LangKeys.Running.ToLocalization()
            : IsConnected
                ? LangKeys.Connected.ToLocalization()
                : LangKeys.Unconnected.ToLocalization();

        IsStatusConnectedVisible = !IsRunning && IsConnected;
        IsStatusDisconnectedVisible = !IsRunning && !IsConnected;
        DropdownMetaText = string.Join(" · ", new[] { ControllerBadgeText, ResourceBadgeText }
            .Where(part => !string.IsNullOrWhiteSpace(part) && part != "-"));
    }

    public void UpdateName()
    {
        Name = MaaProcessorManager.Instance.GetInstanceName(InstanceId);
    }

    /// <summary>
    /// 删除当前多开实例（用于设置界面的配置管理）
    /// </summary>
    [RelayCommand]
    private void DeleteConfiguration()
    {
        Instances.InstanceTabBarViewModel.CloseInstanceCommand.Execute(this);
    }
}
