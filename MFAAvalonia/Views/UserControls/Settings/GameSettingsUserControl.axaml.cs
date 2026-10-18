using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class GameSettingsUserControl : UserControl
{
    public GameSettingsUserControl()
    {
        DataContext = Instances.GameSettingsUserControlModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Instances.InstanceTabBarViewModel.PropertyChanged += OnInstanceTabBarPropertyChanged;
        RefreshGlobalOptionSettings();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Instances.InstanceTabBarViewModel.PropertyChanged -= OnInstanceTabBarPropertyChanged;
    }

    private void OnInstanceTabBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceTabBarViewModel.ActiveTab))
            DispatcherHelper.PostOnMainThread(RefreshGlobalOptionSettings);
    }

    private void RefreshGlobalOptionSettings()
    {
        LightweightRecognitionSettingsPanel.Children.Clear();
        GlobalOptionSettingsPanel.Children.Clear();

        var taskQueueViewModel = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
        var maaInterface = MaaProcessor.Interface;
        if (taskQueueViewModel == null || maaInterface == null)
        {
            LightweightRecognitionSettingsPanel.IsVisible = false;
            GlobalOptionCard.IsVisible = false;
            return;
        }

        var savedOptions = taskQueueViewModel.Processor.InstanceConfiguration.GetValue(
            ConfigurationKeys.GlobalOptionItems,
            new List<MaaInterface.MaaInterfaceSelectOption>());
        TaskLoader.InitializeGlobalSelectOptions(maaInterface, savedOptions);
        var globalSelectOptions = maaInterface.GlobalSelectOptions ?? new List<MaaInterface.MaaInterfaceSelectOption>();
        void SaveGlobalOptions() => taskQueueViewModel.Processor.InstanceConfiguration.SetValue(
            ConfigurationKeys.GlobalOptionItems,
            globalSelectOptions);

        var lightweightOption = globalSelectOptions.FirstOrDefault(option =>
            option.Name == TaskLoader.LightweightRecognitionOptionName);
        if (maaInterface.Option?.ContainsKey(TaskLoader.LightweightRecognitionOptionName) == true &&
            lightweightOption != null)
        {
            var lightweightResource = CreateOptionResource([lightweightOption]);
            var lightweightDragItem = new DragItemViewModel(lightweightResource)
            {
                OwnerViewModel = taskQueueViewModel,
                IsVisible = true,
            };
            new TaskOptionGenerator(taskQueueViewModel, SaveGlobalOptions)
                .GenerateResourceOptionPanelContent(LightweightRecognitionSettingsPanel, lightweightDragItem);
        }
        LightweightRecognitionSettingsPanel.IsVisible = LightweightRecognitionSettingsPanel.Children.Count > 0;

        var regularGlobalOptions = globalSelectOptions
            .Where(option => option.Name != TaskLoader.LightweightRecognitionOptionName &&
                             maaInterface.GlobalOption?.Contains(option.Name ?? string.Empty) == true)
            .ToList();
        if (regularGlobalOptions.Count == 0)
        {
            GlobalOptionCard.IsVisible = false;
            return;
        }

        var resource = CreateOptionResource(regularGlobalOptions);
        var dragItem = new DragItemViewModel(resource)
        {
            OwnerViewModel = taskQueueViewModel,
            IsVisible = true,
        };

        new TaskOptionGenerator(taskQueueViewModel, SaveGlobalOptions)
            .GenerateResourceOptionPanelContent(GlobalOptionSettingsPanel, dragItem);
        GlobalOptionCard.IsVisible = GlobalOptionSettingsPanel.Children.Count > 0;
    }

    private static MaaInterface.MaaInterfaceResource CreateOptionResource(
        List<MaaInterface.MaaInterfaceSelectOption> selectOptions)
    {
        var resource = new MaaInterface.MaaInterfaceResource
        {
            Name = "__GlobalOption__",
            SelectOptions = selectOptions,
        };
        resource.InitializeDisplayName();
        return resource;
    }
}

