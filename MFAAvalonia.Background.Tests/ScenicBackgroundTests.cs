using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MFAAvalonia.Controls;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MFAAvalonia.Background.Tests.TestAppBuilder))]

namespace MFAAvalonia.Background.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ScenicTestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class ScenicTestApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new SukiUI.SukiTheme { ThemeColor = SukiUI.Enums.SukiColor.Blue });
    }
}

public class ScenicBackgroundTests
{
    [AvaloniaTheory]
    [InlineData(BackgroundPlacement.Right, 1200, 600, 600, 0, 600, 600)]
    [InlineData(BackgroundPlacement.Left, 1200, 600, 0, 0, 600, 600)]
    [InlineData(BackgroundPlacement.Top, 600, 1000, 0, 0, 600, 600)]
    [InlineData(BackgroundPlacement.Bottom, 600, 1000, 0, 400, 600, 600)]
    public void PlacementPreservesTheCompleteImage(BackgroundPlacement placement,
        double width, double height, double x, double y, double imageWidth, double imageHeight)
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(1254, 1254)), Placement = placement };
        Arrange(control, new Size(width, height));
        var foreground = control.Children.OfType<Image>().Last();
        Assert.Equal(new Rect(x, y, imageWidth, imageHeight), foreground.Bounds);
        Assert.Equal(1, foreground.Bounds.Width / foreground.Bounds.Height);
    }

    [AvaloniaFact]
    public void ResizingKeepsLandscapeImageInsideTheViewport()
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(1920, 1080)) };
        foreach (var size in new[] { new Size(1400, 600), new Size(700, 900), new Size(700, 360) })
        {
            Arrange(control, size);
            var bounds = control.Children.OfType<Image>().Last().Bounds;
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0);
            Assert.True(bounds.Right <= size.Width + .001 && bounds.Bottom <= size.Height + .001);
            Assert.Equal(1920.0 / 1080, bounds.Width / bounds.Height, 6);
        }
    }

    [AvaloniaFact]
    public void ChangingDirectionMovesImageAndReversesItsFade()
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(100, 100)) };
        Arrange(control, new Size(1000, 500));
        var foreground = control.Children.OfType<Image>().Last();
        var rightMask = Assert.IsType<LinearGradientBrush>(foreground.OpacityMask);
        var rightStart = rightMask.StartPoint;
        var rightEnd = rightMask.EndPoint;
        control.Placement = BackgroundPlacement.Left;
        Arrange(control, new Size(1000, 500));
        var leftMask = Assert.IsType<LinearGradientBrush>(foreground.OpacityMask);
        Assert.Equal(0, foreground.Bounds.Left);
        Assert.Equal(rightEnd, leftMask.StartPoint);
        Assert.Equal(rightStart, leftMask.EndPoint);
    }

    [AvaloniaFact]
    public void BlurredCopyExtendsPastEveryViewportEdge()
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(200, 300)), BlurRadius = 32 };
        Arrange(control, new Size(1200, 600));
        var ambient = control.Children.OfType<Image>().First();
        Assert.True(ambient.Bounds.Left < 0 && ambient.Bounds.Top < 0);
        Assert.True(ambient.Bounds.Right > 1200 && ambient.Bounds.Bottom > 600);
        Assert.Equal(32, Assert.IsType<BlurEffect>(ambient.Effect).Radius);
    }

    [AvaloniaFact]
    public void ClearingImageRevealsNormalBackgroundAndReleasesReferences()
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(1254, 1254)) };
        control.Source = null;
        Assert.All(control.Children, child => Assert.False(child.IsVisible));
        Assert.All(control.Children.OfType<Image>(), image => Assert.Null(image.Source));
        Assert.False(control.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void WallpaperDoesNotImposeItsBitmapSizeOnTheWindow()
    {
        var control = new ScenicBackground { Source = new TestImage(new Size(8000, 6000)) };
        control.Measure(Size.Infinity);
        Assert.Equal(default, control.DesiredSize);
    }

    [AvaloniaTheory]
    [InlineData(-1, 1)]
    [InlineData(2, 0)]
    [InlineData(.45, .55)]
    public void BrightnessIsBoundedAndUsesAFixedOverlay(double brightness, double opacity)
    {
        var control = new ScenicBackground { Brightness = brightness };
        var dimming = Assert.IsType<Border>(control.Children.Last());
        Assert.Equal(opacity, dimming.Opacity, 6);
        Assert.Equal(Color.Parse("#0B1623"), Assert.IsType<SolidColorBrush>(dimming.Background).Color);
    }

    private static void Arrange(Control control, Size size)
    {
        control.Measure(size);
        control.Arrange(new Rect(size));
    }

    [AvaloniaFact]
    public void SoftBackdropHidesOnlyTheSharpImage()
    {
        var control = new ScenicBackground
        {
            Source = new TestImage(new Size(100, 100)), ImageMode = ScenicImageMode.Blurred
        };
        Assert.True(control.Children.OfType<Image>().First().IsVisible);
        Assert.False(control.Children.OfType<Image>().Last().IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(1200, 600, 300, 0)]
    [InlineData(600, 1000, 0, 200)]
    public void CenteredModeFitsWithoutCropping(double width, double height, double x, double y)
    {
        var control = new ScenicBackground
        {
            Source = new TestImage(new Size(100, 100)), ImageMode = ScenicImageMode.CenteredBlend
        };
        Arrange(control, new Size(width, height));
        Assert.Equal(new Rect(x, y, 600, 600), control.Children.OfType<Image>().Last().Bounds);
    }

    [AvaloniaFact]
    public void LightAppearanceFadesIntoAPaleSurfaceInsteadOfDarkening()
    {
        var control = new ScenicBackground { LightMode = true, Brightness = .16 };
        var overlay = Assert.IsType<Border>(control.Children.Last());
        Assert.Equal(.84, overlay.Opacity, 6);
        Assert.Equal(Color.Parse("#F0F4F8"), Assert.IsType<SolidColorBrush>(overlay.Background).Color);
        control.LightMode = false;
        Assert.Equal(Color.Parse("#0B1623"), Assert.IsType<SolidColorBrush>(overlay.Background).Color);
    }

    private sealed class TestImage(Size size) : IImage
    {
        public Size Size { get; } = size;
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) { }
    }
}
