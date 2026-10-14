using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using HtmlAgilityPack;
using Markdown.Avalonia.Html.Core.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using FStyle = Avalonia.Media.FontStyle;
using FWeight = Avalonia.Media.FontWeight;

namespace Markdown.Avalonia.Html.Core.Parsers
{
    public class TypicalParseInfo
    {
        public string HtmlTag { get; }
        public string FlowDocumentTagText { get; }
        public Type? FlowDocumentTag { get; }
        public string? TagNameReference { get; }
        public Tags TagName { get; }
        public string? ExtraModifyName { get; }

        public TypicalParseInfo(string[] line)
        {
            FlowDocumentTagText = line[1];

            if (FlowDocumentTagText.StartsWith("#"))
            {
                FlowDocumentTag = null;
            }
            else
            {
                Type? elementType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Select(asm => asm.GetType(FlowDocumentTagText))
                    .OfType<Type>()
                    .FirstOrDefault();

                if (elementType is null)
                    throw new ArgumentException($"Failed to load type '{line[1]}'");

                FlowDocumentTag = elementType;
            }


            HtmlTag = line[0];
            TagNameReference = GetArrayAt(line, 2);
            ExtraModifyName = GetArrayAt(line, 3);

            if (TagNameReference is not null)
            {
                TagName = (Tags)Enum.Parse(typeof(Tags), TagNameReference);
            }

            if (ExtraModifyName is not null)
            {
                switch ("ExtraModify" + ExtraModifyName)
                {
                    case nameof(ExtraModifyHyperlink):
                    case nameof(ExtraModifyStrikethrough):
                    case nameof(ExtraModifySubscript):
                    case nameof(ExtraModifySuperscript):
                    case nameof(ExtraModifyAcronym):
                    case nameof(ExtraModifyCenter):
                        break;

                    default:
                        throw new InvalidOperationException("unknown method ExtraModify" + ExtraModifyName);
                }
            }

            static string? GetArrayAt(string[] array, int idx)
            {
                if (idx < array.Length && !string.IsNullOrWhiteSpace(array[idx]))
                {
                    return array[idx];
                }
                return null;
            }
        }

        /// <summary>
        /// 检查 HTML 节点是否有任何样式属性
        /// </summary>
        private bool HasAnyStyle(HtmlNode node)
        {
            // 检查 style 属性
            var styleAttr = node.Attributes["style"]?.Value;
            if (!string.IsNullOrWhiteSpace(styleAttr)) return true;

            // 检查 class 属性
            var classAttr = node.Attributes["class"]?.Value;
            if (!string.IsNullOrWhiteSpace(classAttr)) return true;

            // 检查常见的样式属性
            if (node.Attributes["color"] != null || node.Attributes["bgcolor"] != null || node.Attributes["align"] != null || node.Attributes["valign"] != null) return true;

            return false;
        }

        public bool TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<StyledElement> generated)
        {
            // 创建控件实例
            if (FlowDocumentTag is null)
            {
                switch (FlowDocumentTagText)
                {
                    case "#border":
                        var pnl = new StackPanel
                        {
                            Orientation = Orientation.Vertical
                        };
                        var parseResult = manager.ParseChildrenAndGroup(node).ToArray();
                        foreach (var ctrl in parseResult)
                            pnl.Children.Add(ctrl);

                        var bdr = new Border
                        {
                            Child = pnl
                        };
                        generated = new[]
                        {
                            bdr
                        };
                        break;

                    case "#blocks":
                        generated = manager.ParseChildrenAndGroup(node).ToArray();
                        break;

                    case "#jagging":
                        generated = manager.ParseChildrenJagging(node).ToArray();
                        break;

                    case "#inlines":
                        if (manager.ParseChildrenJagging(node).TryCast<CInline>(out var inlines))
                        {
                            generated = inlines.ToArray();
                            break;
                        }
                        else
                        {
                            generated = EnumerableExt.Empty<StyledElement>();
                            return false;
                        }

                    default:
                        throw new InvalidOperationException();
                }
            }
            else
            {
                var tag = (StyledElement)Activator.CreateInstance(FlowDocumentTag)!;

                if (tag is StackPanel pnl)
                {
                    pnl.Orientation = Orientation.Vertical;
                    var parseResult = manager.ParseChildrenAndGroup(node).ToArray();
                    foreach (var ctrl in parseResult)
                        pnl.Children.Add(ctrl);
                }
                else if (tag is CTextBlock textbox)
                {
                    var parseResult = manager.ParseChildrenJagging(node).ToArray();

                    if (parseResult.TryCast<CInline>(out var parsed))
                    {
                        textbox.Content.AddRange(parsed);
                    }
                    else if (parseResult.Length == 1 && parseResult[0] is CTextBlock)
                    {
                        tag = parseResult[0];
                    }
                    else
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                }
                else if (tag is CBold bold)
                {
                    if (!SetupCSpan(bold))
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                    // 显式确保加粗样式不被覆盖
                    bold.FontWeight = FWeight.Bold;
                }
                else if (tag is CUnderline underline)
                {
                    if (!SetupCSpan(underline))
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                    // 显式确保下划线样式不被覆盖
                    underline.IsUnderline = true;
                }
                else if (tag is CItalic italic)
                {
                    if (!SetupCSpan(italic))
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                    // 显式确保斜体样式不被覆盖
                    italic.FontStyle = FStyle.Italic;
                }
                else if (tag is CSpan span)
                {
                    // 对于没有任何样式的简单 <span> 标签，直接返回内容，避免不必要的包装
                    // 这可以确保与纯文本有相同的渲染行为
                    if (HtmlTag == "span" && !HasAnyStyle(node))
                    {
                        var parseResult = manager.ParseChildrenJagging(node).ToArray();
                        if (parseResult.TryCast<CInline>(out var inlines))
                        {
                            generated = inlines.ToArray();
                            return true;
                        }
                    }

                    if (!SetupCSpan(span))
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                }
                else if (tag is CCode code)
                {
                    var codecontent = (AvaloniaList<CInline>)code.Content;
                    var codespan = new CSpan();
                    codecontent.Add(codespan);

                    if (!SetupCSpan(codespan))
                    {
                        generated = EnumerableExt.Empty<StyledElement>();
                        return false;
                    }
                }
                else if (tag is not CLineBreak)
                {
                    throw new InvalidOperationException();
                }

                generated = [tag];

                bool SetupCSpan(CSpan span)
                {
                    var content = (AvaloniaList<CInline>)span.Content;
                    var parseResult = manager.ParseChildrenJagging(node).ToArray();

                    if (parseResult.TryCast<CInline>(out var parsed))
                    {
                        content.AddRange(parsed);
                    }
                    else if (tag is CSpan && manager.Grouping(parseResult).TryCast<CTextBlock>(out var paragraphs))
                    {
                        foreach (var para in paragraphs)
                        foreach (var inline in para.Content.ToArray())
                            content.Add(inline);
                    }
                    else return false;

                    return true;
                }
            }

            // 应用对齐样式（核心逻辑：处理align属性和text-align样式）
            var alignment = DocUtils.GetHorizontalAlignment(node);
            if (alignment.HasValue)
            {
                foreach (var element in generated)
                {
                    // 处理容器控件（如StackPanel、Border）的水平对齐
                    if (element is Panel panel)
                    {
                        panel.HorizontalAlignment = alignment.Value;
                    }
                    // 处理文本控件（如CTextBlock）的文本对齐
                    if (element is CTextBlock textBlock)
                    {
                        textBlock.TextAlignment = alignment.Value switch
                        {
                            HorizontalAlignment.Left => TextAlignment.Left,
                            HorizontalAlignment.Right => TextAlignment.Right,
                            HorizontalAlignment.Center => TextAlignment.Center,
                            _ => textBlock.TextAlignment
                        };
                        textBlock.HorizontalAlignment = alignment.Value;
                    }
                    // 处理Border控件的对齐
                    if (element is Border border)
                    {
                        border.HorizontalAlignment = alignment.Value;
                        if (border.Child is Panel borderChild)
                        {
                            borderChild.HorizontalAlignment = alignment.Value;
                        }
                    }
                }
            }

            var foregroundBrush = DocUtils.GetForegroundColor(node);
            if (foregroundBrush != null)
            {
                foreach (var element in generated)
                {
                    // 为文本控件设置前景色
                    ApplyForegroundToTextElements(element, foregroundBrush);
                }
            }


            var fontSize = DocUtils.GetFontSize(node);
            if (fontSize.HasValue)
            {
                foreach (var element in generated)
                {
                    // 为文本控件设置字体大小
                    ApplyFontSizeToTextElements(element, fontSize.Value);
                }
            }

            // 新增：应用字体粗细
            var fontWeight = DocUtils.GetFontWeight(node);
            if (fontWeight.HasValue)
            {
                foreach (var element in generated)
                {
                    ApplyFontWeightToTextElements(element, fontWeight.Value);
                }
            }

            // 新增：应用字体样式（斜体）
            var fontStyle = DocUtils.GetFontStyle(node);
            if (fontStyle.HasValue)
            {
                foreach (var element in generated)
                {
                    ApplyFontStyleToTextElements(element, fontStyle.Value);
                }
            }

            // 新增：应用文本装饰（删除线/下划线）
            var (isStrikethrough, isUnderline) = DocUtils.GetTextDecoration(node);
            if (isStrikethrough || isUnderline)
            {
                foreach (var element in generated)
                {
                    ApplyTextDecorationToTextElements(element, isStrikethrough, isUnderline);
                }
            }

            // 新增：应用字体家族（font-family）
            var fontFamily = DocUtils.GetFontFamily(node);
            if (fontFamily != null)
            {
                foreach (var element in generated)
                {
                    ApplyFontFamilyToTextElements(element, fontFamily);
                }
            }

            // 新增：应用背景色（background-color）
            var backgroundColor = DocUtils.GetBackgroundColor(node);
            if (backgroundColor != null)
            {
                foreach (var element in generated)
                {
                    ApplyBackgroundColorToTextElements(element, backgroundColor);
                }
            }

            // 应用标签样式
            if (TagNameReference is not null)
            {
                var clsNm = TagName.GetClass();
                foreach (var tag in generated)
                {
                    tag.Classes.Add(clsNm);
                    if (tag is Border bdr)
                        bdr.Child.Classes.Add(clsNm);
                }
            }

            // 额外样式修改
            if (ExtraModifyName is not null)
            {
                switch ("ExtraModify" + ExtraModifyName)
                {
                    case nameof(ExtraModifyHyperlink):
                        foreach (var tag in generated)
                            ExtraModifyHyperlink((CHyperlink)tag, node, manager);
                        break;
                    case nameof(ExtraModifyStrikethrough):
                        foreach (var tag in generated)
                            ExtraModifyStrikethrough((CSpan)tag, node, manager);
                        break;
                    case nameof(ExtraModifySubscript):
                        foreach (var tag in generated)
                            ExtraModifySubscript((CSpan)tag, node, manager);
                        break;
                    case nameof(ExtraModifySuperscript):
                        foreach (var tag in generated)
                            ExtraModifySuperscript((CSpan)tag, node, manager);
                        break;
                    case nameof(ExtraModifyAcronym):
                        foreach (var tag in generated)
                            ExtraModifyAcronym((CSpan)tag, node, manager);
                        break;
                    case nameof(ExtraModifyCenter):
                        foreach (var tag in generated)
                            ExtraModifyCenter((Border)tag, node, manager);
                        break;
                }
            }

            return true;
        }

        private void ApplyFontSizeToTextElements(StyledElement element, double fontSize)
        {
            // 直接设置文本控件的字体大小
            if (element is CTextBlock textBlock)
            {
                textBlock.FontSize = fontSize;
            }
            else if (element is CSpan span)
            {
                span.FontSize = fontSize;
            }
            else if (element is CCode code)
            {
                code.FontSize = fontSize;
            }
            else if (element is CBold bold)
            {
                bold.FontSize = fontSize;
            }
            else if (element is CItalic italic)
            {
                italic.FontSize = fontSize;
            }
            else if (element is CUnderline underline)
            {
                underline.FontSize = fontSize;
            }
            // 递归处理容器控件的子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyFontSizeToTextElements(styledChild, fontSize);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyFontSizeToTextElements(borderChild, fontSize);
            }
        }

        private void ApplyForegroundToTextElements(StyledElement element, Brush brush)
        {
            // 直接设置文本控件的前景色
            if (element is CTextBlock textBlock)
            {
                textBlock.Foreground = brush;
            }
            else if (element is CSpan span)
            {
                span.Foreground = brush;
            }
            else if (element is CCode code)
            {
                code.Foreground = brush;
            }
            // 递归处理容器控件的子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyForegroundToTextElements(styledChild, brush);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyForegroundToTextElements(borderChild, brush);
            }
        }

        /// <summary>
        /// 为文本元素应用字体粗细样式
        /// </summary>
        private void ApplyFontWeightToTextElements(StyledElement element, FWeight fontWeight)
        {
            if (element is CTextBlock textBlock)
            {
                textBlock.FontWeight = fontWeight;
            }
            else if (element is CSpan span)
            {
                span.FontWeight = fontWeight;
            }
            else if (element is CCode code)
            {
                code.FontWeight = fontWeight;
            }
            else if (element is CBold bold)
            {
                // 保留显式加粗，但允许通过样式覆盖
                bold.FontWeight = fontWeight;
            }
            else if (element is CItalic italic)
            {
                italic.FontWeight = fontWeight;
            }
            else if (element is CUnderline underline)
            {
                underline.FontWeight = fontWeight;
            }
            // 递归处理容器子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyFontWeightToTextElements(styledChild, fontWeight);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyFontWeightToTextElements(borderChild, fontWeight);
            }
        }

        /// <summary>
        /// 为文本元素应用字体样式（斜体）
        /// </summary>
        private void ApplyFontStyleToTextElements(StyledElement element, FStyle fontStyle)
        {
            if (element is CTextBlock textBlock)
            {
                textBlock.FontStyle = fontStyle;
            }
            else if (element is CSpan span)
            {
                span.FontStyle = fontStyle;
            }
            else if (element is CCode code)
            {
                code.FontStyle = fontStyle;
            }
            else if (element is CBold bold)
            {
                bold.FontStyle = fontStyle;
            }
            else if (element is CItalic italic)
            {
                // 保留显式斜体，但允许通过样式覆盖
                italic.FontStyle = fontStyle;
            }
            else if (element is CUnderline underline)
            {
                underline.FontStyle = fontStyle;
            }
            // 递归处理容器子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyFontStyleToTextElements(styledChild, fontStyle);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyFontStyleToTextElements(borderChild, fontStyle);
            }
        }

        /// <summary>
        /// 为文本元素应用文本装饰（删除线/下划线）
        /// </summary>
        private void ApplyTextDecorationToTextElements(StyledElement element, bool isStrikethrough, bool isUnderline)
        {
            if (element is CTextBlock textBlock)
            {
                foreach (var cInline in textBlock.Content)
                {
                    if (isStrikethrough) cInline.IsStrikethrough = true;
                    if (isUnderline) cInline.IsUnderline = true;
                }
            }
            else if (element is CSpan span)
            {
                if (isStrikethrough) span.IsStrikethrough = true;
                if (isUnderline) span.IsUnderline = true;
            }
            else if (element is CCode code)
            {
                if (isStrikethrough) code.IsStrikethrough = true;
                if (isUnderline) code.IsUnderline = true;
            }
            else if (element is CBold bold)
            {
                if (isStrikethrough) bold.IsStrikethrough = true;
                if (isUnderline) bold.IsUnderline = true;
            }
            else if (element is CItalic italic)
            {
                if (isStrikethrough) italic.IsStrikethrough = true;
                if (isUnderline) italic.IsUnderline = true;
            }
            else if (element is CUnderline underline)
            {
                if (isStrikethrough) underline.IsStrikethrough = true;
                // 保留显式下划线，但允许通过样式增强
                if (isUnderline) underline.IsUnderline = true;
            }
            // 递归处理容器子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyTextDecorationToTextElements(styledChild, isStrikethrough, isUnderline);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyTextDecorationToTextElements(borderChild, isStrikethrough, isUnderline);
            }
        }

        /// <summary>
        /// 为文本元素应用字体家族（font-family）
        /// </summary>
        private void ApplyFontFamilyToTextElements(StyledElement element, FontFamily fontFamily)
        {
            if (element is CTextBlock textBlock)
            {
                textBlock.FontFamily = fontFamily;
            }
            else if (element is CSpan span)
            {
                span.FontFamily = fontFamily;
            }
            else if (element is CCode code)
            {
                code.FontFamily = fontFamily;
            }
            else if (element is CBold bold)
            {
                bold.FontFamily = fontFamily;
            }
            else if (element is CItalic italic)
            {
                italic.FontFamily = fontFamily;
            }
            else if (element is CUnderline underline)
            {
                underline.FontFamily = fontFamily;
            }
            // 递归处理容器子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyFontFamilyToTextElements(styledChild, fontFamily);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyFontFamilyToTextElements(borderChild, fontFamily);
            }
        }

        /// <summary>
        /// 为文本元素应用背景色（background-color）
        /// </summary>
        private void ApplyBackgroundColorToTextElements(StyledElement element, Brush brush)
        {
            if (element is CTextBlock textBlock)
            {
                textBlock.Background = brush;
            }
            else if (element is CSpan span)
            {
                span.Background = brush;
            }
            else if (element is CCode code)
            {
                code.Background = brush;
            }
            else if (element is CBold bold)
            {
                bold.Background = brush;
            }
            else if (element is CItalic italic)
            {
                italic.Background = brush;
            }
            else if (element is CUnderline underline)
            {
                underline.Background = brush;
            }
            // 递归处理容器子元素
            else if (element is Panel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is StyledElement styledChild)
                    {
                        ApplyBackgroundColorToTextElements(styledChild, brush);
                    }
                }
            }
            else if (element is Border { Child: StyledElement borderChild })
            {
                ApplyBackgroundColorToTextElements(borderChild, brush);
            }
        }


        public void ExtraModifyHyperlink(CHyperlink link, HtmlNode node, ReplaceManager manager)
        {
            var href = node.Attributes["href"]?.Value;
            if (href is not null)
            {
                link.CommandParameter = href;
                link.Command = (urlTxt) =>
                {
                    var command = manager.HyperlinkCommand;
                    if (command != null && command.CanExecute(urlTxt))
                    {
                        command.Execute(urlTxt);
                    }
                };
            }
        }

        public void ExtraModifyStrikethrough(CSpan span, HtmlNode node, ReplaceManager manager)
        {
            span.IsStrikethrough = true;
        }

        public void ExtraModifySubscript(CSpan span, HtmlNode node, ReplaceManager manager)
        {
            span.TextVerticalAlignment = TextVerticalAlignment.Bottom;
            span.FontSize = 10;
        }

        public void ExtraModifySuperscript(CSpan span, HtmlNode node, ReplaceManager manager)
        {
            span.TextVerticalAlignment = TextVerticalAlignment.Top;
            span.FontSize = 10;
        }

        public void ExtraModifyAcronym(CSpan span, HtmlNode node, ReplaceManager manager)
        {
            span.IsUnderline = true;
            span.UnderlineStyle = CTextUnderlineStyle.Dotted;

            var title = HtmlEntity.DeEntitize(node.GetAttributeValue("title", String.Empty));
            if (!String.IsNullOrWhiteSpace(title))
            {
                span.ToolTipText = title;
            }
        }

        public void ExtraModifyCenter(Border center, HtmlNode node, ReplaceManager manager)
        {
            center.HorizontalAlignment = HorizontalAlignment.Center;
            foreach (var child in ((StackPanel)center.Child!).Children)
            {
                if (child is CTextBlock cbox)
                {
                    cbox.HorizontalAlignment = HorizontalAlignment.Center;
                }
            }
        }

        internal static IEnumerable<TypicalParseInfo> Load(string resourcePath)
        {
            var asm = typeof(TypicalBlockParser).Assembly;
            using var stream = asm.GetManifestResourceStream(resourcePath);

            if (stream is null) throw new ArgumentException($"resource not found: '{resourcePath}'");

            using var reader = new StreamReader(stream!);
            while (reader.ReadLine() is string line)
            {
                if (line.StartsWith("#")) continue;
                var elements = line.Split('|').Select(t => t.Trim()).ToArray();
                yield return new TypicalParseInfo(elements);
            }
        }
    }
}
