using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    /// <summary>
    /// Bounded fallback for a single block that is too large to safely expand
    /// into a full visual tree.
    /// </summary>
    public sealed class LargeRawTextElement : DocumentElement
    {
        private readonly string _text;
        private readonly Lazy<TextBox> _control;

        public LargeRawTextElement(string text)
        {
            _text = text;
            _control = new Lazy<TextBox>(CreateControl);
        }

        public override Control Control => _control.Value;
        public override IEnumerable<DocumentElement> Children => Array.Empty<DocumentElement>();

        private TextBox CreateControl()
        {
            var textBox = new TextBox
            {
                Text = _text,
                AcceptsReturn = true,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                MaxHeight = 600,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent
            };
            textBox.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            textBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            return textBox;
        }

        protected override void BuildContentString(StringBuilder sb)
        {
            sb.Append(_text);
        }

        public override void Select(Point from, Point to)
        {
        }

        public override void UnSelect()
        {
            if (_control.IsValueCreated)
                _control.Value.ClearSelection();
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            if (_control.IsValueCreated)
                builder.Append(_control.Value.SelectedText);
        }
    }
}
