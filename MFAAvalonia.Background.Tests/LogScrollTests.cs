using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions;
using Xunit;

namespace MFAAvalonia.Background.Tests;

public class LogScrollTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringFocusInsideNestedLogViewerPreservesScrollPosition(bool applyFix)
    {
        var nested = new Markdown.Avalonia.Full.MarkdownScrollViewer
        {
            Markdown = "<details><summary>Old collapsible log</summary>Log details</details>"
        };
        var logs = new StackPanel();
        logs.Children.Add(nested);
        var scroll = new ScrollViewer { Content = logs, BringIntoViewOnFocusChange = false };
        ScrollViewerExtensions.SetAutoScroll(scroll, true);
        if (applyFix)
            scroll.Styles.Add(new StyleInclude(new Uri("avares://MFAAvalonia.Background.Tests/"))
            {
                Source = new Uri("avares://MFAAvalonia.Background.Tests/LogScrollStyles.axaml")
            });
        var outside = new Button { Content = "Outside logs" };
        var layout = new DockPanel();
        DockPanel.SetDock(outside, Dock.Top);
        layout.Children.Add(outside);
        layout.Children.Add(scroll);
        var window = new Window { Width = 500, Height = 300, Content = layout };
        window.Show();
        try
        {
            Flush(window);
            var expander = nested.GetVisualDescendants().OfType<Expander>().Single();
            var oldLog = expander.GetVisualDescendants().OfType<ToggleButton>().Single();
            Assert.True(oldLog.Focus());
            Assert.True(outside.Focus());
            for (var i = 0; i < 30; i++)
                logs.Children.Add(new TextBlock { Text = $"New log {i}", Height = 40 });
            Flush(window);
            Assert.True(scroll.Offset.Y > 500);
            var bottom = scroll.Offset;
            Assert.True(oldLog.Focus());
            Flush(window);
            if (!applyFix)
            {
                // The previous fix on the outer viewer alone still jumps to the old log.
                Assert.True(scroll.Offset.Y < bottom.Y - 500);
                return;
            }
            Assert.Equal(bottom, scroll.Offset);

            // Manual history browsing still pauses following new logs.
            scroll.Offset = new Vector(0, 200);
            Flush(window);
            logs.Children.Add(new TextBlock { Text = "While browsing", Height = 40 });
            Flush(window);
            Assert.Equal(200, scroll.Offset.Y);
            Assert.True(outside.Focus());
            Assert.True(oldLog.Focus());
            Flush(window);
            Assert.Equal(200, scroll.Offset.Y);

            scroll.ScrollToEnd();
            Flush(window);
            logs.Children.Add(new TextBlock { Text = "Follow again", Height = 40 });
            Flush(window);
            Assert.Equal(scroll.ScrollBarMaximum.Y, scroll.Offset.Y);

            // Focus suppression must not disable the actual Markdown disclosure control.
            oldLog.IsChecked = true;
            Flush(window);
            Assert.True(expander.IsExpanded);
            oldLog.IsChecked = false;
            Flush(window);
            Assert.False(expander.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Flush(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
