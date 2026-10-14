using Avalonia;
using Avalonia.Controls.Documents;
using ColorTextBlock.Avalonia;
using HtmlAgilityPack;
using Markdown.Avalonia.Html.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace Markdown.Avalonia.Html.Core.Parsers;

public class TextNodeParser : IInlineTagParser
{
    public IEnumerable<string> SupportTag => [HtmlNode.HtmlNodeTypeNameText];

    bool ITagParser.TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<StyledElement> generated)
    {
        var rtn = TryReplace(node, manager, out var list);
        generated = list;
        return rtn;
    }

    public bool TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<CInline> generated)
    {
        if (node is HtmlTextNode textNode)
        {
            generated = Replace(textNode.Text, manager);
            return true;
        }

        generated = EnumerableExt.Empty<CInline>();
        return false;
    }

    public IEnumerable<CInline> Replace(string text, ReplaceManager manager)
    {
        var processedText = HtmlTextNormalizer.NormalizeSourceWhitespace(text);

        // 如果处理后为空，返回空列表
        if (string.IsNullOrEmpty(processedText))
        {
            return EnumerableExt.Empty<CInline>();
        }

        // A whitespace-only node between inline HTML elements still occupies
        // one collapsed HTML space. A non-breaking space prevents the custom
        // text formatter from discarding that standalone run.
        if (string.IsNullOrWhiteSpace(processedText))
        {
            return [new CRun { Text = "\u00A0" }];
        }

        // 使用Markdown引擎处理文本
        var inlines = manager.Engine.RunSpanGamut(processedText).ToArray();
        HtmlTextNormalizer.DecodeEntities(inlines);
        return inlines;
    }
}
