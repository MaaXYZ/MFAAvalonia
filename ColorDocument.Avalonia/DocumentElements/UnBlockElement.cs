using Avalonia;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    public class UnBlockElement : DocumentElement
    {
        private Control _control;
        private readonly string? _contentKey;

        public override Control Control => _control;

        public override IEnumerable<DocumentElement> Children => Array.Empty<DocumentElement>();

        public UnBlockElement(Control control, string? contentKey = null)
        {
            _control = control;
            _contentKey = contentKey;
        }

        protected override void BuildContentString(StringBuilder sb)
        {
            sb.Append(_control.GetType().FullName);
            sb.Append('|');
            sb.Append(_contentKey);
        }

        public override void Select(Point from, Point to) { }

        public override void UnSelect() { }

        public override void ConstructSelectedText(StringBuilder stringBuilder)
        {
        }
    }
}

