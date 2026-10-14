using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lang.Avalonia;
using MaaFramework.Binding.Buffers;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Other;
using Newtonsoft.Json.Linq;
using SukiUI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace MFAAvalonia.Extensions;

public static class MFAExtensions
{
    public static string GetFallbackCommand()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cmd.exe"
            : "/bin/bash";
    }

    /// <summary>
    /// 解析 Markdown 内容：支持国际化字符串、文件路径、URL 或直接文本
    /// </summary>
    /// <param name="input">输入内容（可能是 $key、文件路径、URL 或直接文本）</param>
    /// <param name="projectDir">项目目录（用于解析相对路径）</param>
    /// <returns>解析后的 Markdown 文本</returns>
    /// <summary>
    /// 可获取内容的文本文件扩展名
    /// </summary>
    private static readonly string[] TextFileExtensions = [".md", ".markdown", ".txt", ".text"];

    public async static Task<string> ResolveContentAsync(this string? input, string? projectDir = null, bool transform = true)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;
        if (string.IsNullOrWhiteSpace(projectDir))
            projectDir = AppPaths.DataRoot;
        try
        {
            // 1. 国际化处理（以$开头）
            var content = transform ? LanguageHelper.GetLocalizedString(input) : input;
            // 2. 判断是否为 URL
            if (Uri.TryCreate(content, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                // 如果是文本文件 URL，获取内容；否则返回超链接格式
                var path = uri.AbsolutePath;
                if (TextFileExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return await content.FetchUrlContentAsync().ConfigureAwait(false);
                }
                // 返回 Markdown 超链接格式
                return $"[{content}]({content})";
            }
            // 3. 判断是否为文件路径
            var filePath = MaaInterface.ReplacePlaceholder(content, projectDir, true);
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                // 使用 ConfigureAwait(false) 避免在 UI 线程上使用 .Result 时死锁
                return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            }

            // Windows accepts Resource/descs/... when the directory on disk is resource/descs/....
            // Android and Linux do not. Resource projects created on Windows commonly rely on
            // that behavior, so resolve each relative segment case-insensitively as a fallback.
            // Exact paths always win above; the fallback is restricted to the project root.
            var compatibleFilePath = ResolveProjectFileIgnoringCase(content, projectDir);
            if (compatibleFilePath != null)
                return await File.ReadAllTextAsync(compatibleFilePath).ConfigureAwait(false);

            // 4. 直接返回文本（可能是 Markdown）
            return content;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"解析 Markdown 内容失败: {input}, 错误: {ex.Message}");
            return string.Empty;
        }
    }

    private static string? ResolveProjectFileIgnoringCase(string content, string projectDir)
    {
        if (!TextFileExtensions.Any(extension =>
                content.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var candidate = MaaInterface.ReplacePlaceholder(content, projectDir, false);
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var root = Path.GetFullPath(projectDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        var relativePath = Path.GetRelativePath(root, fullCandidate);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current))
                return null;

            var exact = Path.Combine(current, segment);
            if (File.Exists(exact) || Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            current = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(
                    Path.GetFileName(entry),
                    segment,
                    StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return null;
        }

        return File.Exists(current) ? current : null;
    }


    public static MaaToken ToMaaToken(
        this Dictionary<string, JToken>? taskModels)
    {
        // 空安全处理：输入为 null 时返回空字典，避免空引用异常
        var nonNullModels = taskModels ?? new Dictionary<string, JToken>();

        // 转换逻辑：遍历集合，将每个 JToken 转为 MaaToken，构建字典
        return MaaToken.FromDictionary(nonNullModels);
    }


    // public static Dictionary<TKey, JToken> MergeJTokens<TKey>(
    //     this IEnumerable<KeyValuePair<TKey, JToken>>? taskModels,
    //     IEnumerable<KeyValuePair<TKey, JToken>>? additionalModels) where TKey : notnull
    // {
    //
    //     if (additionalModels == null)
    //         return taskModels?.ToDictionary() ?? new Dictionary<TKey, JToken>();
    //     return taskModels?
    //             .Concat(additionalModels)
    //             .GroupBy(pair => pair.Key)
    //             .ToDictionary(
    //                 group => group.Key,
    //                 group =>
    //                 {
    //                     var mergedModel = group.First().Value;
    //                     foreach (var taskModel in group.Skip(1))
    //                     {
    //                         mergedModel.Merge(taskModel.Value);
    //                     }
    //                     return mergedModel;
    //                 }
    //             )
    //         ?? new Dictionary<TKey, JToken>();
    // }

    // public static JToken Merge(this JToken? target, JToken? source)
    // {
    //     if (target == null) return source;
    //     if (source == null) return target;
    //
    //     // 确保目标和源都是 JObject 类型
    //     if (target.Type != JTokenType.Object || source.Type != JTokenType.Object)
    //         return target;
    //
    //     var targetObj = (JObject)target;
    //     var sourceObj = (JObject)source;
    //
    //     // 遍历源对象的所有属性
    //     foreach (var property in sourceObj.Properties())
    //     {
    //         string propName = property.Name;
    //         JToken? targetProp = targetObj.Property(propName)?.Value;
    //         JToken sourceProp = property.Value;
    //         if (propName == "attach")
    //         {
    //             // 仅当双方都是对象类型时才进行第一层字段合并
    //             if (targetProp != null && targetProp.Type == JTokenType.Object && sourceProp.Type == JTokenType.Object)
    //             {
    //                 JObject targetAttach = (JObject)targetProp;
    //                 JObject sourceAttach = (JObject)sourceProp;
    //
    //                 // 遍历sourceAttach的所有第一层字段，直接覆盖或添加（不递归）
    //                 foreach (var attachProp in sourceAttach.Properties())
    //                 {
    //                     string attachPropName = attachProp.Name;
    //                     JToken sourceAttachValue = attachProp.Value;
    //
    //                     // 目标存在该字段则直接用源覆盖（不递归），否则添加
    //                     targetAttach[attachPropName] = sourceAttachValue.DeepClone();
    //                 }
    //
    //                 targetObj[propName] = targetAttach;
    //             }
    //             // 目标不存在attach字段时，直接克隆源的attach
    //             else if (targetProp == null)
    //             {
    //                 targetObj[propName] = sourceProp.DeepClone();
    //             }
    //             // 若类型不匹配（如一方不是对象），则用源覆盖目标
    //             else
    //             {
    //                 targetObj[propName] = sourceProp.DeepClone();
    //             }
    //             continue;
    //         }
    //         // if (propName == "attach")
    //         // {
    //         //     // 仅当双方都是对象类型时才进行字段合并
    //         //     if (targetProp != null && targetProp.Type == JTokenType.Object && sourceProp.Type == JTokenType.Object)
    //         //     {
    //         //         JObject targetAttach = (JObject)targetProp;
    //         //         JObject sourceAttach = (JObject)sourceProp;
    //         //
    //         //         // 遍历sourceAttach的所有字段，逐个合并到targetAttach
    //         //         foreach (var attachProp in sourceAttach.Properties())
    //         //         {
    //         //             string attachPropName = attachProp.Name;
    //         //             JToken sourceAttachValue = attachProp.Value;
    //         //
    //         //             // 目标存在该字段则递归合并，否则直接添加
    //         //             if (targetAttach.ContainsKey(attachPropName))
    //         //             {
    //         //                 targetAttach[attachPropName] = Merge(targetAttach[attachPropName], sourceAttachValue);
    //         //             }
    //         //             else
    //         //             {
    //         //                 targetAttach[attachPropName] = sourceAttachValue.DeepClone();
    //         //             }
    //         //         }
    //         //
    //         //         targetObj[propName] = targetAttach;
    //         //     }
    //         //     // 目标不存在attach字段时，直接克隆源的attach
    //         //     else if (targetProp == null)
    //         //     {
    //         //         targetObj[propName] = sourceProp.DeepClone();
    //         //     }
    //         //     // 若类型不匹配（如一方不是对象），则用源覆盖目标
    //         //     else
    //         //     {
    //         //         targetObj[propName] = sourceProp.DeepClone();
    //         //     }
    //         //     continue;
    //         // }
    //         //
    //         // 处理 recognition 相关合并逻辑
    //         if (propName == "recognition")
    //         {
    //             if (targetProp != null && targetProp.Type == JTokenType.Object && sourceProp.Type == JTokenType.Object)
    //             {
    //                 JObject targetRecognition = (JObject)targetProp;
    //                 JObject sourceRecognition = (JObject)sourceProp;
    //
    //                 // 覆盖 type 属性
    //                 if (sourceRecognition.ContainsKey("type"))
    //                 {
    //                     targetRecognition["type"] = sourceRecognition["type"]?.DeepClone() ?? new JValue((string)null);
    //                 }
    //
    //                 // 处理 recognition 内部的 param 属性，递归合并
    //                 if (sourceRecognition.ContainsKey("param") && targetRecognition.ContainsKey("param") && targetRecognition["param"]?.Type == JTokenType.Object && sourceRecognition["param"]?.Type == JTokenType.Object)
    //                 {
    //                     targetRecognition["param"] = Merge(targetRecognition["param"], sourceRecognition["param"]);
    //                 }
    //                 else if (sourceRecognition.ContainsKey("param") && targetRecognition["param"] == null)
    //                 {
    //                     targetRecognition["param"] = sourceRecognition["param"]?.DeepClone();
    //                 }
    //
    //                 targetObj[propName] = targetRecognition;
    //             }
    //             else if (targetProp == null)
    //             {
    //                 targetObj[propName] = sourceProp.DeepClone();
    //             }
    //             continue;
    //         }
    //
    //         // 处理 action 相关合并逻辑
    //         if (propName == "action")
    //         {
    //             if (targetProp != null && targetProp.Type == JTokenType.Object && sourceProp.Type == JTokenType.Object)
    //             {
    //                 JObject targetAction = (JObject)targetProp;
    //                 JObject sourceAction = (JObject)sourceProp;
    //
    //                 // 覆盖 type 属性
    //                 if (sourceAction.ContainsKey("type"))
    //                 {
    //                     targetAction["type"] = sourceAction["type"]?.DeepClone() ?? new JValue((string)null);
    //                 }
    //
    //                 // 处理 action 内部的 param 属性，递归合并
    //                 if (sourceAction.ContainsKey("param") && targetAction.ContainsKey("param") && targetAction["param"]?.Type == JTokenType.Object && sourceAction["param"]?.Type == JTokenType.Object)
    //                 {
    //                     targetAction["param"] = Merge(targetAction["param"], sourceAction["param"]);
    //                 }
    //                 else if (sourceAction.ContainsKey("param") && targetAction["param"] == null)
    //                 {
    //                     targetAction["param"] = sourceAction["param"]?.DeepClone();
    //                 }
    //
    //                 targetObj[propName] = targetAction;
    //             }
    //             else if (targetProp == null)
    //             {
    //                 targetObj[propName] = sourceProp.DeepClone();
    //             }
    //             continue;
    //         }
    //
    //         // 其他普通属性直接替换或添加
    //         targetObj[propName] = sourceProp.DeepClone();
    //     }
    //
    //     return target;
    // }

    public static string FormatWith(this string format, params object[] args)
    {
        return string.Format(format, args);
    }

    public static void AddRange<T>(this ICollection<T>? collection, IEnumerable<T> newItems)
    {
        if (collection == null)
            return;
        if (collection is List<T> objList)
        {
            objList.AddRange(newItems);
        }
        else
        {
            foreach (T newItem in newItems)
                collection.Add(newItem);
        }
    }

    extension(string? key)
    {
        public string ToLocalization()
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            return I18nManager.Instance.GetResource(key) ?? key;
        }

        public string ToLocalizationFormatted(bool transformKey = true, params string[] args)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;

            var localizedKey = key.ToLocalization();
            var processedArgs = transformKey
                ? Array.ConvertAll(args, a => a.ToLocalization() as object)
                : Array.ConvertAll(args, a => a as object);

            try
            {
                return Regex.Unescape(localizedKey.FormatWith(processedArgs));
            }
            catch
            {
                return localizedKey.FormatWith(processedArgs);
            }
        }
    }

    public static bool ContainsKey(this IEnumerable<LocalizationViewModel> settingViewModels, string key)
    {
        return settingViewModels.Any(vm => vm.ResourceKey == key);
    }

    public static bool ShouldSwitchButton(this List<MaaInterface.MaaInterfaceOptionCase>? cases, out int yes, out int no)
    {
        yes = -1;
        no = -1;

        if (cases == null || cases.Count != 2)
            return false;

        var yesItem = cases
            .Select((c, index) => new
            {
                c.Name,
                Index = index
            })
            .Where(x => x.Name?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var noItem = cases
            .Select((c, index) => new
            {
                c.Name,
                Index = index
            })
            .Where(x => x.Name?.Equals("no", StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (yesItem.Count == 0 || noItem.Count == 0)
            return false;

        yes = yesItem[0].Index;
        no = noItem[0].Index;

        return true;
    }

    public static void SafeCancel(this CancellationTokenSource? cts, bool useCancel = true)
    {
        if (cts == null || cts.IsCancellationRequested) return;

        try
        {
            if (useCancel) cts.Cancel();
            cts.Dispose();
        }
        catch (Exception e) { LoggerHelper.Error($"释放取消令牌源失败：原因={e.Message}", e); }
    }

    /// <summary>
    /// 安全移动元素的扩展方法（泛型版本）
    /// </summary>
    /// <param name="targetIndex">目标位置索引应先于实际插入位置</param>
    /// <remarks>当移动方向为向后移动时，实际插入位置会比targetIndex大1[8](@ref)</remarks>
    public static void MoveTo<T>(this IList<T> list, int sourceIndex, int targetIndex) where T : class
    {
        ValidateIndexes(list, sourceIndex, targetIndex);
        if (sourceIndex == targetIndex) return;

        var item = list[sourceIndex];

        list.RemoveAt(sourceIndex);

        list.Insert(targetIndex > sourceIndex ? targetIndex - 1 : targetIndex, item);
    }

    /// <summary>
    /// 安全移动元素的扩展方法（非泛型版本）
    /// </summary>
    public static void MoveTo(this IList list, int sourceIndex, int targetIndex)
    {
        ValidateIndexes(list, sourceIndex, targetIndex);
        if (sourceIndex == targetIndex) return;

        var item = list[sourceIndex];

        list.RemoveAt(sourceIndex);

        list.Insert(targetIndex > sourceIndex ? targetIndex - 1 : targetIndex, item);
    }

    // 扩展方法：范围判断
    public static bool Between(this double value, double min, double max)
        => value >= min && value <= max;
    private static void ValidateIndexes(IList list, int source, int target)
    {
        if (source < 0 || source >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(source), "源索引越界");
        if (target < 0 || target > list.Count)
            throw new ArgumentOutOfRangeException(nameof(target), "目标索引越界");
    }
    private static void ValidateIndexes<T>(IList<T> list, int source, int target)
    {
        if (source < 0 || source >= list.Count)
            throw new ArgumentOutOfRangeException(nameof(source), "源索引越界");
        if (target < 0 || target > list.Count)
            throw new ArgumentOutOfRangeException(nameof(target), "目标索引越界");
    }

    public static string GetName(this VersionChecker.VersionType type)
    {
        return type.ToString().ToLower();
    }

    public static VersionChecker.VersionType ToVersionType(this string version)
    {
        if (version.Contains("alpha", StringComparison.OrdinalIgnoreCase))
            return VersionChecker.VersionType.Alpha;
        if (version.Contains("beta", StringComparison.OrdinalIgnoreCase)) return VersionChecker.VersionType.Beta;
        return VersionChecker.VersionType.Stable;
    }

    public static VersionChecker.VersionType ToVersionType(this int version)
    {
        if (version == 0)
            return VersionChecker.VersionType.Alpha;
        if (version == 1) return VersionChecker.VersionType.Beta;
        return VersionChecker.VersionType.Stable;
    }

    public static Bitmap? ToBitmap(this MaaImageBuffer buffer)
    {
        if (buffer.IsInvalid || buffer.IsEmpty || !buffer.TryGetEncodedData(out Stream encodedDataStream)) return null;

        try
        {

            encodedDataStream.Seek(0, SeekOrigin.Begin);
            return new Bitmap(encodedDataStream);

        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"Bitmap 创建失败: {ex.Message}");
            // 确保异常情况下也释放 Stream
            encodedDataStream?.Dispose();
            return null;
        }
    }
    // public static System.Drawing.Bitmap? ToDrawingBitmap(this Bitmap? bitmap)
    // {
    //     if (bitmap == null)
    //         return null;
    //
    //     using var memory = new MemoryStream();
    //
    //     bitmap.Save(memory);
    //     memory.Position = 0;
    //     
    //     return new System.Drawing.Bitmap(memory);
    // }
    public static Bitmap DrawRectangle(this Bitmap sourceBitmap, MaaRectBuffer rect, IBrush color, double thickness = 1.5)
    {
        return sourceBitmap.DrawRectangle(new Rect(rect.X, rect.Y, rect.Width, rect.Height), color, thickness);
    }

    public static Bitmap DrawRectangle(this Bitmap sourceBitmap, Rect rect, IBrush color, double thickness = 1.5)
    {
        if (sourceBitmap == null)
            throw new ArgumentNullException(nameof(sourceBitmap));

        var bitmapSize = sourceBitmap.Size;
        var pixelSize = sourceBitmap.PixelSize;
        var dpi = sourceBitmap.Dpi;

        var renderBitmap = new RenderTargetBitmap(pixelSize, dpi);

        try
        {
            // 使用 DrawingContext 绘制（同步执行，避免异步导致的资源释放问题）
            using var context = renderBitmap.CreateDrawingContext();

            // 1. 绘制原始图像作为背景
            context.DrawImage(sourceBitmap, new Rect(bitmapSize));

            // 2. 创建抗锯齿画笔
            var pen = new Avalonia.Media.Pen(color, thickness)
            {
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round
            };

            // 3. 绘制矩形边框
            context.DrawRectangle(pen, rect);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"DrawRectangle 绘制失败: {ex.Message}");
        }

        return renderBitmap;
    }


    // public static Bitmap? ToAvaloniaBitmap(this System.Drawing.Bitmap? bitmap)
    // {
    //     if (bitmap == null)
    //         return null;
    //     var bitmapTmp = new System.Drawing.Bitmap(bitmap);
    //     var bd = bitmapTmp.LockBits(new Rectangle(0, 0, bitmapTmp.Width, bitmapTmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    //     var bitmap1 = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul,
    //         bd.Scan0,
    //         new Avalonia.PixelSize(bd.Width, bd.Height),
    //         new Avalonia.Vector(96, 96),
    //         bd.Stride);
    //     bitmapTmp.UnlockBits(bd);
    //     bitmapTmp.Dispose();
    //     return bitmap1;
    // }

    public static bool TryGetText(this IDataTransfer dataTransfer, out string? result)
    {
        result = null;
        var textFormat = DataFormat.Text;
        if (!dataTransfer.Formats.Contains(textFormat))
            return false;

        var rawData = dataTransfer.TryGetText();

        result = rawData;
        return true;
    }

    /// <summary>
    /// 专为 SukiUI 适配：精准查找 Light/Dark.axaml 中的主题资源
    /// </summary>
    public static T? FindSukiUiResource<T>(object resourceKey) where T : struct
    {

        // 1. 直接通过 SukiUI 提供的 GetInstance() 获取 SukiTheme 实例（最靠谱）
        var sukiTheme = SukiTheme.GetInstance();
        var currentThemeVariant = sukiTheme.ActiveBaseTheme; // 当前主题（Light/Dark）
        var sukiResources = sukiTheme.Resources; // SukiTheme 自身的资源字典（包含 ThemeDictionaries）

        return sukiResources.TryGetResource(resourceKey, currentThemeVariant, out var value) && value is T t ? t : null;
    }

    extension(string url)
    {
        /// <summary>
        /// 判断是否为本地绝对路径
        /// </summary>
        public bool IsAbsolutePath() => Path.IsPathRooted(url);

        /// <summary>
        /// 判断是否为网络URL（http/https）
        /// </summary>
        public bool IsUrl()
        {
            // 网络链接（http/https/ftp等）
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
                return true;
            return false;
        }

        /// <summary>
        /// 从 URL 获取文本内容
        /// </summary>
        public async Task<string> FetchUrlContentAsync()
        {
            try
            {
                using var httpClient = VersionChecker.CreateHttpClientWithProxy();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                return await httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                LoggerHelper.Warning($"获取 URL 内容失败：链接={url}，原因={ex.Message}");
                return string.Empty;
            }
        }

        public string ResolveUrl(string basePath)
        {
            // 1. 处理http链接
            if (url.IsUrl())
            {
                return url;
            }

            // 获取基准目录（basePath 可能是文件路径或目录路径）
            string? baseDir = null;
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                // 如果 basePath 是文件路径，获取其目录；如果是目录路径，直接使用
                if (File.Exists(basePath))
                    baseDir = Path.GetDirectoryName(basePath);
                else if (Directory.Exists(basePath))
                    baseDir = basePath;
                else
                    baseDir = Path.GetDirectoryName(basePath);
            }

            // 判断是否为 Windows 平台
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // 2. 处理以 / 或 \ 开头的路径
            if (url.StartsWith("/") || url.StartsWith("\\"))
            {
                // 在 Linux/macOS 上，/ 开头是真正的绝对路径
                if (!isWindows && url.StartsWith("/"))
                {
                    // 先检查文件是否存在
                    if (File.Exists(url))
                        return url;

                    // 如果不存在，尝试在基准目录中查找
                    string fileName = Path.GetFileName(url);
                    if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                    {
                        string foundPath = baseDir.FindFileInDirectoryAndSubfolders(fileName);
                        if (!string.IsNullOrEmpty(foundPath))
                            return foundPath;
                    }
                    // 找不到则返回原始路径
                    return url;
                }

                // 在 Windows 上，/ 开头不是真正的绝对路径，作为相对路径处理
                // 去掉开头的 / 或 \，作为相对路径处理
                string relativePath = url.TrimStart('/', '\\');

                if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                {
                    // 先尝试直接组合路径
                    string combinedPath = Path.Combine(baseDir, relativePath);
                    if (File.Exists(combinedPath))
                        return combinedPath;

                    // 尝试在基准目录及子目录中查找文件
                    string fileName = Path.GetFileName(relativePath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        string foundPath = baseDir.FindFileInDirectoryAndSubfolders(fileName);
                        if (!string.IsNullOrEmpty(foundPath))
                            return foundPath;
                    }
                }

                // 找不到则返回组合后的路径
                return !string.IsNullOrEmpty(baseDir)
                    ? Path.Combine(baseDir, relativePath)
                    : relativePath;
            }

            // 3. 检查真正的绝对路径
            // Windows: 带盘符的路径（如 C:\xxx 或 D:\xxx）
            // Linux/macOS: 以 / 开头的路径（已在上面处理）
            bool isRealAbsolutePath = isWindows
                ? (url.Length > 1 && url[1] == ':') // Windows: C:\xxx
                : url.StartsWith("/"); // Linux/macOS: /xxx

            if (isRealAbsolutePath)
            {
                if (File.Exists(url))
                    return url;

                // 提取文件名尝试在基准目录查找
                string fileName = Path.GetFileName(url);
                if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                {
                    string foundPath = baseDir.FindFileInDirectoryAndSubfolders(fileName);
                    if (!string.IsNullOrEmpty(foundPath))
                        return foundPath;
                }
                // 找不到则返回原始绝对路径
                return url;
            }

            // 4. 处理相对路径
            // 若没有有效基准目录，直接返回
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                return url;

            // 解析相对路径为绝对路径
            string absolutePath = Path.Combine(baseDir, url);
            string normalizedPath = Path.GetFullPath(absolutePath);

            // 检查解析后的路径是否存在
            if (File.Exists(normalizedPath))
                return normalizedPath;

            // 提取文件名尝试在基准目录查找
            string targetFileName = Path.GetFileName(normalizedPath);
            if (!string.IsNullOrEmpty(targetFileName) && Directory.Exists(baseDir))
            {
                string foundPath = baseDir.FindFileInDirectoryAndSubfolders(targetFileName, true);
                if (!string.IsNullOrEmpty(foundPath))
                    return foundPath;
            }
            // 所有尝试失败，返回规范化后的路径
            return normalizedPath;
        }
    }

    /// <summary>
    /// 常见图片扩展名（用于智能匹配）
    /// </summary>
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico"];

    public static string FindFileInDirectoryAndSubfolders(this string rootDir, string fileName, bool tryAlternativeExtensions = false)
    {
        try
        {
            // 检查当前目录是否包含目标文件
            string currentDirFile = Path.Combine(rootDir, fileName);
            if (File.Exists(currentDirFile))
                return currentDirFile;

            // 如果启用了扩展名智能匹配，尝试查找同名但不同扩展名的文件
            if (tryAlternativeExtensions)
            {
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                string currentExt = Path.GetExtension(fileName).ToLowerInvariant();

                // 只对图片文件进行扩展名智能匹配
                if (ImageExtensions.Contains(currentExt))
                {
                    foreach (var ext in ImageExtensions)
                    {
                        if (ext == currentExt) continue; // 跳过当前扩展名

                        string alternativeFile = Path.Combine(rootDir, fileNameWithoutExt + ext);
                        if (File.Exists(alternativeFile))
                            return alternativeFile;
                    }
                }
            }

            // 递归查找所有子目录
            foreach (string subDir in Directory.EnumerateDirectories(rootDir))
            {
                string foundFile = FindFileInDirectoryAndSubfolders(subDir, fileName, tryAlternativeExtensions);
                if (!string.IsNullOrEmpty(foundFile))
                    return foundFile;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略无权限目录
            LoggerHelper.Info($"无权限访问目录：{rootDir}");
        }
        catch (PathTooLongException)
        {
            // 忽略路径过长
            LoggerHelper.Info($"路径过长：{rootDir}");
        }
        catch (IOException)
        {
            // 忽略I/O错误
            LoggerHelper.Info($"I/O错误：{rootDir}");
        }

        // 未找到文件
        return string.Empty;
    }

    /// <summary>
    /// 生成ADB设备的指纹字符串，用于设备匹配
    /// 指纹由 Name + Index + Address + Port 组成
    /// </summary>
    /// <param name="device">ADB设备信息</param>
    /// <returns>设备指纹字符串</returns>
    public static string GenerateDeviceFingerprint(this MaaFramework.Binding.AdbDeviceInfo device)
    {
        var index = DeviceDisplayConverter.GetFirstEmulatorIndex(device.Config);
        var (address, port) = ParseAdbSerial(device.AdbSerial);
        return GenerateDeviceFingerprint(device.Name, index, address, port);
    }

    /// <summary>
    /// 生成ADB设备的指纹字符串
    /// </summary>
    /// <param name="name">设备名称</param>
    /// <param name="adbSerial">ADB序列号</param>
    /// <param name="index">模拟器索引（-1表示无索引）</param>
    /// <returns>设备指纹字符串</returns>
    public static string GenerateDeviceFingerprint(string name, string adbSerial, int index)
    {
        var (address, port) = ParseAdbSerial(adbSerial);
        return GenerateDeviceFingerprint(name, index, address, port);
    }

    /// <summary>
    /// 生成ADB设备的指纹字符串
    /// </summary>
    public static string GenerateDeviceFingerprint(string name, int index, string? address, int? port)
    {
        var namePart = string.IsNullOrWhiteSpace(name) ? "?" : name;
        var indexPart = index >= 0 ? index.ToString() : "?";
        var addressPart = string.IsNullOrWhiteSpace(address) ? "?" : address;
        var portPart = port.HasValue && port.Value > 0 ? port.Value.ToString() : "?";
        return $"{namePart}|{indexPart}|{addressPart}|{portPart}";
    }

    private static (string? address, int? port) ParseAdbSerial(string? adbSerial)
    {
        if (string.IsNullOrWhiteSpace(adbSerial))
            return (null, null);

        var token = adbSerial.Trim();
        var splitIndex = token.IndexOfAny([' ', '\t', '\r', '\n']);
        if (splitIndex >= 0)
        {
            token = token[..splitIndex];
        }

        if (string.IsNullOrWhiteSpace(token))
            return (null, null);

        var lastColon = token.LastIndexOf(':');
        if (lastColon > 0 && lastColon < token.Length - 1)
        {
            var addressPart = token[..lastColon];
            var portPart = token[(lastColon + 1)..];
            if (int.TryParse(portPart, out var port))
            {
                return (NormalizeAddress(addressPart), port);
            }
        }

        var separatorMatch = Regex.Match(token, @"^(?<address>.+?)[-_](?<port>\d+)$");
        if (separatorMatch.Success && int.TryParse(separatorMatch.Groups["port"].Value, out var portFromSeparator))
        {
            return (NormalizeAddress(separatorMatch.Groups["address"].Value), portFromSeparator);
        }

        var prefixMatch = Regex.Match(token, @"^(?<address>[A-Za-z][A-Za-z0-9_-]*?)(?<port>\d+)$");
        if (prefixMatch.Success && !prefixMatch.Groups["address"].Value.Contains('.', StringComparison.Ordinal)
            && int.TryParse(prefixMatch.Groups["port"].Value, out var portFromPrefix))
        {
            return (NormalizeAddress(prefixMatch.Groups["address"].Value), portFromPrefix);
        }

        return (NormalizeAddress(token), null);
    }

    private static string NormalizeAddress(string address)
    {
        return address.Trim().TrimEnd(':', '-', '_');
    }

    /// <summary>
    /// 比较两个设备是否匹配（基于指纹）
    /// Name / Index / Address / Port 满足至少 3 项匹配
    /// </summary>
    /// <param name="device">当前设备</param>
    /// <param name="savedDevice">保存的设备</param>
    /// <returns>是否匹配</returns>
    public static bool MatchesFingerprint(this MaaFramework.Binding.AdbDeviceInfo device, MaaFramework.Binding.AdbDeviceInfo savedDevice)
    {
        var deviceIndex = DeviceDisplayConverter.GetFirstEmulatorIndex(device.Config);
        var savedIndex = DeviceDisplayConverter.GetFirstEmulatorIndex(savedDevice.Config);

        var (deviceAddress, devicePort) = ParseAdbSerial(device.AdbSerial);
        var (savedAddress, savedPort) = ParseAdbSerial(savedDevice.AdbSerial);

        var matches = 0;
        var comparable = 0;

        if (!string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(savedDevice.Name))
        {
            comparable++;
            if (string.Equals(device.Name, savedDevice.Name, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }

        if (deviceIndex >= 0 && savedIndex >= 0)
        {
            comparable++;
            if (deviceIndex == savedIndex)
            {
                matches++;
            }
        }

        if (!string.IsNullOrWhiteSpace(deviceAddress) && !string.IsNullOrWhiteSpace(savedAddress))
        {
            comparable++;
            if (string.Equals(deviceAddress, savedAddress, StringComparison.OrdinalIgnoreCase))
            {
                matches++;
            }
        }

        if (devicePort.HasValue && savedPort.HasValue)
        {
            comparable++;
            if (devicePort.Value == savedPort.Value)
            {
                matches++;
            }
        }

        if (comparable < 3)
        {
            return false;
        }

        return matches >= 3;
    }
}
