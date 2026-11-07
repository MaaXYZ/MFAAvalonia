using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MFAAvalonia.Controls;

public enum BackgroundPlacement
{
    Right,
    Left,
    Top,
    Bottom
}

public enum ScenicImageMode
{
    SideBlend,
    CenteredBlend,
    Blurred
}

/// <summary>
/// A complete image feathered into a blurred copy of itself. This decorative
/// layer does not participate in input, desired sizing, or theme color blending.
/// </summary>
public sealed class ScenicBackground : Panel
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ScenicBackground, IImage?>(nameof(Source));

    public static readonly StyledProperty<BackgroundPlacement> PlacementProperty =
        AvaloniaProperty.Register<ScenicBackground, BackgroundPlacement>(nameof(Placement), BackgroundPlacement.Right);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<ScenicBackground, double>(nameof(Brightness), 0.2);

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<ScenicBackground, double>(nameof(BlurRadius), 32);

    public static readonly StyledProperty<ScenicImageMode> ImageModeProperty =
        AvaloniaProperty.Register<ScenicBackground, ScenicImageMode>(nameof(ImageMode), ScenicImageMode.SideBlend);

    public static readonly StyledProperty<bool> LightModeProperty =
        AvaloniaProperty.Register<ScenicBackground, bool>(nameof(LightMode));

    private readonly Border _backing = new() { Background = Brushes.Black };
    private readonly BlurEffect _blur = new() { Radius = 32 };
    private readonly Image _ambient = new() { Stretch = Stretch.UniformToFill };
    private readonly Image _foreground = new() { Stretch = Stretch.Uniform };
    private readonly Border _dimming = new() { Background = new SolidColorBrush(Color.Parse("#0B1623")) };

    public ScenicBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _ambient.Effect = _blur;
        Children.AddRange(new Control[] { _backing, _ambient, _foreground, _dimming });
        UpdateLayers();
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public BackgroundPlacement Placement
    {
        get => GetValue(PlacementProperty);
        set => SetValue(PlacementProperty, value);
    }

    public double Brightness
    {
        get => GetValue(BrightnessProperty);
        set => SetValue(BrightnessProperty, value);
    }

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public ScenicImageMode ImageMode
    {
        get => GetValue(ImageModeProperty);
        set => SetValue(ImageModeProperty, value);
    }

    public bool LightMode
    {
        get => GetValue(LightModeProperty);
        set => SetValue(LightModeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == PlacementProperty
            || change.Property == BrightnessProperty || change.Property == BlurRadiusProperty
            || change.Property == ImageModeProperty || change.Property == LightModeProperty)
        {
            UpdateLayers();
            InvalidateMeasure();
        }
    }

    private void UpdateLayers()
    {
        _ambient.Source = Source;
        _foreground.Source = Source;
        foreach (var child in Children)
            child.IsVisible = Source != null;
        _foreground.IsVisible = Source != null && ImageMode != ScenicImageMode.Blurred;

        _blur.Radius = double.IsFinite(BlurRadius) ? Math.Clamp(BlurRadius, 0, 60) : 32;
        _dimming.Opacity = 1 - (double.IsFinite(Brightness) ? Math.Clamp(Brightness, 0, 1) : 0.2);
        // Light appearance fades the scene into a fixed pale surface; it must
        // not darken the picture beneath light cards. Theme accent colors and
        // animated window shaders do not participate in either composition.
        _dimming.Background = new SolidColorBrush(Color.Parse(LightMode ? "#F0F4F8" : "#0B1623"));
        _backing.Background = LightMode ? _dimming.Background : Brushes.Black;

        UpdateMask(false);
    }

    private void UpdateMask(bool verticalCenterFade)
    {
        if (ImageMode == ScenicImageMode.CenteredBlend)
        {
            _foreground.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = verticalCenterFade
                    ? new RelativePoint(0, 1, RelativeUnit.Relative)
                    : new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new(Colors.Transparent, 0), new(Colors.Black, .12),
                    new(Colors.Black, .88), new(Colors.Transparent, 1)
                }
            };
            return;
        }
        var (start, end) = Placement switch
        {
            BackgroundPlacement.Left => (new RelativePoint(1, .5, RelativeUnit.Relative), new RelativePoint(0, .5, RelativeUnit.Relative)),
            BackgroundPlacement.Top => (new RelativePoint(.5, 1, RelativeUnit.Relative), new RelativePoint(.5, 0, RelativeUnit.Relative)),
            BackgroundPlacement.Bottom => (new RelativePoint(.5, 0, RelativeUnit.Relative), new RelativePoint(.5, 1, RelativeUnit.Relative)),
            _ => (new RelativePoint(0, .5, RelativeUnit.Relative), new RelativePoint(1, .5, RelativeUnit.Relative))
        };
        _foreground.OpacityMask = new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops = new GradientStops
            {
                new(Colors.Transparent, 0),
                new(Color.FromArgb(153, 0, 0, 0), .2),
                new(Colors.Black, .4),
                new(Colors.Black, .95),
                new(Colors.Transparent, 1)
            }
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        // A wallpaper must not impose its intrinsic bitmap dimensions on the window.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var viewport = new Rect(finalSize);
        _backing.Arrange(viewport);
        _dimming.Arrange(viewport);

        // Overscan the blurred layer so filtering never exposes an empty edge.
        var overscan = _blur.Radius * 3;
        _ambient.Arrange(new Rect(-overscan, -overscan,
            finalSize.Width + overscan * 2, finalSize.Height + overscan * 2));

        var imageSize = Source?.Size ?? default;
        if (imageSize.Width > 0 && imageSize.Height > 0
            && double.IsFinite(imageSize.Width) && double.IsFinite(imageSize.Height))
        {
            var scale = Math.Min(finalSize.Width / imageSize.Width, finalSize.Height / imageSize.Height);
            var width = imageSize.Width * scale;
            var height = imageSize.Height * scale;
            var x = ImageMode == ScenicImageMode.CenteredBlend ? (finalSize.Width - width) / 2 : Placement switch
            {
                BackgroundPlacement.Left => 0,
                BackgroundPlacement.Right => finalSize.Width - width,
                _ => (finalSize.Width - width) / 2
            };
            var y = ImageMode == ScenicImageMode.CenteredBlend ? (finalSize.Height - height) / 2 : Placement switch
            {
                BackgroundPlacement.Top => 0,
                BackgroundPlacement.Bottom => finalSize.Height - height,
                _ => (finalSize.Height - height) / 2
            };
            UpdateMask(height < finalSize.Height - .01);
            _foreground.Arrange(new Rect(x, y, width, height));
        }
        else
        {
            _foreground.Arrange(default);
        }

        return finalSize;
    }
}
