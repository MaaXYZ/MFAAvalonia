using HtmlAgilityPack;
using ColorTextBlock.Avalonia;
using System.Collections.Generic;

namespace Markdown.Avalonia.Html.Core.Utils;

internal static class HtmlTextNormalizer
{
    public static string NormalizeSourceWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // HTML collapses source line breaks in normal text. Keep all other
        // whitespace intact. Entities are decoded after Markdown parsing so
        // &lt; and &ast; cannot accidentally become Markdown syntax.
        return text
            .TrimStart('\r', '\n')
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    public static void DecodeEntities(IEnumerable<CInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case CRun run:
                    run.Text = HtmlEntity.DeEntitize(run.Text);
                    break;

                case CSpan span:
                    DecodeEntities(span.Content);
                    break;
            }
        }
    }
}
