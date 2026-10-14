using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ColorTextBlock.Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    public class CTextBlockElement : DocumentElement
    {
        private Lazy<CTextBlock> _text;
        private readonly string _contentString;
        private readonly string _plainText;
        private readonly string? _appendClass;
        private readonly TextAlignment? _alignment;

        public string Text => _text.IsValueCreated ? _text.Value.Text : _plainText;

        public override Control Control => _text.Value;

        public override IEnumerable<DocumentElement> Children => Array.Empty<DocumentElement>();

        public CTextBlockElement(IEnumerable<CInline> inlines)
        {
            var inlineList = inlines.ToList();
            _contentString = BuildInlinesString(inlineList);
            _plainText = string.Concat(inlineList.Select(inline => inline.AsString()));
            _appendClass = null;
            _alignment = null;
            _text = new Lazy<CTextBlock>(() =>
            {
                var text = new CTextBlock();
                foreach (var inline in inlineList)
                    text.Content.Add(inline);
                return text;
            });
        }

        public CTextBlockElement(IEnumerable<CInline> inlines, string appendClass)
        {
            var inlineList = inlines.ToList();
            _contentString = BuildInlinesString(inlineList);
            _plainText = string.Concat(inlineList.Select(inline => inline.AsString()));
            _appendClass = appendClass;
            _alignment = null;

            _text = new Lazy<CTextBlock>(() =>
            {
                var text = new CTextBlock();
                foreach (var inline in inlineList)
                    text.Content.Add(inline);

                text.Classes.Add(appendClass);
                return text;
            });
        }

        public CTextBlockElement(IEnumerable<CInline> inlines, string appendClass, TextAlignment alignment)
        {
            var inlineList = inlines.ToList();
            _contentString = BuildInlinesString(inlineList);
            _plainText = string.Concat(inlineList.Select(inline => inline.AsString()));
            _appendClass = appendClass;
            _alignment = alignment;

            _text = new Lazy<CTextBlock>(() =>
            {
                var text = new CTextBlock();
                foreach (var inline in inlineList)
                    text.Content.Add(inline);

                text.TextAlignment = alignment;
                text.Classes.Add(appendClass);
                return text;
            });
        }

        private static string BuildInlinesString(IEnumerable<CInline> inlines)
        {
            var sb = new StringBuilder();
            foreach (var inline in inlines)
            {
                sb.Append(inline.GetType().Name);
                sb.Append(':');
                sb.Append(inline.AsString());
                sb.Append('|');
            }
            return sb.ToString();
        }

        protected override void BuildContentString(StringBuilder sb)
        {
            sb.Append(_contentString);
            if (_appendClass != null)
            {
                sb.Append("[class:");
                sb.Append(_appendClass);
                sb.Append(']');
            }
            if (_alignment.HasValue)
            {
                sb.Append("[align:");
                sb.Append(_alignment.Value);
                sb.Append(']');
            }
        }


        public override void Select(Point from, Point to)
        {
            var text = _text.Value;

            var fromPoint = text.CalcuatePointerFrom(from.X, from.Y);
            var toPoint = text.CalcuatePointerFrom(to.X, to.Y);
            text.Select(fromPoint, toPoint);
        }

        public override void UnSelect()
        {
            _text.Value.ClearSelection();
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            builder.Append(_text.Value.GetSelectedText());
        }
    }
}
