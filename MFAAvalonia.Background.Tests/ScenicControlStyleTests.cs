using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SukiUI.Controls;
using Xunit;

namespace MFAAvalonia.Background.Tests;

public class ScenicControlStyleTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScenicPanelsPreserveEverySelectionStateAndOtherInteractiveControls(bool light)
    {
        Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var controls = new List<Control>();
        foreach (var enabled in new[] { true, false })
        {
            foreach (bool? state in new bool?[] { false, true, null })
                controls.Add(new CheckBox { Content = $"CheckBox {state?.ToString() ?? "Mixed"}, enabled={enabled}", IsThreeState = true, IsChecked = state, IsEnabled = enabled });
            foreach (var state in new[] { false, true })
                controls.Add(new RadioButton { Content = $"RadioButton {state}, enabled={enabled}", GroupName = Guid.NewGuid().ToString(), IsChecked = state, IsEnabled = enabled });
        }
        controls.Add(new ComboBox { ItemsSource = new[] { "Selected option", "Another option" }, SelectedIndex = 0 });
        controls.Add(new TextBox { Text = "Text input" });
        controls.Add(new ToggleSwitch { Content = "Toggle switch", IsChecked = true });
        controls.Add(new ToggleButton { Content = "Toggle button", IsChecked = true });
        controls.Add(new Slider { Value = 50 });
        var content = new StackPanel { Spacing = 8 };
        foreach (var control in controls) content.Children.Add(control);
        var panel = new GlassCard { Content = content, IsAnimated = false, Padding = new Thickness(24) };
        panel.Classes.Add("ScenicPanel");
        var window = new SukiWindow { Width = 780, Height = 920, BackgroundForceSoftwareRendering = true, Content = panel };
        var panelColor = light ? Color.Parse("#F4F7FC") : Color.Parse("#182435");
        window.Resources["ScenicCardBackground"] = new SolidColorBrush(panelColor);
        window.Resources["ScenicChromeBackground"] = new SolidColorBrush(panelColor);
        window.Styles.Add(new StyleInclude(new Uri("avares://MFAAvalonia.Background.Tests/"))
        {
            Source = new Uri("avares://MFAAvalonia.Background.Tests/ScenicBackgroundStyles.axaml")
        });
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(300);
            using (window.CaptureRenderedFrame()) { }
            foreach (var visual in window.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>())
                visual.Transitions = null;
            var interactiveCards = controls.SelectMany(control => control.GetVisualDescendants().OfType<GlassCard>()).ToArray();
            Assert.True(interactiveCards.Length >= 10, "Real SukiUI control templates must be instantiated");
            var original = interactiveCards.Select(Snapshot).ToArray();
            var originalGlyphs = controls.OfType<CheckBox>().Select(CheckBoxGlyphs).ToArray();
            var primary = SukiUI.SukiTheme.DefaultColorThemes[SukiUI.Enums.SukiColor.Blue].Primary;
            Assert.All(controls.OfType<CheckBox>().Where(check => check.IsChecked != false), check =>
                Assert.Equal(primary, Assert.IsAssignableFrom<ISolidColorBrush>(
                    CardBorder(check.GetVisualDescendants().OfType<GlassCard>().Single()).Background).Color));
            Assert.All(controls.OfType<RadioButton>().Where(radio => radio.IsChecked == true), radio =>
                Assert.Equal(primary, Assert.IsAssignableFrom<ISolidColorBrush>(
                    CardBorder(radio.GetVisualDescendants().OfType<GlassCard>().Single()).Background).Color));
            SaveFrame(window, light, false);

            window.Classes.Add("scenic");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(original, interactiveCards.Select(Snapshot).ToArray());
            var panelBorder = CardBorder(panel);
            Assert.Equal(panelColor, Assert.IsAssignableFrom<ISolidColorBrush>(panelBorder.Background).Color);
            Assert.Equal(.82, panelBorder.Opacity, 6);
            foreach (var check in controls.OfType<CheckBox>())
            {
                var mark = check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "checkMark");
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(mark.Fill).Color);
            }
            // SukiUI also controls glyph visibility through ancestor presenters.
            // Preserve the real template's three-state behavior instead of replacing it.
            Assert.Equal(originalGlyphs, controls.OfType<CheckBox>().Select(CheckBoxGlyphs).ToArray());
            foreach (var radio in controls.OfType<RadioButton>())
            {
                var mark = radio.GetVisualDescendants().OfType<Ellipse>().Single();
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(mark.Fill).Color);
                Assert.Equal(radio.IsChecked == true, mark.IsVisible);
            }
            SaveFrame(window, light, true);

            window.Classes.Remove("scenic");
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(original, interactiveCards.Select(Snapshot).ToArray());
        }
        finally
        {
            window.Close();
        }
    }

    private static Border CardBorder(GlassCard card) => card.GetVisualDescendants().OfType<Border>()
        .First(border => border.Name == "PART_BorderCard");

    private static (string? Background, double BorderOpacity, double ControlOpacity) Snapshot(GlassCard card)
    {
        var border = CardBorder(card);
        return (border.Background?.ToString(), border.Opacity, card.Opacity);
    }

    private static (double Check, double Mixed) CheckBoxGlyphs(CheckBox check) =>
        (check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "checkMark").Opacity,
         check.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "IndetBorder").Opacity);

    private static void SaveFrame(SukiWindow window, bool light, bool scenic)
    {
        var output = Environment.GetEnvironmentVariable("MFAA_STYLE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(System.IO.Path.Combine(output, $"controls-{(light ? "light" : "dark")}-{(scenic ? "scenic" : "normal")}.png"));
    }
}
