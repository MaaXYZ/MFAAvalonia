using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.IO;

namespace MFAAvalonia.Views.UserControls;

/// <summary>
/// 图标显示控件，支持图片路径、emoji 和符号
/// 当图标为空时自动隐藏
/// </summary>
public class DisplayIcon : TemplatedControl
{
    /// <summary>
    /// 图标源属性（可以是图片路径、emoji 或符号）
    /// </summary>
    public static readonly StyledProperty<string?> IconSourceProperty =
        AvaloniaProperty.Register<DisplayIcon, string?>(nameof(IconSource));

    /// <summary>
    /// 图标大小属性
    /// </summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<DisplayIcon, double>(nameof(IconSize), 16);

    /// <summary>
    /// 是否为图片类型
    /// </summary>
    public static readonly StyledProperty<bool> IsImageProperty =
        AvaloniaProperty.Register<DisplayIcon, bool>(nameof(IsImage), false);

    /// <summary>
    /// 是否为文本类型（emoji/符号）
    /// </summary>
    public static readonly StyledProperty<bool> IsTextProperty =
        AvaloniaProperty.Register<DisplayIcon, bool>(nameof(IsText), false);

    /// <summary>
    /// 图片源
    /// </summary>
    public static readonly StyledProperty<IImage?> ImageSourceProperty =
        AvaloniaProperty.Register<DisplayIcon, IImage?>(nameof(ImageSource));

    /// <summary>
    /// 文本内容（emoji/符号）
    /// </summary>
    public static readonly StyledProperty<string?> TextContentProperty =
        AvaloniaProperty.Register<DisplayIcon, string?>(nameof(TextContent));

    /// <summary>
    /// 图标源（可以是图片路径、emoji 或符号）
    /// </summary>
    public string? IconSource
    {
        get => GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    /// <summary>
    /// 图标大小
    /// </summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>
    /// 是否为图片类型
    /// </summary>
    public bool IsImage
    {
        get => GetValue(IsImageProperty);
        private set => SetValue(IsImageProperty, value);
    }

    /// <summary>
    /// 是否为文本类型（emoji/符号）
    /// </summary>
    public bool IsText
    {
        get => GetValue(IsTextProperty);
        private set => SetValue(IsTextProperty, value);
    }

    /// <summary>
    /// 图片源
    /// </summary>
    public IImage? ImageSource
    {
        get => GetValue(ImageSourceProperty);
        private set => SetValue(ImageSourceProperty, value);
    }

    /// <summary>
    /// 文本内容（emoji/符号）
    /// </summary>
    public string? TextContent
    {
        get => GetValue(TextContentProperty);
        private set => SetValue(TextContentProperty, value);
    }

    static DisplayIcon()
    {
        IconSourceProperty.Changed.AddClassHandler<DisplayIcon>((x, e) => x.OnIconSourceChanged());
        IconSizeProperty.Changed.AddClassHandler<DisplayIcon>((x, e) => x.OnIconSizeChanged());
    }

    private void OnIconSourceChanged()
    {
        UpdateIconDisplay();
    }

    private void OnIconSizeChanged()
    {
        // 图标大小变化时可能需要重新加载图片
    }

    private void UpdateIconDisplay()
    {
        var source = IconSource;

        // 如果图标源为空，隐藏控件
        if (string.IsNullOrWhiteSpace(source))
        {
            IsVisible = false;
            IsImage = false;
            IsText = false;
            ClearImage();
            TextContent = null;
            return;
        }

        IsVisible = true;

        // 判断是否为图片路径
        if (IsImagePath(source))
        {
            LoadImage(source);
        }
        else
        {
            // 作为文本（emoji 或符号）显示
            DisplayAsText(source);
        }
    }

    /// <summary>
    /// 判断字符串是否为图片路径
    /// </summary>
    private static bool IsImagePath(string source)
    {
        // 检查是否为常见图片扩展名
        var imageExtensions = new[]
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".ico",
            ".webp",
            ".svg"
        };
        var lowerSource = source.ToLowerInvariant();

        foreach (var ext in imageExtensions)
        {
            if (lowerSource.EndsWith(ext)) return true;
        }

        // 检查是否为路径格式（包含路径分隔符）
        if (source.Contains('/') || source.Contains('\\'))
        {
            // 进一步检查是否有图片扩展名
            foreach (var ext in imageExtensions)
            {
                if (lowerSource.EndsWith(ext))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 加载图片
    /// </summary>
    private void LoadImage(string path)
    {
        try
        {
            // 解析路径（支持相对路径）
            var resolvedPath = ResolvePath(path);

            if (File.Exists(resolvedPath))
            {
                var oldImage = ImageSource as Bitmap;
                var bitmap = new Bitmap(resolvedPath);
                ImageSource = bitmap;
                oldImage?.Dispose();
                IsImage = true;
                IsText = false;
                TextContent = null;
            }
            else
            {
                // 文件不存在，尝试作为文本显示
                DisplayAsText(path);
            }
        }
        catch
        {
            // 加载失败，尝试作为文本显示
            DisplayAsText(path);
        }
    }

    /// <summary>
    /// 解析路径（支持相对路径和 {PROJECT_DIR} 占位符）
    /// </summary>
    private static string ResolvePath(string path)
    {
        // 替换 {PROJECT_DIR} 占位符
        if (path.Contains("{PROJECT_DIR}"))
        {
            var projectDir = MFAAvalonia.Extensions.MaaFW.MaaProcessor.ResourceBase;
            path = path.Replace("{PROJECT_DIR}", projectDir ?? string.Empty);
        }

        // 如果是相对路径，基于资源目录解析
        if (!Path.IsPathRooted(path))
        {
            var resourceBase = MFAAvalonia.Extensions.MaaFW.MaaProcessor.ResourceBase;
            if (!string.IsNullOrEmpty(resourceBase))
            {
                // 尝试从资源目录的父目录（interface.json 所在目录）解析
                var interfaceDir = Path.GetDirectoryName(resourceBase);
                if (!string.IsNullOrEmpty(interfaceDir))
                {
                    var fullPath = Path.Combine(interfaceDir, path);
                    if (File.Exists(fullPath))
                        return fullPath;
                }

                // 尝试从资源目录解析
                var resourcePath = Path.Combine(resourceBase, path);
                if (File.Exists(resourcePath))
                    return resourcePath;
            }
        }

        return path;
    }

    /// <summary>
    /// 作为文本显示（emoji 或符号）
    /// </summary>
    private void DisplayAsText(string text)
    {
        TextContent = text;
        IsText = true;
        IsImage = false;
        ClearImage();
    }

    private void ClearImage()
    {
        var oldImage = ImageSource as Bitmap;
        ImageSource = null;
        oldImage?.Dispose();
    }
}
