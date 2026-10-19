using MFAAvalonia.Features.CombatStrategies.Controls;
using SukiUI.Controls;
using Avalonia.Controls;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Tests;

public sealed class CombatStrategyNavigationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirtyEditor_RestoresSelectionUntilLeaveDecision(bool allow)
    {
        var menu = new SukiSideMenu();
        var editor = new SukiSideMenuItem { Header = "策略", PageContent = new TextBlock { Text = "策略内容" } };
        var home = new SukiSideMenuItem { Header = "首页", PageContent = new TextBlock { Text = "首页内容" } };
        var content = new SukiTransitioningContentControl();
        typeof(SukiSideMenu).GetField("_contentControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(menu, content);
        menu.Items.Add(editor);
        menu.Items.Add(home);
        menu.SelectedItem = editor;
        var decision = new TaskCompletionSource<bool>();
        using var guard = new CombatStrategyNavigationGuard(menu, editor, () => true, () => decision.Task);
        menu.SelectedItem = home;
        Assert.Same(editor, menu.SelectedItem);
        Assert.Same(editor.PageContent, content.Content);
        decision.SetResult(allow);
        await guard.PendingNavigation;
        Assert.Same(allow ? home : editor, menu.SelectedItem);
        Assert.Same((allow ? home : editor).PageContent, content.Content);
    }
}
