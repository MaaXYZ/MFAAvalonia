using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MFAAvalonia.Features.CombatStrategies;
using MFAAvalonia.Views.Pages;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MFAAvalonia.Tests;

[CollectionDefinition("策略界面", DisableParallelization = true)]
public sealed class CombatStrategyUiCollection { }

[Collection("策略界面")]
public sealed class CombatStrategyMenuTests
{
    [Theory]
    [InlineData("删除", false, false)]
    [InlineData("重命名", false, false)]
    [InlineData("删除", true, false)]
    [InlineData("重命名", true, false)]
    [InlineData("删除", false, true)]
    [InlineData("重命名", false, true)]
    [InlineData("删除", true, true)]
    [InlineData("重命名", true, true)]
    public void ContextMenu_ClickPromptsAndChangesTheClickedStrategy(string action, bool requestAlreadyHandled, bool isSelected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "map-menu-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StrategyStore(directory);
            var document = StrategyDefaults.CreateEditable("右键目标");
            store.Save(document);
            var session = new StrategyEditorSession(store);
            if (isSelected) session.Select(document.Id);
            var prompted = false;
            var view = new CombatStrategyView(session,
                (_, name) => { Assert.Equal(document.Name, name); prompted = true; return Task.FromResult<string?>("修改后的名称"); },
                name => { Assert.Equal(document.Name, name); prompted = true; return Task.FromResult(true); });
            var target = session.Catalog.Strategies.Single(x => x.Id == document.Id);
            var list = view.FindControl<ListBox>("StrategyList")!;
            var row = list.ItemTemplate!.Build(target)!;
            row.DataContext = target;
            var menu = row.ContextMenu!;
            list.Template = new FuncControlTemplate<ListBox>((_, _) => new StackPanel { Children = { row } });
            list.ApplyTemplate();
            Assert.Contains(list, row.GetVisualAncestors());
            // 触发真实菜单打开事件，同时阻止创建依赖桌面平台的弹出窗口。
            menu.Opening += (_, e) => e.Cancel = true;
            // 模拟框架先自动打开菜单并将请求标记为已处理。
            using var consumeRequest = Control.ContextRequestedEvent.AddClassHandler<Border>((sender, e) =>
            {
                if (requestAlreadyHandled && ReferenceEquals(sender, row)) e.Handled = true;
            });
            row.GetVisualDescendants().OfType<TextBlock>().Single()
                .RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            var item = menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, action));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(prompted);
            Assert.Equal(isSelected && action != "删除" ? document.Id : "default", session.Current.Id);
            if (action == "删除")
                Assert.DoesNotContain(store.List().Strategies, x => x.Id == document.Id);
            else
            {
                if (isSelected)
                {
                    Assert.Equal("修改后的名称", session.Current.Name);
                    Assert.True(session.HasUnsavedChanges);
                    view.FindControl<Button>("SaveStrategy")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Assert.Equal("修改后的名称", store.Load(document.Id).Name);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
