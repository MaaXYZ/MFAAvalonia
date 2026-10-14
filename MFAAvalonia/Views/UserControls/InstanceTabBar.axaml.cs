using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lang.Avalonia.MarkupExtensions;
using MFAAvalonia.Controls;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.UserControls;

public partial class InstanceTabBar : UserControl
{
    public InstanceTabBar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        var tabsControl = this.FindControl<InstanceTabsControl>("TabsControl");
        var instanceDropdown = this.FindControl<Popup>("InstanceDropdown");
        var dropdownButton = this.FindControl<Button>("DropdownButton");
        if (tabsControl != null)
        {
            tabsControl.ContainerPrepared += OnContainerPrepared;
            tabsControl.TabOrderChanged += OnTabOrderChanged;

            // 溢出按钮点击 → 打开下拉框
            tabsControl.OverflowButtonClicked += () =>
            {
                if (instanceDropdown != null)
                    instanceDropdown.PlacementTarget = tabsControl.OverflowButton ?? dropdownButton;

                if (DataContext is InstanceTabBarViewModel vm)
                    vm.ToggleDropdownCommand.Execute(null);
            };

            if (dropdownButton != null && instanceDropdown != null)
            {
                dropdownButton.Click += (_, _) =>
                {
                    // 左侧展开按钮点击时，确保下拉框从左侧按钮下方弹出
                    instanceDropdown.PlacementTarget = dropdownButton;
                };
            }

            // 将外部的 TabBarBackground Border 传给 InstanceTabsControl 用于 Clip 计算
            var tabBarBg = this.FindControl<Border>("TabBarBackgroundBorder");
            if (tabBarBg != null)
                tabsControl.SetExternalTabBarBackground(tabBarBg);

            // 模板应用后，将 PART_AddItemButton 设为预设菜单 Popup 的 PlacementTarget
            tabsControl.TemplateApplied += (_, _) =>
            {
                var addBtn = tabsControl.GetTemplateChildren()
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Name == "PART_AddItemButton");
                var popup = this.FindControl<Popup>("PresetMenuPopup");
                if (addBtn != null && popup != null)
                    popup.PlacementTarget = addBtn;
            };
        }
    }

    private void OnTabOrderChanged()
    {
        if (DataContext is InstanceTabBarViewModel vm)
            vm.SaveTabOrder();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is DragTabItem dragTabItem)
        {
            dragTabItem.ContextMenu = CreateTabContextMenu(dragTabItem);
        }
    }

    private ContextMenu CreateTabContextMenu(DragTabItem container)
    {
        var addItem = new MenuItem();
        addItem.Header = "新建标签页";
        addItem.Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
        {
            Icon = FluentIcons.Common.Icon.Add,
            IconSize = FluentIcons.Common.IconSize.Size16,
            IconVariant = FluentIcons.Common.IconVariant.Regular
        };
        addItem.Click += async (_, _) =>
        {
            if (DataContext is InstanceTabBarViewModel vm)
                await vm.AddInstanceCommand.ExecuteAsync(null);
        };

        var copyItem = new MenuItem();
        copyItem.Header = "复制标签页";
        copyItem.Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
        {
            Icon = FluentIcons.Common.Icon.Copy,
            IconSize = FluentIcons.Common.IconSize.Size16,
            IconVariant = FluentIcons.Common.IconVariant.Regular
        };
        copyItem.Click += async (_, _) =>
        {
            if (DataContext is not InstanceTabBarViewModel vm) return;
            if (container.DataContext is not InstanceTabViewModel tab) return;
            await DuplicateInstanceAsync(vm, tab);
        };

        var copyIdItem = new MenuItem
        {
            Header = "CopyInstanceId".ToLocalization(),
            Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
            {
                Icon = FluentIcons.Common.Icon.Clipboard,
                IconSize = FluentIcons.Common.IconSize.Size16,
                IconVariant = FluentIcons.Common.IconVariant.Regular
            }
        };
        copyIdItem.Click += async (_, _) =>
        {
            if (container.DataContext is not InstanceTabViewModel tab) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            await clipboard.SetTextAsync(tab.InstanceId);
            ToastHelper.Info(LangKeys.CopiedToClipboard.ToLocalization());
        };

        var exportItem = new MenuItem
        {
            Header = LangKeys.ExportInstanceConfig.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.Copy)
        };
        var exportClipboardItem = new MenuItem
        {
            Header = LangKeys.ExportToClipboard.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.Clipboard)
        };
        exportClipboardItem.Click += async (_, _) =>
        {
            if (container.DataContext is InstanceTabViewModel tab)
                await ExportInstanceToClipboardAsync(tab);
        };
        var exportFileItem = new MenuItem
        {
            Header = LangKeys.ExportToFile.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.FolderArrowLeft)
        };
        exportFileItem.Click += async (_, _) =>
        {
            if (container.DataContext is InstanceTabViewModel tab)
                await ExportInstanceToFileAsync(tab);
        };
        exportItem.Items.Add(exportClipboardItem);
        exportItem.Items.Add(exportFileItem);

        var importItem = new MenuItem
        {
            Header = LangKeys.ImportInstanceConfig.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.FolderArrowLeft)
        };
        var importClipboardItem = new MenuItem
        {
            Header = LangKeys.ImportFromClipboard.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.Clipboard)
        };
        importClipboardItem.Click += async (_, _) =>
        {
            if (container.DataContext is not InstanceTabViewModel tab) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await ImportInstanceAsync(await clipboard.TryGetTextAsync() ?? string.Empty, tab);
        };
        var importFileItem = new MenuItem
        {
            Header = LangKeys.ImportFromFile.ToLocalization(),
            Icon = CreateMenuIcon(FluentIcons.Common.Icon.FolderArrowLeft)
        };
        importFileItem.Click += async (_, _) =>
        {
            if (container.DataContext is InstanceTabViewModel tab)
                await ImportInstanceFromFileAsync(tab);
        };
        importItem.Items.Add(importClipboardItem);
        importItem.Items.Add(importFileItem);

        var renameItem = new MenuItem();
        renameItem.Header = "重命名";
        renameItem.Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
        {
            Icon = FluentIcons.Common.Icon.Edit,
            IconSize = FluentIcons.Common.IconSize.Size16,
            IconVariant = FluentIcons.Common.IconVariant.Regular
        };
        renameItem.Click += (_, _) =>
        {
            if (DataContext is InstanceTabBarViewModel vm)
            {
                var tab = container.DataContext as InstanceTabViewModel;
                if (tab != null)
                    vm.RenameInstanceCommand.Execute(tab);
            }
        };

        var closeItem = new MenuItem();
        closeItem.Header = "关闭标签页";
        closeItem.Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
        {
            Icon = FluentIcons.Common.Icon.Dismiss,
            IconSize = FluentIcons.Common.IconSize.Size16,
            IconVariant = FluentIcons.Common.IconVariant.Regular
        };
        closeItem.Click += async (_, _) =>
        {
            if (DataContext is not InstanceTabBarViewModel vm) return;
            if (container.DataContext is not InstanceTabViewModel tab) return;
            await vm.CloseInstanceCommand.ExecuteAsync(tab);
        };

        var closeOthersItem = new MenuItem
        {
            Header = "关闭其他标签页",
            Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
            {
                Icon = FluentIcons.Common.Icon.DismissCircle,
                IconSize = FluentIcons.Common.IconSize.Size16,
                IconVariant = FluentIcons.Common.IconVariant.Regular
            }
        };
        closeOthersItem.Click += async (_, _) =>
        {
            if (DataContext is not InstanceTabBarViewModel vm) return;
            if (container.DataContext is not InstanceTabViewModel currentTab) return;

            var toClose = vm.Tabs.Where(t => t != currentTab).ToList();
            foreach (var tab in toClose)
                await vm.CloseInstanceCommand.ExecuteAsync(tab);
        };

        var closeRightItem = new MenuItem
        {
            Header = "关闭右侧标签页",
            Icon = new FluentIcons.Avalonia.Fluent.FluentIcon
            {
                Icon = FluentIcons.Common.Icon.ArrowExit,
                IconSize = FluentIcons.Common.IconSize.Size16,
                IconVariant = FluentIcons.Common.IconVariant.Regular
            }
        };
        closeRightItem.Click += async (_, _) =>
        {
            if (DataContext is not InstanceTabBarViewModel vm) return;
            if (container.DataContext is not InstanceTabViewModel currentTab) return;

            var currentIndex = vm.Tabs.IndexOf(currentTab);
            if (currentIndex < 0) return;

            var toClose = vm.Tabs.Skip(currentIndex + 1).ToList();
            foreach (var tab in toClose)
                await vm.CloseInstanceCommand.ExecuteAsync(tab);
        };

        var menu = new ContextMenu
        {
            Items =
            {
                addItem,
                copyItem,
                copyIdItem,
                exportItem,
                importItem,
                renameItem,
                new Separator(),
                closeItem,
                closeOthersItem,
                closeRightItem
            }
        };

        menu.Opening += (_, _) =>
        {
            if (DataContext is not InstanceTabBarViewModel vm)
            {
                closeOthersItem.IsEnabled = false;
                closeRightItem.IsEnabled = false;
                return;
            }

            if (container.DataContext is not InstanceTabViewModel currentTab)
            {
                closeOthersItem.IsEnabled = false;
                closeRightItem.IsEnabled = false;
                return;
            }

            var currentIndex = vm.Tabs.IndexOf(currentTab);
            var hasRight = currentIndex >= 0 && currentIndex < vm.Tabs.Count - 1;

            closeOthersItem.IsEnabled = vm.Tabs.Count > 1;
            closeRightItem.IsEnabled = hasRight;
        };

        return menu;
    }

    private static FluentIcons.Avalonia.Fluent.FluentIcon CreateMenuIcon(FluentIcons.Common.Icon icon) => new()
    {
        Icon = icon,
        IconSize = FluentIcons.Common.IconSize.Size16,
        IconVariant = FluentIcons.Common.IconVariant.Regular
    };

    private static string GetShareProjectName() =>
        string.IsNullOrWhiteSpace(MaaProcessor.Interface?.Name) ? "MFAAvalonia" : MaaProcessor.Interface.Name;

    private static string BuildInstanceExportText(InstanceTabViewModel tab)
    {
        var vm = tab.TaskQueueViewModel;
        vm.PersistConfigurationState();
        var config = tab.Processor.InstanceConfiguration;
        var payload = new InstanceConfigSharePayload
        {
            ControllerType = vm.CurrentController.ToString(),
            ControllerName = vm.GetCurrentControllerName(),
            ResourceName = vm.CurrentResource,
            Tasks = vm.TaskItemViewModels
                .Where(item => !item.IsResourceOptionItem && item.InterfaceItem != null)
                .Select(item => item.InterfaceItem!)
                .ToList(),
            GlobalOptions = config.GetValue(ConfigurationKeys.GlobalOptionItems, new List<MaaInterface.MaaInterfaceSelectOption>()),
            ControllerOptions = config.GetValue(ConfigurationKeys.ControllerOptionItems,
                new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>()),
            ResourceOptions = config.GetValue(ConfigurationKeys.ResourceOptionItems,
                new Dictionary<string, List<MaaInterface.MaaInterfaceSelectOption>>())
        };
        return InstanceConfigShareService.BuildExportText(GetShareProjectName(), tab.Name, payload);
    }

    private async Task ExportInstanceToClipboardAsync(InstanceTabViewModel tab)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(BuildInstanceExportText(tab));
            ToastHelper.Success(LangKeys.ExportInstanceConfig.ToLocalization(), LangKeys.ExportInstanceConfigSuccess.ToLocalization());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("Failed to export instance configuration to clipboard.", ex);
            ToastHelper.Error(LangKeys.ExportInstanceConfig.ToLocalization(), LangKeys.ExportInstanceConfigFailed.ToLocalization());
        }
    }

    private async Task ExportInstanceToFileAsync(InstanceTabViewModel tab)
    {
        try
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider == null) throw new InvalidOperationException("Storage provider is unavailable.");
            var projectName = GetShareProjectName();
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LangKeys.ExportInstanceConfig.ToLocalization(),
                DefaultExtension = "txt",
                SuggestedFileName = $"{InstanceConfigShareService.SanitizeFileName(projectName)}-{InstanceConfigShareService.SanitizeFileName(tab.Name)}.txt",
                FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
            });
            if (file == null) return;

            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(BuildInstanceExportText(tab));
            ToastHelper.Success(LangKeys.ExportInstanceConfig.ToLocalization(), LangKeys.ExportInstanceConfigSuccess.ToLocalization());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("Failed to export instance configuration to file.", ex);
            ToastHelper.Error(LangKeys.ExportInstanceConfig.ToLocalization(), LangKeys.ExportInstanceConfigFailed.ToLocalization());
        }
    }

    private async Task ImportInstanceFromFileAsync(InstanceTabViewModel? templateTab = null)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LangKeys.ImportInstanceConfig.ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            await ImportInstanceAsync(await reader.ReadToEndAsync(), templateTab);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("Failed to read instance configuration file.", ex);
            ToastHelper.Error(LangKeys.ImportInstanceConfig.ToLocalization(), LangKeys.ImportInstanceConfigInvalid.ToLocalization());
        }
    }

    private async Task ImportInstanceAsync(string rawText, InstanceTabViewModel? templateTab = null)
    {
        string? createdInstanceId = null;
        try
        {
            var result = InstanceConfigShareService.ParseImportText(GetShareProjectName(), rawText);
            var payload = result.Payload;
            var interfaceTasks = MaaProcessor.Interface?.Task ?? [];
            var exactKeys = interfaceTasks.Select(task => (task.Name, task.Entry)).ToHashSet();
            var entries = interfaceTasks.Where(task => !string.IsNullOrWhiteSpace(task.Entry))
                .Select(task => task.Entry!).ToHashSet(StringComparer.Ordinal);
            var filteredTasks = payload.Tasks.Where(task =>
                    exactKeys.Contains((task.Name, task.Entry))
                    || (!string.IsNullOrWhiteSpace(task.Entry) && entries.Contains(task.Entry!))
                    || (!string.IsNullOrWhiteSpace(task.Entry) && ViewModels.UsersControls.Settings.AddTaskDialogViewModel.SpecialActionNames.Contains(task.Entry!)))
                .ToList();

            var currentTasks = interfaceTasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Name) && !string.IsNullOrWhiteSpace(task.Entry))
                .Select(task => $"{task.Name}{TaskLoader.NEW_SEPARATOR}{task.Entry}")
                .ToList();
            currentTasks.AddRange(filteredTasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Name) && !string.IsNullOrWhiteSpace(task.Entry))
                .Select(task => $"{task.Name}{TaskLoader.NEW_SEPARATOR}{task.Entry}"));

            if (DataContext is not InstanceTabBarViewModel vm)
                throw new InvalidOperationException("Instance tab bar is unavailable.");

            templateTab ??= vm.ActiveTab ?? vm.Tabs.LastOrDefault();
            var newId = MaaProcessorManager.CreateInstanceId();
            createdInstanceId = newId;
            templateTab?.TaskQueueViewModel.PersistConfigurationState();
            templateTab?.Processor.InstanceConfiguration.CopyToNewInstance(newId);

            var config = new InstanceConfiguration(newId);
            config.SetValues(new Dictionary<string, object>
            {
                [ConfigurationKeys.CurrentController] = payload.ControllerType ?? MaaControllerTypes.Adb.ToString(),
                [ConfigurationKeys.CurrentControllerName] = payload.ControllerName ?? string.Empty,
                [ConfigurationKeys.Resource] = payload.ResourceName ?? string.Empty,
                [ConfigurationKeys.TaskItems] = filteredTasks,
                [ConfigurationKeys.CurrentTasks] = currentTasks.Distinct().ToList(),
                [ConfigurationKeys.GlobalOptionItems] = payload.GlobalOptions ?? [],
                [ConfigurationKeys.ControllerOptionItems] = payload.ControllerOptions ?? [],
                [ConfigurationKeys.ResourceOptionItems] = payload.ResourceOptions ?? []
            });
            config.RemoveValue(ConfigurationKeys.InstancePresetKey);

            var processor = MaaProcessorManager.Instance.CreateInstance(newId, false);
            var instanceName = string.IsNullOrWhiteSpace(result.InstanceName)
                ? MaaProcessorManager.Instance.GetInstanceName(newId)
                : result.InstanceName;
            MaaProcessorManager.Instance.SetInstanceName(newId, instanceName);
            if (!await Task.Run(() => processor.InitializeData()))
                throw new InvalidOperationException("Failed to initialize the imported instance configuration.");

            LoggerHelper.UserAction(
                "导入实例配置",
                $"new={instanceName} ({newId}), tasks={filteredTasks.Count}",
                operation: "ImportInstanceConfig",
                instanceId: newId,
                instanceName: instanceName);

            vm.ReloadTabs();
            var importedTab = vm.Tabs.FirstOrDefault(item => item.Processor == processor);
            if (importedTab != null)
            {
                importedTab.UpdateName();
                vm.ActiveTab = importedTab;
            }
            vm.IsAddMenuOpen = false;
            createdInstanceId = null;
            ToastHelper.Success(LangKeys.ImportInstanceConfig.ToLocalization(),
                LangKeys.ImportInstanceConfigSuccess.ToLocalizationFormatted(false, filteredTasks.Count.ToString()));
        }
        catch (InstanceConfigImportException ex)
        {
            RollbackImportedInstance(createdInstanceId);
            var message = ex.Error switch
            {
                InstanceConfigImportError.ProjectMismatch => LangKeys.ImportInstanceConfigProjectMismatch,
                InstanceConfigImportError.UnsupportedVersion => LangKeys.ImportInstanceConfigUnsupportedVersion,
                _ => LangKeys.ImportInstanceConfigInvalid
            };
            ToastHelper.Error(LangKeys.ImportInstanceConfig.ToLocalization(), message.ToLocalization());
        }
        catch (Exception ex)
        {
            RollbackImportedInstance(createdInstanceId);
            LoggerHelper.Error("Failed to import instance configuration.", ex);
            ToastHelper.Error(LangKeys.ImportInstanceConfig.ToLocalization(), LangKeys.ImportInstanceConfigInvalid.ToLocalization());
        }
    }

    private static void RollbackImportedInstance(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return;
        if (!MaaProcessorManager.Instance.RemoveInstance(instanceId))
            new InstanceConfiguration(instanceId).DeleteConfigFile();
    }

    private async void OnImportInstanceFromClipboardClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        await ImportInstanceAsync(await clipboard.TryGetTextAsync() ?? string.Empty);
    }

    private async void OnImportInstanceFromFileClick(object? sender, RoutedEventArgs e)
    {
        await ImportInstanceFromFileAsync();
    }

    private static void OnPresetDescriptionTogglePointerPressed(object? sender, PointerPressedEventArgs e)
        => e.Handled = true;

    private static void OnPresetDescriptionTogglePointerReleased(object? sender, PointerReleasedEventArgs e)
        => e.Handled = true;

    private static void OnPresetDescriptionToggleClick(object? sender, RoutedEventArgs e)
        => e.Handled = true;

    private static async Task DuplicateInstanceAsync(InstanceTabBarViewModel vm, InstanceTabViewModel sourceTab)
    {
        var sourceVm = sourceTab.TaskQueueViewModel;
        if (sourceVm != null)
        {
            sourceTab.Processor.InstanceConfiguration.SetValue(
                Configuration.ConfigurationKeys.TaskItems,
                sourceVm.TaskItemViewModels
                    .Where(m => !m.IsResourceOptionItem)
                    .Select(model => model.InterfaceItem)
                    .ToList());
        }

        var newId = MaaProcessorManager.CreateInstanceId();
        sourceTab.Processor.InstanceConfiguration.CopyToNewInstance(newId);
        new InstanceConfiguration(newId).RemoveValue(Configuration.ConfigurationKeys.InstancePresetKey);

        var processor = MaaProcessorManager.Instance.CreateInstance(newId, false);
        await Task.Run(() => processor.InitializeData());

        vm.ReloadTabs();
        var tab = vm.Tabs.FirstOrDefault(t => t.Processor == processor);
        if (tab != null)
            vm.ActiveTab = tab;
    }

    private void OnDropdownItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (sender is Border border && border.DataContext is InstanceTabViewModel tab)
        {
            if (DataContext is InstanceTabBarViewModel viewModel)
            {
                viewModel.SelectInstanceCommand.Execute(tab);
            }
        }
    }

    private void OnDropdownCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is Button btn && btn.DataContext is InstanceTabViewModel tab)
        {
            if (DataContext is InstanceTabBarViewModel vm)
                vm.CloseInstanceCommand.Execute(tab);
        }
    }

    private void OnRecentClosedItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (sender is Border border && border.DataContext is RecentClosedInstanceItem item)
        {
            if (DataContext is InstanceTabBarViewModel viewModel)
            {
                viewModel.ReopenRecentClosedCommand.Execute(item);
            }
        }
    }
}
