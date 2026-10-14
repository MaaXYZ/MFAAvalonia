using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;
using SukiUI;
using SukiUI.Models;

namespace MFAAvalonia.Views.Mobile;

public partial class MobileSettingsView : UserControl
{
    private readonly SukiTheme _theme = SukiTheme.GetInstance();
    private bool _initializing = true;
    private readonly TimerModel _timerModel = TimerModel.Instance;

    public MobileSettingsView()
    {
        InitializeComponent();

        var baseTheme = ConfigurationManager.Current.GetValue(
            ConfigurationKeys.BaseTheme,
            ThemeVariant.Light,
            new Dictionary<object, ThemeVariant>
            {
                ["Light"] = ThemeVariant.Light,
                ["Dark"] = ThemeVariant.Dark,
            });
        BaseThemeSelector.SelectedIndex = baseTheme == ThemeVariant.Dark ? 1 : 0;
        _theme.ChangeBaseTheme(baseTheme);

        var colorThemes = _theme.ColorThemes.ToList();
        ColorThemeSelector.ItemsSource = CreateColorThemeOptions(colorThemes);
        var configuredColor = ConfigurationManager.Current.GetValue(ConfigurationKeys.ColorTheme, "blue");
        ColorThemeSelector.SelectedItem = ColorThemeSelector.ItemsSource
            ?.Cast<MobileColorThemeOption>()
            .FirstOrDefault(option => option.Theme.DisplayName.Equals(configuredColor, StringComparison.OrdinalIgnoreCase))
            ?? ColorThemeSelector.ItemsSource?.Cast<MobileColorThemeOption>().FirstOrDefault();
        if (ColorThemeSelector.SelectedItem is MobileColorThemeOption colorTheme)
            _theme.ChangeColorTheme(colorTheme.Theme);

        LanguageSelector.ItemsSource = LanguageHelper.SupportedLanguages;
        LanguageSelector.SelectedItem = LanguageHelper.GetLanguage(LanguageHelper.CurrentLanguage);
        RefreshInstances();
        UpdateLocalizedLabels(LanguageHelper.CurrentLanguage);
        LanguageHelper.LanguageChanged += OnGlobalLanguageChanged;
        DetachedFromVisualTree += (_, _) => LanguageHelper.LanguageChanged -= OnGlobalLanguageChanged;
        _initializing = false;
    }

    private void RefreshInstances(string? selectId = null)
    {
        _timerModel.RefreshInstanceList();
        InstanceSelector.ItemsSource = _timerModel.InstanceList;
        var targetId = selectId ?? MaaProcessorManager.Instance.Current.InstanceId;
        InstanceSelector.SelectedItem = _timerModel.InstanceList.FirstOrDefault(x => x.InstanceId == targetId);
        if (InstanceSelector.SelectedItem is TimerModel.InstanceEntry entry)
            InstanceNameEditor.Text = entry.Name;
    }

    private void AddInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var processor = MaaProcessorManager.Instance.CreateInstance(setCurrent: false);
        processor.InitializeData();
        RefreshInstances(processor.InstanceId);
        MobileInstanceCoordinator.TrySwitch(processor.InstanceId);
        InstanceStatus.Text = MobileLocalization.Get("ConfigurationAdded");
    }

    private void OnInstanceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || InstanceSelector.SelectedItem is not TimerModel.InstanceEntry entry)
            return;
        if (!MobileInstanceCoordinator.TrySwitch(entry.InstanceId))
        {
            InstanceStatus.Text = MobileLocalization.Get("StopBeforeSwitch");
            RefreshInstances();
            return;
        }
        InstanceNameEditor.Text = entry.Name;
        InstanceStatus.Text = MobileLocalization.Get("ConfigurationActive");
    }

    private void RenameInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (InstanceSelector.SelectedItem is not TimerModel.InstanceEntry entry)
            return;
        var name = InstanceNameEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;
        MaaProcessorManager.Instance.SetInstanceName(entry.InstanceId, name);
        RefreshInstances(entry.InstanceId);
        MobileInstanceCoordinator.NotifyChanged();
        InstanceStatus.Text = MobileLocalization.Get("ConfigurationRenamed");
    }

    private void DeleteInstance(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (InstanceSelector.SelectedItem is not TimerModel.InstanceEntry entry || _timerModel.InstanceList.Count <= 1)
            return;
        var manager = MaaProcessorManager.Instance;
        if (manager.GetViewModel(entry.InstanceId)?.IsRunning == true)
        {
            InstanceStatus.Text = MobileLocalization.Get("StopBeforeDelete");
            return;
        }
        if (!manager.RemoveInstance(entry.InstanceId))
            return;
        var fallback = manager.GetAllInstanceIdsAndNames().First();
        MobileInstanceCoordinator.TrySwitch(fallback.Id);
        RefreshInstances(fallback.Id);
        InstanceStatus.Text = MobileLocalization.Get("ConfigurationDeleted");
    }

    private void OnBaseThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || BaseThemeSelector.SelectedIndex < 0)
            return;

        var theme = BaseThemeSelector.SelectedIndex == 1 ? ThemeVariant.Dark : ThemeVariant.Light;
        ConfigurationManager.Current.SetValue(ConfigurationKeys.BaseTheme, theme);
        _theme.ChangeBaseTheme(theme);
    }

    private void OnColorThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ColorThemeSelector.SelectedItem is not MobileColorThemeOption option)
            return;

        ConfigurationManager.Current.SetValue(ConfigurationKeys.ColorTheme, option.Theme.DisplayName);
        _theme.ChangeColorTheme(option.Theme);
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageSelector.SelectedItem is not SupportedLanguage language)
            return;

        ConfigurationManager.Current.SetValue(ConfigurationKeys.CurrentLanguage, language.Key);
        LanguageHelper.ChangeLanguage(language);
        UpdateLocalizedLabels(language.Key);
    }

    private void OnGlobalLanguageChanged(object? sender, LanguageHelper.LanguageEventArgs e)
    {
        UpdateLocalizedLabels(e.Value.Key);
    }

    private void UpdateLocalizedLabels(string language)
    {
        PageTitle.Text = MobileLocalization.Get("Settings", language);
        AppearanceTitle.Text = MobileLocalization.Get("Appearance", language);
        BaseThemeLabel.Text = MobileLocalization.Get("ThemeMode", language);
        ColorThemeLabel.Text = MobileLocalization.Get("AccentColor", language);
        LanguageTitle.Text = MobileLocalization.Get("Language", language);
        LanguageLabel.Text = MobileLocalization.Get("DisplayLanguage", language);
        InstanceModeTitle.Text = MobileLocalization.Get("Configurations", language);
        InstanceModeValue.Text = MobileLocalization.Get("SingleActiveConfiguration", language);
        InstanceNameEditor.Watermark = MobileLocalization.Get("ConfigurationName", language);
        VersionTitle.Text = GetDesktopLabel(LangKeys.UpdateSettings, "Version settings", language);
        ExternalNotificationTitle.Text = GetDesktopLabel(
            LangKeys.ExternalNotificationSettings,
            "External notifications",
            language);
        AboutTitle.Text = GetDesktopLabel(LangKeys.About, "About", language);
        VersionBackText.Text = GetDesktopLabel(LangKeys.Back, "Back", language);
        AboutBackText.Text = VersionBackText.Text;
        ExternalNotificationBackText.Text = VersionBackText.Text;
        LightThemeItem.Content = MobileLocalization.Get("Light", language);
        DarkThemeItem.Content = MobileLocalization.Get("Dark", language);

        if (ColorThemeSelector.ItemsSource is IEnumerable<MobileColorThemeOption> currentOptions)
        {
            var selectedTheme = (ColorThemeSelector.SelectedItem as MobileColorThemeOption)?.Theme;
            ColorThemeSelector.ItemsSource = CreateColorThemeOptions(currentOptions.Select(option => option.Theme), language);
            ColorThemeSelector.SelectedItem = ColorThemeSelector.ItemsSource.Cast<MobileColorThemeOption>()
                .FirstOrDefault(option => ReferenceEquals(option.Theme, selectedTheme));
        }
    }

    private static string GetDesktopLabel(string key, string fallback, string language)
    {
        // LangKeys is the source of truth for desktop translations. The mobile-specific
        // dictionary intentionally only contains compact navigation labels.
        try
        {
            return key.ToLocalization();
        }
        catch
        {
            return fallback;
        }
    }

    private void OpenVersionSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsList.IsVisible = false;
        VersionSettingsPage.IsVisible = true;
        AboutPage.IsVisible = false;
        ExternalNotificationPage.IsVisible = false;
    }

    private void OpenExternalNotifications(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsList.IsVisible = false;
        VersionSettingsPage.IsVisible = false;
        AboutPage.IsVisible = false;
        ExternalNotificationPage.IsVisible = true;
    }

    private void OpenAbout(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SettingsList.IsVisible = false;
        VersionSettingsPage.IsVisible = false;
        ExternalNotificationPage.IsVisible = false;
        AboutPage.IsVisible = true;
    }

    private void CloseSubPage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        VersionSettingsPage.IsVisible = false;
        ExternalNotificationPage.IsVisible = false;
        AboutPage.IsVisible = false;
        SettingsList.IsVisible = true;
    }

    private static IReadOnlyList<MobileColorThemeOption> CreateColorThemeOptions(
        IEnumerable<SukiColorTheme> themes,
        string? language = null) => themes
        .Select(theme => new MobileColorThemeOption(theme, MobileLocalization.GetThemeName(theme.DisplayName, language)))
        .ToList();
}

public sealed class MobileColorThemeOption(SukiColorTheme theme, string displayName)
{
    public SukiColorTheme Theme { get; } = theme;
    public string DisplayName { get; } = displayName;
    public IBrush PrimaryBrush => Theme.PrimaryBrush;
    public IBrush AccentBrush => Theme.AccentBrush;
}
