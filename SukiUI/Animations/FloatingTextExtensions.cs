using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SukiUI.Animations;

/// <summary>
/// Adds a floating text feedback bubble to a button. Each click creates a new
/// bubble which moves upward and fades out independently.
/// </summary>
public static class FloatingTextExtensions
{
    private sealed class State
    {
        public HashSet<AdornerEntry> Adorners { get; } = [];
    }

    private sealed record AdornerEntry(AdornerLayer Layer, Control Control);

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<Button, State?>("FloatingTextState", typeof(FloatingTextExtensions));

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsEnabled", typeof(FloatingTextExtensions));

    public static readonly AttachedProperty<object?> TextProperty =
        AvaloniaProperty.RegisterAttached<Button, object?>("Text", typeof(FloatingTextExtensions), "+1");

    public static readonly AttachedProperty<double> DurationProperty =
        AvaloniaProperty.RegisterAttached<Button, double>("Duration", typeof(FloatingTextExtensions), 900d);

    public static readonly AttachedProperty<double> DistanceProperty =
        AvaloniaProperty.RegisterAttached<Button, double>("Distance", typeof(FloatingTextExtensions), 48d);

    public static readonly AttachedProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.RegisterAttached<Button, IBrush?>("Foreground", typeof(FloatingTextExtensions));

    public static readonly AttachedProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.RegisterAttached<Button, IBrush?>("Background", typeof(FloatingTextExtensions));

    static FloatingTextExtensions()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>(
            (button, _) => SetEnabled(button, button.GetValue(IsEnabledProperty)));
    }

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);
    public static object? GetText(Button button) => button.GetValue(TextProperty);
    public static void SetText(Button button, object? value) => button.SetValue(TextProperty, value);
    public static double GetDuration(Button button) => button.GetValue(DurationProperty);
    public static void SetDuration(Button button, double value) => button.SetValue(DurationProperty, value);
    public static double GetDistance(Button button) => button.GetValue(DistanceProperty);
    public static void SetDistance(Button button, double value) => button.SetValue(DistanceProperty, value);
    public static IBrush? GetForeground(Button button) => button.GetValue(ForegroundProperty);
    public static void SetForeground(Button button, IBrush? value) => button.SetValue(ForegroundProperty, value);
    public static IBrush? GetBackground(Button button) => button.GetValue(BackgroundProperty);
    public static void SetBackground(Button button, IBrush? value) => button.SetValue(BackgroundProperty, value);

    private static void SetEnabled(Button button, bool enabled)
    {
        var state = button.GetValue(StateProperty);
        if (enabled && state == null)
        {
            state = new State();
            button.SetValue(StateProperty, state);
            button.Click += OnButtonClick;
            button.DetachedFromVisualTree += OnButtonDetached;
        }
        else if (!enabled && state != null)
        {
            button.Click -= OnButtonClick;
            button.DetachedFromVisualTree -= OnButtonDetached;
            CloseAdorners(state);
            button.ClearValue(StateProperty);
        }
    }

    private static void OnButtonDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button && button.GetValue(StateProperty) is { } state)
            CloseAdorners(state);
    }

    private static async void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            Show(button);
    }

    /// <summary>
    /// Shows the configured floating text without requiring a click. This is
    /// useful when feedback should only appear after an async action succeeds.
    /// </summary>
    public static void Show(Button button) => _ = ShowSafelyAsync(button);

    private static async Task ShowSafelyAsync(Button button)
    {
        try
        {
            await ShowAsync(button);
        }
        catch
        {
            // Visual feedback must never surface an unobserved task exception.
        }
    }

    private static async Task ShowAsync(Button button)
    {
        var state = button.GetValue(StateProperty);
        if (state == null)
        {
            state = new State();
            button.SetValue(StateProperty, state);
        }

        if (!button.IsAttachedToVisualTree())
            return;

        var adornerLayer = AdornerLayer.GetAdornerLayer(button);
        if (adornerLayer == null)
            return;

        var text = GetText(button);
        if (text == null)
            return;

        var displayText = text.ToString() ?? string.Empty;
        if (displayText.Length == 0)
            return;

        var bubble = new Border
        {
            Background = GetBackground(button) ?? new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 5),
            Width = CalculateBubbleWidth(displayText),
            Opacity = 1,
            RenderTransform = new TranslateTransform(),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = displayText,
                Foreground = GetForeground(button) ?? Brushes.White,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        var host = new Grid
        {
            IsHitTestVisible = false,
            ClipToBounds = false,
            Children = { bubble }
        };
        bubble.HorizontalAlignment = HorizontalAlignment.Center;
        bubble.VerticalAlignment = VerticalAlignment.Top;
        ((TranslateTransform)bubble.RenderTransform!).Y = -4;

        AdornerLayer.SetAdornedElement(host, button);
        AdornerLayer.SetIsClipEnabled(host, false);
        var entry = new AdornerEntry(adornerLayer, host);
        state.Adorners.Add(entry);
        adornerLayer.Children.Add(host);

        var duration = TimeSpan.FromMilliseconds(Math.Max(100, GetDuration(button)));
        var distance = Math.Max(0, GetDistance(button));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var translate = (TranslateTransform)bubble.RenderTransform!;

            while (adornerLayer.Children.Contains(host))
            {
                var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                var easedProgress = 1 - Math.Pow(1 - progress, 3);
                translate.Y = -distance * easedProgress;
                bubble.Opacity = progress < 0.55
                    ? 1
                    : 1 - (progress - 0.55) / 0.45;

                if (progress >= 1)
                    break;

                await Task.Delay(16);
            }
        }
        finally
        {
            adornerLayer.Children.Remove(host);
            state.Adorners.Remove(entry);
        }
    }

    private static void CloseAdorners(State state)
    {
        foreach (var entry in state.Adorners)
            entry.Layer.Children.Remove(entry.Control);
        state.Adorners.Clear();
    }

    private static double CalculateBubbleWidth(string text)
    {
        double textWidth = 0;
        foreach (var character in text)
            textWidth += character <= 0x7f ? 7.5 : 14;

        return Math.Clamp(textWidth + 22, 56, 260);
    }
}
