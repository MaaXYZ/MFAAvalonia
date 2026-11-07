using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

internal static class OptionInputPicker
{
    public static void Attach(TextBox textBox, MaaInterface.MaaInterfaceOptionInput input, Func<bool> canEdit)
    {
        if (input.IsPassword || input.EditorType is not ("file" or "directory")) return;

        var button = new Button
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 30,
            Height = 24,
            Padding = new Thickness(0),
            Content = new FluentIcon { Icon = Icon.FolderArrowLeft, IconSize = IconSize.Size16 }
        };
        AutomationProperties.SetAutomationId(button, $"BrowseInput:{input.Name}");
        AutomationProperties.SetName(button, input.DisplayName);
        ToolTip.SetTip(button, LangKeys.Select.ToLocalization());
        button.Click += async (_, _) =>
        {
            if (!canEdit()) return;
            try
            {
                var provider = TopLevel.GetTopLevel(textBox)?.StorageProvider;
                if (provider == null) return;
                var path = await PickPathAsync(provider, input.EditorType);
                if (path != null && canEdit()) textBox.Text = path;
            }
            catch (NotSupportedException)
            {
                // Manual input remains available when native pickers are unsupported.
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"Input picker failed: {ex.GetType().Name}");
            }
        };
        textBox.InnerRightContent = button;
    }

    internal static async Task<string?> PickPathAsync(IStorageProvider provider, string editorType)
    {
        if (editorType == "directory")
        {
            if (!provider.CanPickFolder) return null;
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }

        if (!provider.CanOpen) return null;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
