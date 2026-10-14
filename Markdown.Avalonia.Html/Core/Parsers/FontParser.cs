using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using HtmlAgilityPack;
using Markdown.Avalonia.Html.Core.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Markdown.Avalonia.Html.Core.Parsers;

public class FontParser : IInlineTagParser
{
    // 支持的标签为font
    public IEnumerable<string> SupportTag => ["font"];

    // 显式实现ITagParser接口的TryReplace方法
    bool ITagParser.TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<StyledElement> generated)
    {
        var result = TryReplace(node, manager, out var inlineElements);
        generated = inlineElements;
        return result;
    }
    public static ICollection<T> AddRange<T>(IEnumerable<T>? collection, IEnumerable<T> newItems)
    {
        if (collection == null)
            return newItems.ToList();
        var list = new List<T>(collection);

        foreach (T newItem in newItems)
            list.Add(newItem);
        return list;
    }

    // 实现行内标签的核心替换逻辑
    public bool TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<CInline> generated)
    {
        generated = Enumerable.Empty<CInline>();

        // 创建行内文本容器（CSpan是ColorTextBlock的行内容器）
        var fontSpan = new CSpan();
        fontSpan.Classes.Add("Font");

        // 1. 解析并应用颜色（color属性）
        ApplyColor(node, fontSpan);

        // 2. 解析并应用字体大小（size属性）
        var fontSize = ReadFontSize(node);
        if (fontSize.HasValue)
            fontSpan.FontSize = fontSize.Value;

        // 3. HTML face属性和CSS font-family都应作用于内部文本。
        var fontFamily = ReadFontFamily(node);
        if (fontFamily is not null)
            fontSpan.FontFamily = fontFamily;

        // 4. 解析并应用对齐样式（align属性或style中的text-align）
        ApplyAlignment(node, fontSpan, manager);

        // 5. 解析<font>标签的子内容并添加到容器中
        var childInlines = manager.ParseChildrenJagging(node).ToArray();
        if (childInlines.TryCast<CInline>(out var parsedInlines))
        {
            ApplyTypographyToChildren(parsedInlines, fontSize, fontFamily);
            fontSpan.Content = AddRange(fontSpan.Content, parsedInlines);
        }
        else
        {
            // 子内容解析失败时返回false
            return false;
        }

        // 返回生成的行内元素
        generated = [fontSpan];
        return true;
    }

    /// <summary>
    /// 解析<font>的color属性并应用到CSpan
    /// </summary>
    private void ApplyColor(HtmlNode node, CSpan span)
    {
        // 优先解析<font>的color属性（如<font color="red">）
        var colorAttr = node.Attributes["color"];
        if (colorAttr != null && !string.IsNullOrWhiteSpace(colorAttr.Value))
        {
            try
            {
                // 解析颜色值（支持颜色名、十六进制、RGB/RGBA）
                var colorBrush = DocUtils.ParseColorToBrush(colorAttr.Value);
                if (colorBrush != null)
                {
                    span.Foreground = colorBrush;
                }
            }
            catch
            {
                // 颜色解析失败时不修改前景色
            }
        }
        // 其次解析style中的color（如<font style="color: blue;">）
        else
        {
            var styleColor = DocUtils.GetForegroundColor(node);
            if (styleColor != null)
            {
                span.Foreground = styleColor;
            }
        }
    }

    /// <summary>
    /// 解析<font>的size属性并应用到CSpan
    /// 支持两种格式：1. 数字（1-7，对应相对字号）；2. 带单位的像素值（如12px、16pt）
    /// </summary>
    private static double? ReadFontSize(HtmlNode node)
    {
        var sizeAttr = node.Attributes["size"];
        if (sizeAttr == null || string.IsNullOrWhiteSpace(sizeAttr.Value))
        {
            return DocUtils.GetFontSize(node);
        }

        var sizeValue = sizeAttr.Value.Trim().ToLower();
        try
        {
            double fontSize;
            // 情况1：纯数字（HTML标准的<font> size属性，1-7对应相对字号）
            if (int.TryParse(sizeValue, out var sizeNum))
            {
                // 将1-7的数字映射为具体像素值（可根据需求调整映射关系）
                fontSize = sizeNum switch
                {
                    1 => 8,
                    2 => 10,
                    3 => 12, // 默认字号
                    4 => 14,
                    5 => 18,
                    6 => 24,
                    7 => 32,
                    _ => 12 // 超出范围用默认值
                };
            }
            // 情况2：带单位的字号（如12px、16pt、2em）
            else if (sizeValue.EndsWith("px"))
            {
                fontSize = double.Parse(sizeValue.Replace("px", ""), CultureInfo.InvariantCulture);
            }
            else if (sizeValue.EndsWith("pt"))
            {
                // 1pt = 1.333px（近似转换）
                fontSize = double.Parse(sizeValue.Replace("pt", ""), CultureInfo.InvariantCulture) * 1.333;
            }
            else if (sizeValue.EndsWith("em"))
            {
                // 1em = 当前默认字号（12px）
                fontSize = double.Parse(sizeValue.Replace("em", ""), CultureInfo.InvariantCulture) * 12;
            }
            else
            {
                // 不支持的单位，返回默认值
                return null;
            }

            // 设置字体大小（限制最小值为8px，避免过小）
            return Math.Max(fontSize, 8);
        }
        catch
        {
            // 字号解析失败时不修改字体大小
            return null;
        }
    }

    private static FontFamily? ReadFontFamily(HtmlNode node)
    {
        var face = node.Attributes["face"]?.Value;
        if (string.IsNullOrWhiteSpace(face))
            return DocUtils.GetFontFamily(node);

        var familyName = face.Trim().Trim('\'', '"');
        try
        {
            return new FontFamily(familyName);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyTypographyToChildren(
        IEnumerable<CInline> inlines,
        double? fontSize,
        FontFamily? fontFamily)
    {
        foreach (var inline in inlines)
        {
            // Nested font/style tags keep their explicitly assigned values.
            if (fontSize.HasValue && !inline.IsSet(CInline.FontSizeProperty))
                inline.FontSize = fontSize.Value;
            if (fontFamily is not null && !inline.IsSet(CInline.FontFamilyProperty))
                inline.FontFamily = fontFamily;

            if (inline is CSpan span)
                ApplyTypographyToChildren(span.Content, fontSize, fontFamily);
        }
    }

    /// <summary>
    /// 解析对齐样式并应用到文本容器
    /// 注：CSpan为行内元素，对齐需通过外层CTextBlock实现
    /// </summary>
    private void ApplyAlignment(HtmlNode node, CSpan span, ReplaceManager manager)
    {
        var alignment = DocUtils.GetHorizontalAlignment(node);
        if (!alignment.HasValue)
        {
            return;
        }

        // 行内元素本身无法设置文本对齐，需将CSpan包装到CTextBlock中
        var textBlock = new CTextBlock
        {
            TextAlignment = alignment.Value switch
            {
                HorizontalAlignment.Left => TextAlignment.Left,
                HorizontalAlignment.Right => TextAlignment.Right,
                HorizontalAlignment.Center => TextAlignment.Center,
                _ => TextAlignment.Left
            },
            HorizontalAlignment = alignment.Value
        };
        textBlock.Content.Add(span);

        // 替换原有span为包装后的CTextBlock（需通过父容器重新生成）
        // 若需更灵活的对齐处理，可根据实际场景调整容器类型
    }
}
