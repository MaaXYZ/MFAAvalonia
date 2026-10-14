using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Markdown.Avalonia.Full;
using Markdown.Avalonia.Html;
using Markdown.Avalonia.Svg;
using Markdown.Avalonia.SyntaxHigh;
using Markdown.Avalonia.Math;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Notification;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.Pages;
using MFAAvalonia.Views.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SukiUI.Controls;
using SukiUI.Dialogs;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW;

/// <summary>
/// Focus 消息处理类
/// </summary>
public class FocusHandler
{
    private const double ToastMarkdownMaxHeight = 220;
    private const double DialogMarkdownMaxHeight = 420;
    private const double DialogMarkdownMinWidth = 320;
    private const double DialogMarkdownMaxWidth = 720;
    private const double NotificationTitleMarkdownMaxHeight = 44;
    private const double NotificationMarkdownMaxHeight = 260;

    private AutoInitDictionary autoInitDictionary;
    private readonly TaskQueueViewModel _viewModel;

    public FocusHandler(AutoInitDictionary autoInitDictionary, TaskQueueViewModel viewModel)
    {
        this.autoInitDictionary = autoInitDictionary;
        _viewModel = viewModel;
    }

    public void UpdateDictionary(AutoInitDictionary dictionary)
    {
        autoInitDictionary = dictionary;
    }

    /// <summary>
    /// Focus 数据模型
    /// </summary>
    public class Focus
    {
        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("start")]
        public List<string>? Start;

        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("succeeded")]
        public List<string>? Succeeded;

        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("failed")]
        public List<string>? Failed;

        [JsonConverter(typeof(GenericSingleOrListConverter<string>))] [JsonProperty("toast")]
        public List<string>? Toast;

        [JsonProperty("aborted")] public bool? Aborted;
    }

    /// <summary>
    /// 解析带颜色标记的文本
    /// </summary>
    public static (string Text, string? Color) ParseColorText(string input)
    {
        var match = Regex.Match(input.Trim(), @"\[color:(?<color>.*?)\](?<text>.*?)\[/color\]", RegexOptions.IgnoreCase);

        if (match.Success)
        {
            string color = match.Groups["color"].Value.Trim();
            string text = match.Groups["text"].Value;
            return (text, color);
        }

        return (input, null);
    }

    /// <summary>
    /// 显示 Focus 消息
    /// </summary>
    /// <param name="taskModel">任务模型 JObject</param>
    /// <param name="message">消息类型</param>
    /// <param name="detail">详情</param>
    /// <param name="imageBuffer">当前截图的 MaaImageBuffer，用于新协议中 {image} 占位符替换为 base64</param>
    /// <param name="onAborted">中止回调</param>
    public void DisplayFocus(JObject taskModel, string message, string detail, MaaImageBuffer? imageBuffer = null, Action? onAborted = null)
    {
        try
        {
            if (taskModel["focus"] == null)
                return;

            var focusToken = taskModel["focus"];
            var focus = new Focus();
            JObject? newProtocolFocus = null;

            // 解析focus内容，同时提取新旧协议数据
            if (focusToken!.Type == JTokenType.String)
            {
                // 旧协议：字符串形式（等价于start）
                focus.Start = new List<string>
                {
                    focusToken.Value<string>()!
                };
            }
            else if (focusToken.Type == JTokenType.Object)
            {
                var focusObj = focusToken as JObject;
                // 提取旧协议字段（start/succeeded/failed/toast等）
                focus = focusObj!.ToObject<Focus>();
                // 提取新协议字段（消息类型为键的条目）
                newProtocolFocus = new JObject(
                    focusObj.Properties()
                        .Select(prop => new JProperty(prop.Name, prop.Value))
                );
            }

            // 解析详情数据（用于新协议占位符替换，detail 为空时 detailsObj 保持 null）
            JObject? detailsObj = null;
            if (!string.IsNullOrEmpty(detail))
            {
                try
                {
                    detailsObj = JObject.Parse(detail);
                }
                catch
                {
                    // 忽略详情解析错误
                }
            }

            // 1. 处理新协议（不依赖 detail 是否为空，node.action.starting 的 detail 通常为空）
            if (newProtocolFocus is { HasValues: true } && newProtocolFocus.TryGetValue(message, out var templateToken))
            {
                ProcessNewProtocol(templateToken, taskModel, detailsObj, imageBuffer);
            }

            // 2. 处理旧协议（如果有）
            ProcessOldProtocol(focus, message, onAborted);
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"处理焦点协议消息失败：原因={e.Message}", e);
        }
    }

    /// <summary>
    /// 处理新协议消息。
    /// 支持三种 focus 值形式：
    /// 1. 字符串: "content" → display=log
    /// 2. 字符串数组: ["c1","c2"] → 每项 display=log
    /// 3. 对象: { "content": "...", "display": "toast" | ["log","toast"] }
    /// </summary>
    private void ProcessNewProtocol(JToken templateToken, JObject taskModel, JObject? detailsObj, MaaImageBuffer? imageBuffer)
    {
        List<string> displays = new List<string> { "log" };

        if (templateToken.Type == JTokenType.Object)
        {
            // 对象形式：{ "content": "...", "display": "toast" | ["log","toast"] }
            var obj = (JObject)templateToken;
            var content = obj["content"]?.Value<string>();
            var displayToken = obj["display"];
            if (displayToken != null)
            {
                if (displayToken.Type == JTokenType.Array)
                    displays = displayToken.ToObject<List<string>>() ?? displays;
                else if (displayToken.Type == JTokenType.String)
                    displays = new List<string> { displayToken.Value<string>()! };
            }
            if (content != null)
            {
                var displayText = ResolveFocusContent(content, detailsObj, imageBuffer);
                DispatchToChannels(displayText, displays);
            }
        }
        else if (templateToken.Type == JTokenType.Array)
        {
            // 数组形式：每项为字符串，全部 display=log
            foreach (var item in templateToken.Children())
            {
                if (item.Type == JTokenType.String)
                {
                    var template = item.Value<string>();
                    var displayText = ResolveFocusContent(template, detailsObj, imageBuffer);
                    DispatchToChannels(displayText, new List<string> { "log" });
                }
            }
        }
        else if (templateToken.Type == JTokenType.String)
        {
            // 字符串形式：display=log
            var template = templateToken.Value<string>();
            var displayText = ResolveFocusContent(template, detailsObj, imageBuffer);
            DispatchToChannels(displayText, new List<string> { "log" });
        }
    }

    /// <summary>
    /// 根据 display 渠道列表分发消息
    /// </summary>
    private void DispatchToChannels(string displayText, List<string> displays)
    {
        foreach (var channel in displays)
        {
            switch (channel.ToLower())
            {
                case "log":
                    _viewModel.AddMarkdown(TaskQueueView.ConvertCustomMarkup(displayText));
                    break;
                case "toast":
                    DispatcherHelper.RunOnMainThread(() =>
                        ToastHelper.CreateToastByType(
                                Avalonia.Controls.Notifications.NotificationType.Information,
                                LangKeys.Tip.ToLocalization(),
                                CreateFocusMarkdownContent(displayText, ToastMarkdownMaxHeight))
                            .Queue());
                    break;
                case "notification":
                    ToastNotification.Show(
                        LangKeys.Tip.ToLocalization(),
                        CreateFocusMarkdownContent(displayText, NotificationMarkdownMaxHeight));
                    break;
                case "dialog":
                    // 非阻塞式弹窗：fire-and-forget，任务继续执行
                    Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                    {
                        await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
                        {
                            Content = CreateFocusMarkdownContent(displayText, DialogMarkdownMaxHeight, DialogMarkdownMaxWidth),
                            ActionButtonsPreset = SukiMessageBoxButtons.OK,
                        }, new SukiMessageBoxOptions
                        {
                            Title = LangKeys.Tip.ToLocalization(),
                            CanResize = true,
                            MinWidth = DialogMarkdownMinWidth,
                            MinHeight = 120,
                            MaxWidthScreenRatio = 0.6,
                            MaxHeightScreenRatio = 0.7,
                        });
                    });
                    break;
                case "modal":
                    // 阻塞式弹窗：InvokeAsync 返回 Task，.Wait() 阻塞回调线程直到用户关闭弹窗
                    var modalProcessor = _viewModel.Processor;
                    modalProcessor?.SetWaitingForModal(true);
                    Avalonia.Threading.Dispatcher.UIThread.InvokeAsync((async () =>
                    {
                        await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
                        {
                            Content = CreateFocusMarkdownContent(displayText, DialogMarkdownMaxHeight, DialogMarkdownMaxWidth),
                            ActionButtonsPreset = SukiMessageBoxButtons.OK,
                        }, new SukiMessageBoxOptions
                        {
                            Title = LangKeys.Tip.ToLocalization(),
                            CanResize = true,
                            MinWidth = DialogMarkdownMinWidth,
                            MinHeight = 120,
                            MaxWidthScreenRatio = 0.6,
                            MaxHeightScreenRatio = 0.7,
                        });
                    })).Wait();
                    modalProcessor?.SetWaitingForModal(false);
                    break;
            }
        }
    }

    /// <summary>
    /// 处理旧协议消息
    /// </summary>
    private void ProcessOldProtocol(Focus? focus, string message, Action? onAborted)
    {
        if (focus == null) return;

        switch (message)
        {
            case MaaMsg.Node.Action.Succeeded:
                if (focus.Succeeded != null)
                {
                    foreach (var line in focus.Succeeded)
                    {
                        var (text, color) = ParseColorText(line);
                        _viewModel.AddLog(HandleStringsWithVariables(text), color == null ? null : BrushHelper.ConvertToBrush(color));
                    }
                }
                break;

            case MaaMsg.Node.Action.Failed:
                if (focus.Failed != null)
                {
                    foreach (var line in focus.Failed)
                    {
                        var (text, color) = ParseColorText(line);
                        _viewModel.AddLog(HandleStringsWithVariables(text), color == null ? null : BrushHelper.ConvertToBrush(color));
                    }
                }
                break;

            case MaaMsg.Node.Action.Starting:
                if (focus.Aborted == true)
                {
                    onAborted?.Invoke();
                }
                if (focus.Toast is { Count: > 0 })
                {
                    var title = ResolveLegacyFocusText(focus.Toast[0]);
                    var content = focus.Toast.Count >= 2 ? ResolveLegacyFocusText(focus.Toast[1]) : "";
                    ToastNotification.Show(
                        CreateFocusMarkdownContent(title, NotificationTitleMarkdownMaxHeight),
                        CreateFocusMarkdownContent(content, NotificationMarkdownMaxHeight));
                }
                if (focus.Start != null)
                {
                    foreach (var line in focus.Start)
                    {
                        var (text, color) = ParseColorText(line);
                        _viewModel.AddLog(HandleStringsWithVariables(text), color == null ? null : BrushHelper.ConvertToBrush(color));
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 替换模板中的占位符
    /// </summary>
    private string ReplacePlaceholders(string template, JObject? details, MaaImageBuffer? imageBuffer = null)
    {
        string result = template;

        // 替换 {image} 为 Markdown 内联图片（使用 maa://image/ 协议）
        if (result.Contains("{image}") && imageBuffer != null)
        {
            var imageUrl = StoreImageAndGetUrl(imageBuffer);
            if (!string.IsNullOrEmpty(imageUrl))
            {
                result = result.Replace("{image}", $"![image]({imageUrl})");
            }
        }

        // 再用details中的属性替换（如果有的话）
        if (details != null)
        {
            foreach (var prop in details.Properties())
            {
                result = result.Replace($"{{{prop.Name}}}", prop.Value.ToString());
            }
        }

        return result;
    }

    private string ResolveFocusContent(string? template, JObject? detailsObj, MaaImageBuffer? imageBuffer = null)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var contentBaseDir = GetFocusContentBaseDir();
        var resolved = template.ResolveContentAsync(contentBaseDir).GetAwaiter().GetResult();
        LogUnresolvedFocusPath(template, resolved, contentBaseDir);
        return ReplacePlaceholders(resolved, detailsObj, imageBuffer);
    }

    private string ResolveLegacyFocusText(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var value = HandleStringsWithVariables(template);
        var contentBaseDir = GetFocusContentBaseDir();
        var resolved = value.ResolveContentAsync(contentBaseDir).GetAwaiter().GetResult();
        LogUnresolvedFocusPath(value, resolved, contentBaseDir);

        // 兼容 focus 老协议里直接使用普通 i18n key 的情况。
        if (resolved == value)
        {
            var localized = value.ToLocalization();
            if (localized != value)
                return localized;
        }

        return resolved;
    }

    private static string GetFocusContentBaseDir()
    {
        var interfacePath = MaaProcessor.GetInterfaceFilePath();
        if (!string.IsNullOrWhiteSpace(interfacePath))
        {
            var interfaceDir = Path.GetDirectoryName(interfacePath);
            if (!string.IsNullOrWhiteSpace(interfaceDir))
                return interfaceDir;
        }

        return AppPaths.DataRoot;
    }

    private static void LogUnresolvedFocusPath(string original, string resolved, string baseDir)
    {
        if (!LooksLikeFilePath(original))
            return;

        if (resolved != original)
            return;

        var candidatePath = MaaInterface.ReplacePlaceholder(original, baseDir, true);
        LoggerHelper.Warning(
            $"Focus 内容文件解析失败：input={original}, baseDir={baseDir}, candidate={candidatePath}, exists={File.Exists(candidatePath)}");
    }

    private static bool LooksLikeFilePath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("$", StringComparison.Ordinal))
            return false;

        return input.Contains(Path.DirectorySeparatorChar)
               || input.Contains(Path.AltDirectorySeparatorChar)
               || input.StartsWith("./", StringComparison.Ordinal)
               || input.StartsWith("../", StringComparison.Ordinal)
               || Path.HasExtension(input);
    }

    private static Control CreateFocusMarkdownContent(string markdown, double maxHeight, double? maxWidth = null)
    {
        return CreateMarkdownContent(
            TaskQueueView.ConvertCustomMarkup(markdown),
            EstimateMarkdownMaxHeight(markdown, maxHeight),
            maxWidth.HasValue ? EstimateMarkdownWidth(markdown, maxWidth.Value) : null,
            maxWidth);
    }

    private static double EstimateMarkdownMaxHeight(string markdown, double maxHeight)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return 1;

        if (markdown.Contains("![", StringComparison.Ordinal)
            || markdown.Contains("<img", StringComparison.OrdinalIgnoreCase)
            || markdown.Contains("```", StringComparison.Ordinal)
            || markdown.Contains('|', StringComparison.Ordinal))
            return maxHeight;

        var lineCount = Regex.Matches(markdown, @"\r\n|\r|\n").Count + 1;
        var estimatedHeight = 18 + lineCount * 24;
        return Math.Min(maxHeight, estimatedHeight);
    }

    private static double EstimateMarkdownWidth(string markdown, double maxWidth)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return DialogMarkdownMinWidth;

        if (markdown.Contains("![", StringComparison.Ordinal)
            || markdown.Contains("<img", StringComparison.OrdinalIgnoreCase)
            || markdown.Contains("```", StringComparison.Ordinal)
            || markdown.Contains('|', StringComparison.Ordinal))
            return maxWidth;

        var longestLine = markdown
            .Split(["\r\n", "\r", "\n"], StringSplitOptions.None)
            .Select(line => line.Trim().Length)
            .DefaultIfEmpty(0)
            .Max();

        var estimatedWidth = 80 + longestLine * 9;
        return Math.Clamp(estimatedWidth, DialogMarkdownMinWidth, maxWidth);
    }

    private static Control CreateMarkdownContent(string markdown, double maxHeight, double? width = null, double? maxWidth = null)
    {
        var assetRoot = AppPaths.DataRoot;
        var linkCommand = new MFALinkCommand
        {
            CurrentDocumentPath = assetRoot
        };

        var viewer = new MarkdownScrollViewer
        {
            Markdown = markdown,
            Focusable = true,
            EnableVirtualization = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            MaxHeight = maxHeight,
            Width = width ?? double.NaN,
            MaxWidth = maxWidth ?? double.PositiveInfinity,
            Margin = new Thickness(0, 4, 0, 0),
            Engine = new Markdown.Avalonia.Markdown
            {
                HyperlinkCommand = linkCommand,
                AssetPathRoot = assetRoot,
                StrictBoldItalic = false,
            },
            Plugins = CreateMarkdownPlugins(linkCommand),
        };
        viewer.Styles.Add(new StyleInclude(new Uri("avares://MFAAvalonia.Core/Assets/Style/MdXamlStyles.axaml"))
        {
            Source = new Uri("avares://MFAAvalonia.Core/Assets/Style/MdXamlStyles.axaml")
        });

        return viewer;
    }

    private static Markdown.Avalonia.MdAvPlugins CreateMarkdownPlugins(MFALinkCommand linkCommand)
    {
        var plugins = new Markdown.Avalonia.MdAvPlugins
        {
            PathResolver = new CustomPathResolver(),
            HyperlinkCommand = linkCommand,
        };
        plugins.Plugins.Add(new HtmlPlugin());
        plugins.Plugins.Add(new SvgFormat());
        plugins.Plugins.Add(new Markdown.Avalonia.ChatAISetup());
        plugins.Plugins.Add(new SyntaxHighlight());
        plugins.Plugins.Add(new MathPlugin());
        return plugins;
    }

    /// <summary>
    /// 将 MaaImageBuffer 存入内存图片存储，返回 maa://image/{key} URL
    /// </summary>
    private static string? StoreImageAndGetUrl(MaaImageBuffer imageBuffer)
    {
        try
        {
            if (imageBuffer.IsInvalid || imageBuffer.IsEmpty)
                return null;

            if (!imageBuffer.TryGetEncodedData(out Stream encodedDataStream))
                return null;

            using (encodedDataStream)
            {
                encodedDataStream.Seek(0, SeekOrigin.Begin);
                using var memoryStream = new MemoryStream();
                encodedDataStream.CopyTo(memoryStream);
                return CustomPathResolver.StoreImage(memoryStream.ToArray());
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"存储图片失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 处理带变量的字符串
    /// </summary>
    public string HandleStringsWithVariables(string content)
    {
        try
        {
            return Regex.Replace(content, @"\{(\+\+|\-\-)?(\w+)(\+\+|\-\-)?([\+\-\*/]\w+)?\}", match =>
            {
                var prefix = match.Groups[1].Value;
                var counterKey = match.Groups[2].Value;
                var suffix = match.Groups[3].Value;
                var operation = match.Groups[4].Value;

                int value = autoInitDictionary.GetValueOrDefault(counterKey, 0);

                // 前置操作符
                if (prefix == "++")
                {
                    value = ++autoInitDictionary[counterKey];
                }
                else if (prefix == "--")
                {
                    value = --autoInitDictionary[counterKey];
                }

                // 后置操作符
                if (suffix == "++")
                {
                    value = autoInitDictionary[counterKey]++;
                }
                else if (suffix == "--")
                {
                    value = autoInitDictionary[counterKey]--;
                }

                // 算术操作
                if (!string.IsNullOrEmpty(operation))
                {
                    string operationType = operation[0].ToString();
                    string operandKey = operation.Substring(1);

                    if (autoInitDictionary.TryGetValue(operandKey, out var operandValue))
                    {
                        value = operationType switch
                        {
                            "+" => value + operandValue,
                            "-" => value - operandValue,
                            "*" => value * operandValue,
                            "/" => value / operandValue,
                            _ => value
                        };
                    }
                }

                return value.ToString();
            });
        }
        catch (Exception e)
        {
            LoggerHelper.Error($"解析带变量的焦点文本失败：原因={e.Message}", e);
            ErrorView.ShowException(e);
            return content;
        }
    }

    /// <summary>
    /// 静态方法：处理带变量的字符串（使用指定的字典）
    /// </summary>
    public static string HandleStringsWithVariables(string content, AutoInitDictionary autoInitDictionary)
    {
        var handler = new FocusHandler(autoInitDictionary, null!);
        return handler.HandleStringsWithVariables(content);
    }
}
