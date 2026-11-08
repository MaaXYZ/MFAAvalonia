using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Controls;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.Views.UserControls;
using Newtonsoft.Json.Linq;
using SukiUI;
using SukiUI.Dialogs;
using SukiUI.Enums;
using SukiUI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class GuiSettingsUserControlModel : ViewModelBase
{
    private static readonly SukiTheme _theme = SukiTheme.GetInstance();
    public IAvaloniaReadOnlyList<SukiBackgroundStyle> AvailableBackgroundStyles { get; set; }
    public IAvaloniaReadOnlyList<string> Test { get; set; }
    [ObservableProperty] private bool _backgroundAnimations =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundAnimations, false);

    [ObservableProperty] private bool _backgroundTransitions =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundTransitions, false);

    [ObservableProperty] private SukiBackgroundStyle _backgroundStyle =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundStyle, SukiBackgroundStyle.GradientSoft, SukiBackgroundStyle.GradientSoft, new UniversalEnumConverter<SukiBackgroundStyle>());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightBackground), nameof(BackgroundImageStrength), nameof(BackgroundImageMode),
        nameof(IsBackgroundPlacementEnabled), nameof(BackgroundImagePlacementHint),
        nameof(BackgroundImageStrengthTitle), nameof(BackgroundImageStrengthDescription))]
    private ThemeVariant _baseTheme;

    [ObservableProperty] private SukiColorTheme _currentColorTheme;
    [ObservableProperty] private AvaloniaList<ThemeItemViewModel> _themeItems;

    public readonly IList<SukiColorTheme> OtherColorThemes = ConfigurationManager.Current.GetValue(ConfigurationKeys.OtherColorTheme, new List<SukiColorTheme>());

    public IAvaloniaReadOnlyList<SupportedLanguage> SupportedLanguages { get; set; }

    [ObservableProperty] private string _currentLanguage;
    [ObservableProperty] private bool _shouldMinimizeToTray = ConfigurationManager.Current.GetValue(ConfigurationKeys.ShouldMinimizeToTray, false);
    partial void OnShouldMinimizeToTrayChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.ShouldMinimizeToTray, value);

    [ObservableProperty] private bool _enableToastNotification = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableToastNotification, true);
    partial void OnEnableToastNotificationChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.EnableToastNotification, value);

    // Background Image properties
    private bool _applyingBackground;
    private string? _loadedBackgroundPath;
    public bool HasBackgroundUserValues => BackgroundDefaults.Keys.Any(ConfigurationManager.Current.Config.ContainsKey);

    [ObservableProperty] private string? _backgroundImagePath = string.Empty;

    [ObservableProperty] private Bitmap? _backgroundImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowBackgroundVisible), nameof(IsContentBackgroundVisible))]
    private bool _backgroundImageExtendToTitleBar;

    public bool IsWindowBackgroundVisible => HasBackgroundImage && BackgroundImageExtendToTitleBar;
    public bool IsContentBackgroundVisible => HasBackgroundImage && !BackgroundImageExtendToTitleBar;
    public bool CanExtendBackgroundToTitleBar => HasBackgroundImage && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageStrength))]
    private double _backgroundImageOpacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageStrength))]
    private double _backgroundImageLightOpacity;

    private ScenicImageMode _lightImageMode;
    private ScenicImageMode _darkImageMode;

    public bool IsLightBackground => BaseTheme == ThemeVariant.Light;
    public bool IsBackgroundPlacementEnabled => BackgroundImageMode == ScenicImageMode.SideBlend;
    public string BackgroundImagePlacementHint => (IsBackgroundPlacementEnabled
        ? LangKeys.BackgroundImagePlacementDescription : LangKeys.BackgroundImagePlacementDisabledDescription).ToLocalization();
    public string BackgroundImageStrengthTitle => (IsLightBackground
        ? LangKeys.BackgroundImageLightOpacityTitle : LangKeys.BackgroundImageOpacityTitle).ToLocalization();
    public string BackgroundImageStrengthDescription => (IsLightBackground
        ? LangKeys.BackgroundImageLightOpacityDescription : LangKeys.BackgroundImageOpacityDescription).ToLocalization();

    public double BackgroundImageStrength
    {
        get => IsLightBackground ? BackgroundImageLightOpacity : BackgroundImageOpacity;
        set
        {
            if (IsLightBackground) BackgroundImageLightOpacity = value;
            else BackgroundImageOpacity = value;
        }
    }

    public ScenicImageMode BackgroundImageMode
    {
        get => IsLightBackground ? _lightImageMode : _darkImageMode;
        set
        {
            if (_applyingBackground || value == BackgroundImageMode) return;
            SaveBackgroundValue(IsLightBackground ? ConfigurationKeys.BackgroundImageLightMode
                : ConfigurationKeys.BackgroundImageDarkMode, value.ToString());
        }
    }

    public void ApplyBackgroundSettings()
    {
        var settings = ConfigurationManager.Background.Resolve(ConfigurationManager.Current.Config);
        _applyingBackground = true;
        try
        {
            BackgroundImagePath = settings.ImagePath;
            BackgroundImageExtendToTitleBar = settings.ExtendToTitleBar;
            BackgroundImageOpacity = settings.Opacity;
            BackgroundImageLightOpacity = settings.LightOpacity;
            BackgroundImagePlacement = settings.Placement;
            BackgroundImageBlurRadius = settings.BlurRadius;
            _lightImageMode = settings.LightMode;
            _darkImageMode = settings.DarkMode;
            OnPropertyChanged(nameof(BackgroundImageMode));
            OnPropertyChanged(nameof(BackgroundImageStrength));
            OnPropertyChanged(nameof(IsBackgroundPlacementEnabled));
            OnPropertyChanged(nameof(BackgroundImagePlacementHint));
        }
        finally { _applyingBackground = false; }
        LoadBackgroundImage();
        OnPropertyChanged(nameof(HasBackgroundUserValues));
        RestoreDefaultBackgroundCommand.NotifyCanExecuteChanged();
    }

    private void SaveBackgroundValue(string key, object value)
    {
        if (_applyingBackground) return;
        var config = ConfigurationManager.Current;
        config.TryCommitBackground(ConfigurationManager.Background.CreateUserChange(config.Config, key, value));
        ApplyBackgroundSettings();
    }

    [ObservableProperty] private BackgroundPlacement _backgroundImagePlacement;

    [ObservableProperty] private double _backgroundImageBlurRadius;

    [RelayCommand(CanExecute = nameof(HasBackgroundUserValues))]
    private void RestoreDefaultBackground()
    {
        if (Instances.RootViewModel.IsRunning) return;
        var config = ConfigurationManager.Current;
        config.TryCommitBackground(ConfigurationManager.Background.CreateUserChange(config.Config));
        ApplyBackgroundSettings();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowBackgroundVisible), nameof(IsContentBackgroundVisible), nameof(CanExtendBackgroundToTitleBar))]
    private bool _hasBackgroundImage;
// Font Scale properties
    [ObservableProperty] private double _fontScale =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.FontScale, FontService.DefaultScale);

    [ObservableProperty] public string _fontScaleDisplay;

    partial void OnBackgroundImagePathChanged(string? value)
    {
        SaveBackgroundValue(ConfigurationKeys.BackgroundImagePath, value ?? string.Empty);
    }

    partial void OnBackgroundImageOpacityChanged(double value) =>
        SaveBackgroundValue(ConfigurationKeys.BackgroundImageOpacity, value);

    partial void OnBackgroundImageExtendToTitleBarChanged(bool value) =>
        SaveBackgroundValue(ConfigurationKeys.BackgroundImageExtendToTitleBar, value);

    partial void OnBackgroundImageLightOpacityChanged(double value) =>
        SaveBackgroundValue(ConfigurationKeys.BackgroundImageLightOpacity, value);

    partial void OnBackgroundImagePlacementChanged(BackgroundPlacement value) =>
        SaveBackgroundValue(ConfigurationKeys.BackgroundImagePlacement, value.ToString());

    partial void OnBackgroundImageBlurRadiusChanged(double value) =>
        SaveBackgroundValue(ConfigurationKeys.BackgroundImageBlurRadius, value);

    partial void OnFontScaleChanged(double value)
    {
        FontService.Instance.ApplyFontScale(value);
        FontScaleDisplay = LangKeys.FontScaleValue.ToLocalizationFormatted(false, ((int)(FontScale * 100)).ToString());
    }

    protected override void Initialize()
    {
        SupportedLanguages = new AvaloniaList<SupportedLanguage>(LanguageHelper.SupportedLanguages);
        AvailableBackgroundStyles = new AvaloniaList<SukiBackgroundStyle>(Enum.GetValues<SukiBackgroundStyle>());
        foreach (var color in OtherColorThemes)
        {
            if (_theme.ColorThemes.All(theme => theme.DisplayName != color.DisplayName))
                _theme.AddColorTheme(color);
        }

        CurrentColorTheme = FindColorThemeFromConfig();

        BaseTheme =
            ConfigurationManager.Current.GetValue(ConfigurationKeys.BaseTheme, ThemeVariant.Light, new Dictionary<object, ThemeVariant>
            {
                ["Dark"] = ThemeVariant.Dark,
                ["Light"] = ThemeVariant.Light,
            });

        CurrentLanguage = LanguageHelper.CurrentLanguage;
        ThemeItems = new AvaloniaList<ThemeItemViewModel>(
            _theme.ColorThemes.ToList().Select(t => new ThemeItemViewModel(t, this))
        );

        _theme.OnColorThemeChanged += theme =>
        {
            ThemeItems = new AvaloniaList<ThemeItemViewModel>(
                _theme.ColorThemes.ToList().Select(t => new ThemeItemViewModel(t, this))
            );
            CurrentColorTheme = theme;
        };

        LanguageHelper.LanguageChanged += (sender, args) =>
        {
            CurrentLanguage = args.Value.Key;
            OnPropertyChanged(nameof(BackgroundImageStrengthTitle));
            OnPropertyChanged(nameof(BackgroundImageStrengthDescription));
            OnPropertyChanged(nameof(BackgroundImagePlacementHint));
        };

        ApplyBackgroundSettings();
    }

    private void LoadBackgroundImage()
    {
        // Changing strength, placement or theme does not require decoding the image again.
        if (string.Equals(_loadedBackgroundPath, BackgroundImagePath, StringComparison.Ordinal)) return;
        _loadedBackgroundPath = BackgroundImagePath;
        Bitmap? image = null;
        try
        {
            if (!string.IsNullOrEmpty(BackgroundImagePath))
            {
                var path = BackgroundDefaults.ResolveImagePath(BackgroundImagePath, AppPaths.DataRoot);
                if (File.Exists(path)) image = new Bitmap(path);
            }
        }
        catch
        {
            // An unreadable image falls back to the normal window background.
        }

        var oldImage = BackgroundImage;
        BackgroundImage = image;
        HasBackgroundImage = image != null;
        oldImage?.Dispose();
    }

    [RelayCommand]
    private async Task SelectBackgroundImage()
    {
        var topLevel = TopLevel.GetTopLevel(Instances.RootView);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "SelectBackgroundImage".ToLocalization(),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LangKeys.ImageFilter.ToLocalization())
                {
                    Patterns =
                    [
                        "*.png",
                        "*.jpg",
                        "*.jpeg",
                        "*.bmp",
                        "*.gif",
                        "*.webp"
                    ]
                }
            ]
        });

        if (files.Count > 0)
        {
            SaveBackgroundValue(ConfigurationKeys.BackgroundImagePath, files[0].Path.LocalPath);
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        if (Instances.RootViewModel.IsRunning) return;
        SaveBackgroundValue(ConfigurationKeys.BackgroundImagePath, string.Empty);
    }

    [RelayCommand]
    private void CreateCustomTheme()
    {
        Instances.DialogManager.CreateDialog()
            .WithViewModel(dialog => new CustomThemeDialogViewModel(_theme, dialog)).Dismiss().ByClickingBackground()
            .TryShow();
    }

    partial void OnCurrentColorThemeChanged(SukiColorTheme value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.ColorTheme, value.DisplayName);
        _theme.ChangeColorTheme(value);
    }

    /// <summary>
    /// 从配置中读取主题色，通过 DisplayName 匹配已注册的主题对象。
    /// 兼容旧格式（JObject）和新格式（字符串）。
    /// </summary>
    internal static SukiColorTheme FindColorThemeFromConfig()
    {
        var defaultTheme = _theme.ColorThemes.First(t => t.DisplayName.Equals("blue", StringComparison.OrdinalIgnoreCase));

        // 新格式：直接存储 DisplayName 字符串
        var themeName = ConfigurationManager.Current.GetValue(ConfigurationKeys.ColorTheme, string.Empty);
        if (!string.IsNullOrEmpty(themeName))
        {
            var found = _theme.ColorThemes.FirstOrDefault(t => t.DisplayName.Equals(themeName, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }

        // 旧格式：存储了整个 SukiColorTheme 对象（JObject），从中提取 DisplayName
        if (ConfigurationManager.Current.Config.TryGetValue(ConfigurationKeys.ColorTheme, out var data) && data is JObject jObj)
        {
            var displayName = jObj["DisplayName"]?.ToString();
            if (!string.IsNullOrEmpty(displayName))
            {
                var found = _theme.ColorThemes.FirstOrDefault(t => t.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }
        }

        return defaultTheme;
    }

    partial void OnBaseThemeChanged(ThemeVariant value) => HandlePropertyChanged(ConfigurationKeys.BaseTheme, value, t => _theme.ChangeBaseTheme(t));

    partial void OnBackgroundAnimationsChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.BackgroundAnimations, value);

    partial void OnBackgroundTransitionsChanged(bool value) => HandlePropertyChanged(ConfigurationKeys.BackgroundTransitions, value);

    partial void OnBackgroundStyleChanged(SukiBackgroundStyle value) => HandlePropertyChanged(ConfigurationKeys.BackgroundStyle, value.ToString());

    partial void OnCurrentLanguageChanged(string value) => HandlePropertyChanged(ConfigurationKeys.CurrentLanguage, value, LanguageHelper.ChangeLanguage);

    public void AddOtherColor(SukiColorTheme color)
    {
        OtherColorThemes.Add(color);
        ConfigurationManager.Current.SetValue(ConfigurationKeys.OtherColorTheme, OtherColorThemes);
    }

    public void RemoveOtherColor(SukiColorTheme color)
    {
        OtherColorThemes.Remove(color);
        ConfigurationManager.Current.SetValue(ConfigurationKeys.OtherColorTheme, OtherColorThemes);
        ThemeItems = new AvaloniaList<ThemeItemViewModel>(
            _theme.ColorThemes.ToList().Select(t => new ThemeItemViewModel(t, this))
        );
    }
}
