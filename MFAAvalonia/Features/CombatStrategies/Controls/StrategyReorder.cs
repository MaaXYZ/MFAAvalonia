using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Features.CombatStrategies.Controls;

/// <summary>在当前列表内捕获手柄拖动，不占用系统剪贴板。</summary>
internal static class StrategyReorder
{
    public static void Attach<T>(Control handle, Control row, StackPanel container, IList<T> values, T value, Action changed)
    {
        Point start = default;
        bool pressed = false, dragging = false;
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(handle);
            pressed = true;
            handle.Focus();
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            var point = e.GetPosition(handle);
            if (!dragging && Math.Abs(point.Y - start.Y) + Math.Abs(point.X - start.X) < 6) return;
            dragging = true;
            row.Opacity = 0.5;
            var scroll = container.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll != null)
            {
                var y = e.GetPosition(scroll).Y;
                if (y < 35) scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y - 18));
                if (y > scroll.Bounds.Height - 35) scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + 18);
            }
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            var moved = dragging;
            dragging = false;
            row.Opacity = 1;
            var point = e.GetPosition(container);
            e.Pointer.Capture(null);
            if (!moved || values.Count < 2) return;
            var from = values.IndexOf(value);
            Move(from, CalculateDropIndex(container.Children.Select(x => x.Bounds).ToArray(), from, point.Y));
            e.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => { pressed = false; dragging = false; row.Opacity = 1; };
        handle.KeyDown += (_, e) =>
        {
            var from = values.IndexOf(value);
            if (e.Key is Key.Up or Key.Down)
            {
                Move(from, from + (e.Key == Key.Up ? -1 : 1));
                e.Handled = true;
            }
        };

        void Move(int from, int to)
        {
            if (from < 0 || to < 0 || to >= values.Count || from == to) return;
            values.RemoveAt(from);
            values.Insert(to, value);
            changed();
        }
    }

    internal static int CalculateDropIndex(IReadOnlyList<Rect> rows, int from, double y)
    {
        if (rows.Count == 0) return -1;
        var index = rows.Count;
        for (var i = 0; i < rows.Count; i++)
            if (y < rows[i].Center.Y) { index = i; break; }
        // 移除原行后，位于其后的插入位置需要向前修正一格。
        if (from >= 0 && from < index) index--;
        return Math.Clamp(index, 0, rows.Count - 1);
    }
}
