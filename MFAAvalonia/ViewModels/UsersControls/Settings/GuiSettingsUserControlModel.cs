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
    [ObservableProperty] private string? _backgroundImagePath =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImagePath, string.Empty);

    [ObservableProperty] private Bitmap? _backgroundImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindowBackgroundVisible), nameof(IsContentBackgroundVisible))]
    private bool _backgroundImageExtendToTitleBar =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImageExtendToTitleBar, false);

    public bool IsWindowBackgroundVisible => HasBackgroundImage && BackgroundImageExtendToTitleBar;
    public bool IsContentBackgroundVisible => HasBackgroundImage && !BackgroundImageExtendToTitleBar;
    public bool CanExtendBackgroundToTitleBar => HasBackgroundImage && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageStrength))]
    private double _backgroundImageOpacity =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImageOpacity, 0.2);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageStrength))]
    private double _backgroundImageLightOpacity =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImageLightOpacity, 0.16, double.NaN);

    private ScenicImageMode _lightImageMode = ReadImageMode(ConfigurationKeys.BackgroundImageLightMode, ScenicImageMode.Blurred);
    private ScenicImageMode _darkImageMode = ReadImageMode(ConfigurationKeys.BackgroundImageDarkMode, ScenicImageMode.SideBlend);

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
            if (value == BackgroundImageMode) return;
            if (IsLightBackground) _lightImageMode = value;
            else _darkImageMode = value;
            HandlePropertyChanged(IsLightBackground ? ConfigurationKeys.BackgroundImageLightMode
                : ConfigurationKeys.BackgroundImageDarkMode, value.ToString());
            OnPropertyChanged(nameof(BackgroundImageMode));
            OnPropertyChanged(nameof(IsBackgroundPlacementEnabled));
            OnPropertyChanged(nameof(BackgroundImagePlacementHint));
        }
    }

    private static ScenicImageMode ReadImageMode(string key, ScenicImageMode fallback)
    {
        var value = ConfigurationManager.Current.GetValue(key, fallback.ToString());
        return Enum.TryParse<ScenicImageMode>(value, true, out var mode) && Enum.IsDefined(mode) ? mode : fallback;
    }

    internal void ReloadBackgroundImageModes()
    {
        _lightImageMode = ReadImageMode(ConfigurationKeys.BackgroundImageLightMode, ScenicImageMode.Blurred);
        _darkImageMode = ReadImageMode(ConfigurationKeys.BackgroundImageDarkMode, ScenicImageMode.SideBlend);
        OnPropertyChanged(nameof(BackgroundImageMode));
        OnPropertyChanged(nameof(IsBackgroundPlacementEnabled));
        OnPropertyChanged(nameof(BackgroundImagePlacementHint));
    }

    [ObservableProperty] private BackgroundPlacement _backgroundImagePlacement = ReadBackgroundImagePlacement();

    [ObservableProperty] private double _backgroundImageBlurRadius =
        ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImageBlurRadius, 32.0, double.NaN);

    internal static BackgroundPlacement ReadBackgroundImagePlacement()
    {
        var value = ConfigurationManager.Current.GetValue(ConfigurationKeys.BackgroundImagePlacement, nameof(BackgroundPlacement.Right));
        return Enum.TryParse<BackgroundPlacement>(value, true, out var placement) && Enum.IsDefined(placement)
            ? placement : BackgroundPlacement.Right;
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
        HandlePropertyChanged(ConfigurationKeys.BackgroundImagePath, value ?? string.Empty);
        LoadBackgroundImage();
    }

    partial void OnBackgroundImageOpacityChanged(double value) =>
        HandlePropertyChanged(ConfigurationKeys.BackgroundImageOpacity, value);

    partial void OnBackgroundImageExtendToTitleBarChanged(bool value) =>
        HandlePropertyChanged(ConfigurationKeys.BackgroundImageExtendToTitleBar, value);

    partial void OnBackgroundImageLightOpacityChanged(double value) =>
        HandlePropertyChanged(ConfigurationKeys.BackgroundImageLightOpacity, value);

    partial void OnBackgroundImagePlacementChanged(BackgroundPlacement value) =>
        HandlePropertyChanged(ConfigurationKeys.BackgroundImagePlacement, value.ToString());

    partial void OnBackgroundImageBlurRadiusChanged(double value) =>
        HandlePropertyChanged(ConfigurationKeys.BackgroundImageBlurRadius, value);

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

        // Load background image if path exists
        LoadBackgroundImage();
    }

    private void LoadBackgroundImage()
    {
        Bitmap? image = null;
        try
        {
            if (!string.IsNullOrEmpty(BackgroundImagePath) && File.Exists(BackgroundImagePath))
            {
                image = new Bitmap(BackgroundImagePath);
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
            BackgroundImagePath = files[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        BackgroundImagePath = string.Empty;
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
