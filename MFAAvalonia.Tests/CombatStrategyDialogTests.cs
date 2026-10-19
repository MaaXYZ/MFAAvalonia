using Avalonia.Controls;
using MFAAvalonia.Views.Pages;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Tests;

[Collection("策略界面")]
public sealed class CombatStrategyDialogTests
{
    [Theory]
    [InlineData("删除", true)]
    [InlineData("取消", false)]
    [InlineData(null, false)]
    public async Task DeleteConfirmation_UsesMessageBoxResult(string? action, bool expected)
    {
        var result = await Invoke<bool>("ConfirmDeleteAsync", "测试策略", Presenter(action));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("确定", "新名称")]
    [InlineData("取消", null)]
    [InlineData(null, null)]
    public async Task RenamePrompt_UsesMessageBoxResult(string? action, string? expected)
    {
        Assert.Equal(expected, await Invoke<string?>("RenameAsync", "重命名", "旧名称", Presenter(action, "  新名称  ")));
    }

    private static Func<SukiMessageBoxHost, SukiMessageBoxOptions, Task<object?>> Presenter(string? action, string? name = null) =>
        (host, _) =>
        {
            if (name != null) Assert.IsType<TextBox>(host.Content).Text = name;
            if (action == null) return Task.FromResult<object?>(null);
            var button = host.ActionButtonsSource!.Single(x => Equals(x.Content, action));
            // 弹窗库会原样返回标准结果，自定义标签则返回按钮本身。
            var result = Assert.IsType<SukiMessageBoxResult>(button.Tag);
            return Task.FromResult<object?>(result);
        };

    private static Task<T> Invoke<T>(string name, params object?[] arguments)
    {
        var dialogs = typeof(CombatStrategyView).Assembly.GetType("MFAAvalonia.Features.CombatStrategies.Controls.StrategyDialogs")!;
        var method = dialogs.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task<T>)method.Invoke(null, arguments)!;
    }
}
