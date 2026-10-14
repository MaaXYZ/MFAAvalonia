using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using System;
using System.Collections.Generic;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    public class PlainCodeBlockElement : DocumentElement
    {
        private string _code;
        private Lazy<Border> _border;

        public override Control Control => _border.Value;

        public override IEnumerable<DocumentElement> Children => Array.Empty<DocumentElement>();

        public PlainCodeBlockElement(string code)
        {
            _code = code;
            _border = new Lazy<Border>(CreateBlock);
        }

        public override void Select(Point from, Point to)
        {
        }

        public override void UnSelect()
        {
        }

        public Border CreateBlock()
        {
            const double codeLineHeight = 20d;
            const double verticalPadding = 8d;
            var normalizedCode = _code
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .TrimEnd('\n');
            var lineCount = CountLines(normalizedCode);

            if (lineCount > 2000 || normalizedCode.Length > 1_000_000)
                return CreateLargeBlock(normalizedCode);

            var ctxt = new TextBlock()
            {
                Text = normalizedCode,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = codeLineHeight,
                MinHeight = lineCount * codeLineHeight
            };
            ctxt.Classes.Add(ClassNames.CodeBlockClass);

            var scrl = new ScrollViewer
            {
                Content = ctxt,
                Padding = new Thickness(10, verticalPadding),
                MinHeight = lineCount * codeLineHeight + verticalPadding * 2,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            scrl.Classes.Add(ClassNames.CodeBlockClass);

            var result = new Border
            {
                Child = scrl
            };
            result.Classes.Add(ClassNames.CodeBlockClass);

            return result;
        }

        private static Border CreateLargeBlock(string code)
        {
            var text = new TextBox
            {
                Text = code,
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                MaxHeight = 600,
                Padding = new Thickness(10, 8)
            };
            text.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            text.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            text.Classes.Add(ClassNames.CodeBlockClass);

            var result = new Border { Child = text };
            result.Classes.Add(ClassNames.CodeBlockClass);
            return result;
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

        public override void ConstructSelectedText(StringBuilder stringBuilder)
        {
            stringBuilder.Append(_code);
        }
    }
}
