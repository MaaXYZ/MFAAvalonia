using Avalonia.Collections;
using Avalonia.Controls;
using MFAAvalonia.Helper;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System.Threading.Tasks;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

internal static class StrategyDialogs
{
    private static Button Action(string label, object result, bool cancel = false)
    {
        var button = SukiMessageBoxButtonsFactory.CreateButton(label);
        button.Tag = result;
        button.IsCancel = cancel;
        return button;
    }

    public static async Task<UnsavedDecision> UnsavedAsync()
    {
        var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = "当前策略有未保存的修改，是否保存后继续？",
            ActionButtonsSource =
            [
                Action("保存并继续", UnsavedDecision.Save),
                Action("放弃修改并继续", UnsavedDecision.Discard),
                Action("取消", UnsavedDecision.Cancel, true)
            ]
        }, new SukiMessageBoxOptions { Title = "未保存的策略" });
        return result is UnsavedDecision decision ? decision : UnsavedDecision.Cancel;
    }

    public static async Task<string?> RenameAsync(string title, string current)
    {
        var input = new TextBox { Text = current, MinWidth = 300, Watermark = "请输入名称" };
        var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = input,
            ActionButtonsSource = [Action("确定", true), Action("取消", false, true)]
        }, new SukiMessageBoxOptions { Title = title });
        if (result is not true) return null;
        var name = input.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;
        await ErrorAsync("名称不能为空。");
        return null;
    }

    public static async Task<bool> ConfirmDeleteAsync(string name)
    {
        var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = $"确定删除策略“{name}”吗？",
            ActionButtonsSource = [Action("删除", true), Action("取消", false, true)]
        }, new SukiMessageBoxOptions { Title = "删除策略" });
        return result is true;
    }

    public static async Task ErrorAsync(string message) =>
        await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
        {
            Content = message,
            ActionButtonsSource = [Action("关闭", false, true)]
        }, new SukiMessageBoxOptions { Title = "作战策略" });
}
