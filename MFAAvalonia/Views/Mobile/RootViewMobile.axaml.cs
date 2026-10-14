using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Mobile;

public partial class RootViewMobile : UserControl
{
    public RootViewMobile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        UpdateLanguage();
        LanguageHelper.LanguageChanged += OnLanguageChanged;
        DetachedFromVisualTree += (_, _) => LanguageHelper.LanguageChanged -= OnLanguageChanged;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (!AppRuntime.IsNewInstance)
            return;

        try
        {
            await VersionChecker.CheckOnStartupAsync();
        }
        catch (System.Exception ex)
        {
            LoggerHelper.Error($"启动更新检测失败：{ex.Message}", ex);
        }
    }

    private void OnLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e) => UpdateLanguage();

    private void UpdateLanguage()
    {
        HomeNavText.Text = MobileLocalization.Get("Home");
        TaskNavText.Text = MobileLocalization.Get("Tasks");
        ScheduleNavText.Text = MobileLocalization.Get("Schedule");
        SettingsNavText.Text = MobileLocalization.Get("Settings");
    }

    private void NavigateHome(object? sender, RoutedEventArgs e) => ShowPage(HomePage, HomeNav);
    private void NavigateTasks(object? sender, RoutedEventArgs e) => ShowPage(TaskPage, TaskNav);
    private void NavigateSchedule(object? sender, RoutedEventArgs e) => ShowPage(SchedulePage, ScheduleNav);
    private void NavigateSettings(object? sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNav);

    private void ShowPage(Control page, Button nav)
    {
        HomePage.IsVisible = ReferenceEquals(page, HomePage);
        TaskPage.IsVisible = ReferenceEquals(page, TaskPage);
        SchedulePage.IsVisible = ReferenceEquals(page, SchedulePage);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsPage);

        HomeNav.Classes.Set("selected", ReferenceEquals(nav, HomeNav));
        TaskNav.Classes.Set("selected", ReferenceEquals(nav, TaskNav));
        ScheduleNav.Classes.Set("selected", ReferenceEquals(nav, ScheduleNav));
        SettingsNav.Classes.Set("selected", ReferenceEquals(nav, SettingsNav));
    }
}
