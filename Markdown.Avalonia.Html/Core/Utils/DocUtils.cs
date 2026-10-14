using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using ColorTextBlock.Avalonia;
using HtmlAgilityPack;
using Markdown.Avalonia.SyntaxHigh;
using Markdown.Avalonia.SyntaxHigh.Extensions;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Markdown.Avalonia.Html.Core.Utils
{
    static class DocUtils
    {
        public static HorizontalAlignment? GetHorizontalAlignment(HtmlNode node)
        {
            // 优先解析 align 属性（如 <p align="center">）
            var alignAttr = node.Attributes["align"];
            if (alignAttr != null)
            {
                return alignAttr.Value.ToLower() switch
                {
                    "left" => HorizontalAlignment.Left,
                    "right" => HorizontalAlignment.Right,
                    "center" => HorizontalAlignment.Center,
                    _ => null
                };
            }

            // 解析 style 中的 text-align（如 <div style="text-align: center;">）
            var styleAttr = node.Attributes["style"];
            if (styleAttr != null)
            {
                var match = Regex.Match(styleAttr.Value, @"text-align\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value.ToLower() switch
                    {
                        "left" => HorizontalAlignment.Left,
                        "right" => HorizontalAlignment.Right,
                        "center" => HorizontalAlignment.Center,
                        _ => null
                    };
                }
            }

            return null;
        }

        public static Brush? GetForegroundColor(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"];
            if (styleAttr == null) return null;

            // 匹配color样式（使用否定回顾断言排除 background-color）
            // 支持 color: red; / color:#fff; / color: rgb(255,0,0); 等
            var colorMatch = Regex.Match(
                styleAttr.Value,
                @"(?<![\w-])color\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!colorMatch.Success) return null;

            string colorValue = colorMatch.Groups[1].Value.Trim();
            try
            {
                // 解析颜色值为Avalonia的Brush
                return ParseColorToBrush(colorValue);
            }
            catch
            {
                // 解析失败时返回null
                return null;
            }
        }

        // 内部方法：将颜色字符串转为SolidColorBrush
        public static SolidColorBrush ParseColorToBrush(string colorValue)
        {
            // 处理颜色名（如 red、blue）
            if (Color.TryParse(colorValue, out var namedColor))
            {
                return new SolidColorBrush(namedColor);
            }

            // 处理十六进制（如 #f00、#ff0000、#ff0000ff）
            if (colorValue.StartsWith("#"))
            {
                string hex = colorValue.TrimStart('#');
                // 补全短十六进制（#f00 → #ff0000）
                hex = hex.Length switch
                {
                    3 => string.Concat(hex.Select(c => $"{c}{c}")),
                    4 => string.Concat(hex.Select(c => $"{c}{c}")), // #rgba → #rrggbbaa
                    _ => hex
                };
                // 转换为Color
                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var hexValue))
                {
                    Color color = Color.FromUInt32(hexValue);
                    return new SolidColorBrush(color);
                }
            }

            // 处理RGB（rgb(255,0,0)）和RGBA（rgba(255,0,0,0.5)）
            var rgbMatch = Regex.Match(
                colorValue,
                @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([0-9.]+)\s*)?\)",
                RegexOptions.IgnoreCase
            );
            if (rgbMatch.Success)
            {
                byte r = byte.Parse(rgbMatch.Groups[1].Value);
                byte g = byte.Parse(rgbMatch.Groups[2].Value);
                byte b = byte.Parse(rgbMatch.Groups[3].Value);
                float a = rgbMatch.Groups[4].Success ? float.Parse(rgbMatch.Groups[4].Value) : 1f;

                // 限制透明度范围0-1
                a = Math.Clamp(a, 0f, 1f);
                Color color = Color.FromArgb((byte)(a * 255), r, g, b);
                return new SolidColorBrush(color);
            }

            // 解析失败抛出异常
            throw new FormatException($"Unsupported color format: {colorValue}");
        }

        /// <summary>
        /// 从HTML节点的style属性中提取并解析字体大小
        /// 支持格式：font-size: 12px; / font-size: 1.5em; / font-size: 14pt; / font-size: large; 等
        /// </summary>
        public static double? GetFontSize(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"];
            if (styleAttr == null) return null;

            // 匹配font-size样式（忽略大小写）
            var fontSizeMatch = Regex.Match(
                styleAttr.Value,
                @"font-size\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!fontSizeMatch.Success) return null;

            string fontSizeValue = fontSizeMatch.Groups[1].Value.Trim();
            try
            {
                return ParseFontSizeToPixels(fontSizeValue);
            }
            catch
            {
                // 解析失败时返回null
                return null;
            }
        }

        /// <summary>
        /// 将CSS字体大小值转换为Avalonia可用的像素值
        /// </summary>
        private static double ParseFontSizeToPixels(string fontSizeValue)
        {
            // 移除所有空白字符（处理类似 "12 px" 这种不规范格式）
            var cleaned = Regex.Replace(fontSizeValue, @"\s+", "");

            // 匹配数值+单位（px/em/pt）
            var unitMatch = Regex.Match(cleaned, @"^(\d+(\.\d+)?)(px|em|pt)$", RegexOptions.IgnoreCase);
            if (unitMatch.Success)
            {
                double value = double.Parse(unitMatch.Groups[1].Value);
                string unit = unitMatch.Groups[3].Value.ToLower();

                return unit switch
                {
                    "px" => value, // 像素直接使用
                    "em" => value * 16, // 假设1em = 16px（默认字体大小）
                    "pt" => value * 1.333, // 1pt ≈ 1.333px
                    _ => value // 未知单位默认按px处理
                };
            }

            // 匹配相对关键字（small/medium/large等）
            return fontSizeValue.ToLower() switch
            {
                "xx-small" => 10,
                "x-small" => 12,
                "small" => 14,
                "medium" => 16, // 默认字体大小
                "large" => 18,
                "x-large" => 24,
                "xx-large" => 32,
                "smaller" => 14, // 相对小一号（假设基于medium）
                "larger" => 18, // 相对大一号（假设基于medium）
                _ => throw new ArgumentException($"不支持的字体大小格式: {fontSizeValue}")
            };
        }
        /// <summary>
        /// 从HTML节点的style属性中提取并解析字体粗细
        /// 支持格式：font-weight: bold; / font-weight: 700; 等（仅识别bold相关值）
        /// </summary>
        /// <returns>返回FontWeight.Bold（匹配时），否则返回null</returns>
        public static FontWeight? GetFontWeight(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"];
            if (styleAttr == null) return null;

            // 匹配font-weight样式（忽略大小写）
            var fontWeightMatch = Regex.Match(
                styleAttr.Value,
                @"font-weight\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!fontWeightMatch.Success) return null;

            string fontWeightValue = fontWeightMatch.Groups[1].Value.Trim().ToLower();
            try
            {
                // 识别bold关键字或700（CSS中700对应bold）
                if (fontWeightValue == "bold" || fontWeightValue == "700")
                {
                    return FontWeight.Bold;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
        /// <summary>
        /// 从HTML节点的style属性中提取并解析字体样式
        /// 支持格式：font-style: italic; 等（仅识别italic）
        /// </summary>
        /// <returns>返回FontStyle.Italic（匹配时），否则返回null</returns>
        public static FontStyle? GetFontStyle(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"];
            if (styleAttr == null) return null;

            // 匹配font-style样式（忽略大小写）
            var fontStyleMatch = Regex.Match(
                styleAttr.Value,
                @"font-style\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!fontStyleMatch.Success) return null;

            string fontStyleValue = fontStyleMatch.Groups[1].Value.Trim().ToLower();
            try
            {
                // 识别italic关键字
                if (fontStyleValue == "italic")
                {
                    return FontStyle.Italic;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
        /// <summary>
        /// 从HTML节点的style属性中提取并解析文本装饰（删除线/下划线）
        /// 支持格式：text-decoration: line-through; / text-decoration: underline; / 同时包含两者等
        /// </summary>
        /// <returns>返回包含删除线和下划线状态的匿名对象，未匹配时对应属性为false</returns>
        public static (bool IsStrikethrough, bool IsUnderline) GetTextDecoration(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"];
            if (styleAttr == null) return (false, false);

            // 匹配text-decoration样式（忽略大小写）
            var textDecorationMatch = Regex.Match(
                styleAttr.Value,
                @"text-decoration\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!textDecorationMatch.Success) return (false, false);

            string decorationValue = textDecorationMatch.Groups[1].Value.Trim().ToLower();
            try
            {
                // 检查是否包含删除线（line-through）和下划线（underline）
                bool isStrikethrough = decorationValue.Contains("line-through");
                bool isUnderline = decorationValue.Contains("underline");
                return (isStrikethrough, isUnderline);
            }
            catch
            {
                return (false, false);
            }
        }
        
        /// <summary>
        /// 从HTML节点的style属性提取字体家族（font-family）
        /// 支持格式：font-family: monospace; / font-family: 'Arial'; / font-family: "Times New Roman";
        /// </summary>
        public static FontFamily? GetFontFamily(HtmlNode node)
        {
            var styleAttr = node.Attributes["style"]?.Value;
            if (string.IsNullOrEmpty(styleAttr)) return null;

            // 匹配font-family（忽略大小写，支持带引号/不带引号的值）
            var match = Regex.Match(
                styleAttr,
                @"font-family\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!match.Success) return null;

            string fontFamilyValue = match.Groups[1].Value.Trim()
                .Replace("'", "") // 去除单引号
                .Replace("\"", ""); // 去除双引号

            try
            {
                return new FontFamily(fontFamilyValue);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从HTML节点的style属性提取背景色（background-color）
        /// 支持格式：background-color: black; / background-color: #000; / background-color: rgb(0,0,0);
        /// </summary>
        public static Brush? GetBackgroundColor(HtmlNode node)
        {
            var bgcolor = node.Attributes["bgcolor"]?.Value;
            if (!string.IsNullOrWhiteSpace(bgcolor))
            {
                try
                {
                    return ParseColorToBrush(bgcolor.Trim());
                }
                catch
                {
                    // Fall through to the CSS declaration when the legacy
                    // attribute contains an invalid color.
                }
            }

            var styleAttr = node.Attributes["style"]?.Value;
            if (string.IsNullOrEmpty(styleAttr)) return null;

            // 匹配background-color（忽略大小写）
            var match = Regex.Match(
                styleAttr,
                @"background-color\s*:\s*([^;]+)",
                RegexOptions.IgnoreCase
            );
            if (!match.Success) return null;

            string colorValue = match.Groups[1].Value.Trim();
            try
            {
                // 复用颜色解析逻辑（和GetForegroundColor一致）
                return ParseColorToBrush(colorValue);
            }
            catch
            {
                return null;
            }
        }
        
        public static Control CreateCodeBlock(string? lang, string code, ReplaceManager manager, SyntaxHighlightProvider provider)
        {
            var txtEdit = new TextEditor();

            if (!String.IsNullOrEmpty(lang))
            {
                txtEdit.Tag = lang;
                txtEdit.SetValue(SyntaxHighlightWrapperExtension.ProviderProperty, provider);
            }

            txtEdit.Text = code;
            txtEdit.HorizontalAlignment = HorizontalAlignment.Stretch;
            txtEdit.IsReadOnly = true;

            var result = new Border();
            result.Classes.Add(Tags.TagCodeBlock.GetClass());
            result.Child = txtEdit;

            return result;
        }

        public static void TrimStart(CInline? inline)
        {
            if (inline is null) return;

            if (inline is CSpan span)
            {
                TrimStart(span.Content.FirstOrDefault());
            }
            else if (inline is CRun run)
            {
                run.Text = run.Text.TrimStart();
            }
        }

        public static void TrimEnd(CInline? inline)
        {
            if (inline is null) return;

            if (inline is CSpan span)
            {
                TrimEnd(span.Content.LastOrDefault());
            }
            else if (inline is CRun run)
            {
                run.Text = run.Text.TrimEnd();
            }
        }
    }
}
