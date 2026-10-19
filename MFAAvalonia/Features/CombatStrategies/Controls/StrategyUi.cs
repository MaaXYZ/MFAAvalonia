using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

public sealed record EditorChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

internal static class StrategyUi
{
    public static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        FontSize = heading ? 15 : 13, FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal
    };

    public static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => action();
        return button;
    }

    public static CheckBox Check(string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        box.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) changed(box.IsChecked == true); };
        return box;
    }

    public static ComboBox Choice<T>(IEnumerable<EditorChoice<T>> source, T selected, Action<T> changed, double width = 160)
    {
        var options = source.ToList();
        var combo = new ComboBox
        {
            ItemsSource = options, MinWidth = width, MaxWidth = 340,
            SelectedItem = options.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, selected)),
            VerticalAlignment = VerticalAlignment.Center
        };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is EditorChoice<T> item) changed(item.Value); };
        return combo;
    }

    public static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in controls)
        {
            control.Margin = new Thickness(0, 3, 10, 3);
            row.Children.Add(control);
        }
        return row;
    }

    public static StackPanel Column(params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    public static TextBlock Section(string title)
    {
        var text = Text(title, true);
        text.Margin = new Thickness(0, 12, 0, 2);
        return text;
    }

    public static Border Card(Control content, string className = "strategy-card")
    {
        var border = new Border { Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(14) };
        border.Classes.Add(className);
        return border;
    }

    public static readonly EditorChoice<ConditionMatch>[] Matches =
        [new(ConditionMatch.All, "全部满足"), new(ConditionMatch.Any, "任一满足")];

    public static readonly EditorChoice<GhoulColor>[] Colors =
        [new(GhoulColor.Red, "红色"), new(GhoulColor.Blue, "蓝色"), new(GhoulColor.Green, "绿色"), new(GhoulColor.Yellow, "黄色")];

    public static string ColorName(GhoulColor color) => Colors.First(x => x.Value == color).Label;

    public static Border DragHandle()
    {
        var handle = new Border
        {
            Child = new TextBlock { Text = "⠿", FontSize = 22, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(5), Focusable = true
        };
        // 光标由平台创建，挂载到实际界面时再获取。
        handle.AttachedToVisualTree += (_, _) => handle.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeAll);
        ToolTip.SetTip(handle, "拖动排序；聚焦后可用上下方向键移动");
        return handle;
    }
}
