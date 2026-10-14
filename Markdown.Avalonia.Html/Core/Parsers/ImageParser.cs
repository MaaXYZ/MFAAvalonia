using Avalonia;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Utils;
using HtmlAgilityPack;
using Markdown.Avalonia.Html.Core.Utils;
using Markdown.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Markdown.Avalonia.Html.Core.Parsers
{
    public class ImageParser(SetupInfo setupInfo) : IInlineTagParser
    {
        
        public IEnumerable<string> SupportTag => new[]
        {
            "img",
            "image"
        };

        bool ITagParser.TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<StyledElement> generated)
        {
            var rtn = TryReplace(node, manager, out var list);
            generated = list;
            return rtn;
        }

        public bool TryReplace(HtmlNode node, ReplaceManager manager, out IEnumerable<CInline> generated)
        {
            var link = node.Attributes["src"]?.Value;
            var alt = node.Attributes["alt"]?.Value;
            if (link is null)
            {
                generated = EnumerableExt.Empty<CInline>();
                return false;
            }
            var title = node.Attributes["title"]?.Value;
            var widthTxt = node.Attributes["width"]?.Value;
            var heightTxt = node.Attributes["height"]?.Value;


            CImage image = setupInfo.LoadImage(link);
            
            image.ClickCommand = new ImageOpenCommand();
            image.ClickCommandParameter = image;
            
            if (!String.IsNullOrEmpty(title)
                && title.All(char.IsLetterOrDigit))
            {
                image.Classes.Add(title);
            }


            ApplyDimensions(image, widthTxt, heightTxt);

            generated =
            [
                image
            ];
            return true;
        }

        internal static void ApplyDimensions(CImage image, string? widthText, string? heightText)
        {
            if (Length.TryParse(heightText, out var height) && height.Unit != Unit.Percentage)
            {
                image.LayoutHeight = height.ToPoint();
            }

            if (!Length.TryParse(widthText, out var width))
                return;

            if (width.Unit == Unit.Percentage)
            {
                // CImage measures RelativeWidth against the complete text line.
                // It is an inline StyledElement, so a visual-ancestor binding
                // is invalid while the document is being built.
                image.RelativeWidth = Math.Clamp(width.Value / 100d, 0d, 1d);
            }
            else
            {
                image.LayoutWidth = width.ToPoint();
            }
        }
    }
}
