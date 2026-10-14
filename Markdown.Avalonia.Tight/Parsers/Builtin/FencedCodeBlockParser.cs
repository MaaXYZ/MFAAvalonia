using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;

namespace Markdown.Avalonia.Parsers.Builtin
{
    internal class FencedCodeBlockParser : BlockParser2
    {
        private static readonly Regex _codeBlockBegin = new(@"
                    ^          # Character before opening
                    [ ]{0,3}
                    (`{3,})          # $1 = Opening run of `
                    ([^\n`]*)        # $2 = The code lang
                    \n", RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline | RegexOptions.Compiled);

        private bool _enablePreRenderingCodeBlock;

        public FencedCodeBlockParser(bool enablePreRenderingCodeBlock) : base(_codeBlockBegin, "CodeBlocksWithLangEvaluator")
        {
            _enablePreRenderingCodeBlock = enablePreRenderingCodeBlock;
        }

        public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status, IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd)
        {
            // 优化：使用字符串搜索替代动态正则，避免每次创建新 Regex 对象
            var backticks = firstMatch.Groups[1].Value;
            var (found, closeIndex, closeEndIndex) = FindCloseTag(text, firstMatch.Index + firstMatch.Length, backticks);

            int codeEndIndex;
            if (found)
            {
                codeEndIndex = closeIndex;
                parseTextEnd = closeEndIndex;
            }
            else if (_enablePreRenderingCodeBlock)
            {
                codeEndIndex = text.Length;
                parseTextEnd = text.Length;
            }
            else
            {
                parseTextBegin = parseTextEnd = -1;
                return null;
            }

            parseTextBegin = firstMatch.Index;

            string code = text.Substring(firstMatch.Index + firstMatch.Length, codeEndIndex - (firstMatch.Index + firstMatch.Length));
            var border = Create(code);
            return new[] { new UnBlockElement(border, "FencedCode:" + code) };
        }

        /// <summary>
        /// 查找代码块的结束标记（使用字符串搜索替代动态正则）
        /// </summary>
        /// <returns>(是否找到, 结束标记起始位置, 结束标记结束位置)</returns>
        private static (bool Found, int Index, int EndIndex) FindCloseTag(string text, int startIndex, string backticks)
        {
            int index = startIndex;
            while (index < text.Length)
            {
                // 查找换行符
                int newlineIndex = text.IndexOf('\n', index);
                if (newlineIndex == -1) break;

                // 检查换行符后是否是空格+反引号
                int lineStart = newlineIndex + 1;
                int checkIndex = lineStart;

                // 跳过前导空格
                while (checkIndex < text.Length && text[checkIndex] == ' ')
                    checkIndex++;

                // 检查是否匹配反引号
                if (checkIndex + backticks.Length <= text.Length)
                {
                    bool match = true;
                    for (int i = 0; i < backticks.Length; i++)
                    {
                        if (text[checkIndex + i] != backticks[i])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        int afterBackticks = checkIndex + backticks.Length;
                        // 跳过尾部空格
                        while (afterBackticks < text.Length && text[afterBackticks] == ' ')
                            afterBackticks++;

                        // 检查是否以换行符结束
                        if (afterBackticks < text.Length && text[afterBackticks] == '\n')
                        {
                            return (true, newlineIndex, afterBackticks + 1);
                        }
                        // 文件末尾也算
                        if (afterBackticks == text.Length)
                        {
                            return (true, newlineIndex, afterBackticks);
                        }
                    }
                }

                index = newlineIndex + 1;
            }

            return (false, -1, -1);
        }

        public static Border Create(string code)
        {
            const double codeLineHeight = 20d;
            const double verticalPadding = 8d;
            var lineCount = CountLines(code);

            if (lineCount > 2000 || code.Length > 1_000_000)
            {
                var largeText = new TextBox
                {
                    Text = code,
                    AcceptsReturn = true,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxHeight = 600,
                    Padding = new Thickness(10, verticalPadding)
                };
                largeText.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                largeText.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                largeText.Classes.Add(Markdown.CodeBlockClass);

                var largeBorder = new Border { Child = largeText };
                largeBorder.Classes.Add(Markdown.CodeBlockClass);
                return largeBorder;
            }
            var ctxt = new TextBlock()
            {
                Text = code,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = codeLineHeight,
                MinHeight = lineCount * codeLineHeight
            };
            ctxt.Classes.Add(Markdown.CodeBlockClass);

            var scrl = new ScrollViewer();
            scrl.Classes.Add(Markdown.CodeBlockClass);
            scrl.Content = ctxt;
            scrl.Padding = new Thickness(10, verticalPadding);
            scrl.MinHeight = lineCount * codeLineHeight + verticalPadding * 2;
            scrl.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scrl.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            var border = new Border();
            border.Classes.Add(Markdown.CodeBlockClass);
            border.Child = scrl;

            return border;
        }

        private static int CountLines(string text)
        {
            var count = 1;
            foreach (var character in text)
            {
                if (character == '\n')
                    count++;
            }
            return count;
        }
    }
}
