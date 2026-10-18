using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MFAAvalonia.Helper;

public static class IconHelper
{
    private static readonly Lazy<Bitmap> LazyIcon = new(LoadIconWithFallback);
    private static readonly Lazy<Bitmap> LazyLogo = new(LoadLogoWithFallback);
    public static Bitmap Icon => LazyIcon.Value;
    public static Bitmap Logo => LazyLogo.Value;
    public static WindowIcon WindowIcon => new (Icon);

    private static Bitmap LoadLogoWithFallback()
    {
        try
        {
            var installDirectory = AppPaths.InstallRoot;
            var logoPath = Path.Combine(installDirectory, "Assets", "logo.png");
            if (!File.Exists(logoPath))
                logoPath = Path.Combine(installDirectory, "assets", "logo.png");
            if (File.Exists(logoPath))
            {
                using var fileStream = File.OpenRead(logoPath);
                return new Bitmap(fileStream);
            }

            var uri = new Uri("avares://MFAAvalonia.Core/Assets/logo.png");
            if (AssetLoader.Exists(uri))
            {
                var assets = AssetLoader.Open(uri);
                return new Bitmap(assets);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("加载高清应用 Logo 失败", ex);
        }

        return Icon;
    }

    private static Bitmap LoadIconWithFallback()
    {
        try
        {
            var dataDirectory = AppPaths.DataRoot;
            var installDirectory = AppPaths.InstallRoot;

            // 优先使用应用自带图标
            var iconPath = Path.Combine(installDirectory, "Assets", "logo.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(installDirectory, "assets", "logo.ico");
            if (File.Exists(iconPath))
            {
                using var fileStream = File.OpenRead(iconPath);
                return new Bitmap(fileStream);
            }

            // 尝试从嵌入资源加载
            var uri = new Uri("avares://MFAAvalonia.Core/Assets/logo.ico");
            if (AssetLoader.Exists(uri))
            {
                var assets = AssetLoader.Open(uri);
                return new Bitmap(assets);
            }

            // 仅在应用图标资源不可用时尝试读取 interface.json 图标
            var interfacePath = AppPaths.InterfaceJsonPath;
            if (File.Exists(interfacePath))
            {
                var interfaceIcon = TryGetInterfaceIcon(interfacePath);
                if (!string.IsNullOrWhiteSpace(interfaceIcon))
                {
                    var interfaceIconPath = Path.Combine(dataDirectory, interfaceIcon);
                    if (File.Exists(interfaceIconPath))
                    {
                        using var fileStream = File.OpenRead(interfaceIconPath);
                        return new Bitmap(fileStream);
                    }
                }
            }

            LoggerHelper.Warning("未找到内嵌图标资源");
            return CreateEmptyImage();
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"图标加载失败: {ex}");
            return CreateEmptyImage();
        }
    }

    private static string? TryGetInterfaceIcon(string interfacePath)
    {
        try
        {
            var json = JsonHelper.LoadJson<JObject>(interfacePath, null);
            return json?["icon"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap CreateEmptyImage()
    {
        // 创建 1x1 透明位图
        var size = new PixelSize(1, 1);
        var dpi = new Vector(96, 96);
        var format = PixelFormat.Rgba8888;

        var writeableBitmap = new WriteableBitmap(size, dpi, format);

        using var buffer = writeableBitmap.Lock();

        var pixels = new byte[buffer.Size.Width * buffer.Size.Height * 4];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0; // R
            pixels[i + 1] = 0; // G
            pixels[i + 2] = 0; // B
            pixels[i + 3] = 0; // A
        }

        Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);


        return writeableBitmap;
    }
}
