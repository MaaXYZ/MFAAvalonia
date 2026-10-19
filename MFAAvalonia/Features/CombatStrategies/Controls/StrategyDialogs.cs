using Avalonia.Controls;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

internal static class StrategyDialogs
{
    private static Button Action(string label, SukiMessageBoxResult result, bool cancel = false)
    {
        var button = SukiMessageBoxButtonsFactory.CreateButton(label, result);
        button.IsCancel = cancel;
        return button;
    }

    public static async Task<UnsavedDecision> UnsavedAsync()
    {
        var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = "当前策略有未保存的修改。选择“是”保存后继续，选择“否”放弃修改并继续。",
            ActionButtonsPreset = SukiMessageBoxButtons.YesNoCancel,
            IconPreset = SukiMessageBoxIcons.Warning
        }, new SukiMessageBoxOptions { Title = "未保存的策略" });
        return result switch
        {
            SukiMessageBoxResult.Yes => UnsavedDecision.Save,
            SukiMessageBoxResult.No => UnsavedDecision.Discard,
            _ => UnsavedDecision.Cancel
        };
    }

    public static Task<string?> RenameAsync(string title, string current) => RenameAsync(title, current, SukiMessageBox.ShowDialog);

    internal static async Task<string?> RenameAsync(string title, string current, Func<SukiMessageBoxHost, SukiMessageBoxOptions, Task<object?>> showDialog)
    {
        var input = new TextBox { Text = current, MinWidth = 300, Watermark = "请输入名称" };
        var result = await showDialog(new SukiMessageBoxHost
        {
            Content = input,
            ActionButtonsSource = [Action("确定", SukiMessageBoxResult.OK), Action("取消", SukiMessageBoxResult.Cancel, true)]
        }, new SukiMessageBoxOptions { Title = title });
        if (result is not SukiMessageBoxResult.OK) return null;
        var name = input.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;
        await ErrorAsync("名称不能为空。");
        return null;
    }

    public static Task<bool> ConfirmDeleteAsync(string name) => ConfirmDeleteAsync(name, SukiMessageBox.ShowDialog);

    internal static async Task<bool> ConfirmDeleteAsync(string name, Func<SukiMessageBoxHost, SukiMessageBoxOptions, Task<object?>> showDialog)
    {
        var result = await showDialog(new SukiMessageBoxHost
        {
            Content = $"确定删除策略“{name}”吗？",
            ActionButtonsSource = [Action("删除", SukiMessageBoxResult.Yes), Action("取消", SukiMessageBoxResult.Cancel, true)]
        }, new SukiMessageBoxOptions { Title = "删除策略" });
        return result is SukiMessageBoxResult.Yes;
    }

    public static async Task ErrorAsync(string message) =>
        await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = message,
            ActionButtonsSource = [Action("关闭", SukiMessageBoxResult.Close, true)]
        }, new SukiMessageBoxOptions { Title = "作战策略" });
}
