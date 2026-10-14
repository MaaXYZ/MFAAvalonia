using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using CSharpMath.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Markdown.Avalonia.Math;

public sealed class MathPlugin : IMdAvPlugin
{
    public void Setup(SetupInfo info)
    {
        info.RegisterTop(new DisplayMathParser());
        info.RegisterTop(new FencedMathParser());
        info.Register(new InlineMathParser());
    }

    private sealed class DisplayMathParser : BlockParser
    {
        private static readonly Regex DisplayMathPattern = new(
            @"^[\t ]*\$\$(?<latex>.*?)(?:\$\$)[\t ]*(?:\n|$)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

        public DisplayMathParser() : base(DisplayMathPattern, nameof(DisplayMathParser))
        {
        }

        public override IEnumerable<Control> Convert(
            string text,
            Match firstMatch,
            ParseStatus status,
            IMarkdownEngine engine,
            out int parseTextBegin,
            out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = firstMatch.Index + firstMatch.Length;

            var latex = NormalizeLatex(firstMatch.Groups["latex"].Value);
            return [CreateDisplayMath(latex)];
        }
    }

    private sealed class FencedMathParser : BlockParser
    {
        private static readonly Regex FencedMathPattern = new(
            @"^[ ]{0,3}(?<fence>`{3,})[\t ]*math[\t ]*\n(?<latex>.*?)(?:\n[ ]{0,3}\k<fence>[\t ]*(?:\n|$))",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public FencedMathParser() : base(FencedMathPattern, nameof(FencedMathParser))
        {
        }

        public override IEnumerable<Control> Convert(
            string text,
            Match firstMatch,
            ParseStatus status,
            IMarkdownEngine engine,
            out int parseTextBegin,
            out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = firstMatch.Index + firstMatch.Length;
            return [CreateDisplayMath(NormalizeLatex(firstMatch.Groups["latex"].Value))];
        }
    }

    private sealed class InlineMathParser : InlineParser
    {
        private static readonly Regex InlineMathPattern = new(
            @"(?<!\\)(?<!\$)\$(?!\$)(?<latex>.+?)(?<!\\)\$(?!\$)",
            RegexOptions.Compiled);

        public InlineMathParser() : base(InlineMathPattern, nameof(InlineMathParser))
        {
        }

        public override IEnumerable<CInline> Convert(
            string text,
            Match firstMatch,
            IMarkdownEngine engine,
            out int parseTextBegin,
            out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = firstMatch.Index + firstMatch.Length;

            var latex = NormalizeLatex(firstMatch.Groups["latex"].Value);
            return [new CInlineUIContainer(CreateMathView(latex))];
        }
    }

    private static Control CreateDisplayMath(string latex)
    {
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 8),
            Child = new Viewbox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = CreateMathView(latex, HorizontalAlignment.Center)
            }
        };
    }

    internal static string NormalizeLatex(string latex)
    {
        var normalized = latex.Trim();

        if ((normalized.StartsWith(@"\(", StringComparison.Ordinal)
             && normalized.EndsWith(@"\)", StringComparison.Ordinal))
            || (normalized.StartsWith(@"\[", StringComparison.Ordinal)
                && normalized.EndsWith(@"\]", StringComparison.Ordinal)))
        {
            normalized = normalized[2..^2].Trim();
        }

        return normalized;
    }

    private static MathView CreateMathView(
        string latex,
        HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        return new MathView
        {
            LaTeX = latex,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}
