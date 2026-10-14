using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    /// <summary>
    /// A block whose semantic/visual tree is created only when the block enters
    /// the viewport. The rendered tree may be discarded and recreated later.
    /// </summary>
    public class DeferredBlockElement : DocumentElement
    {
        private readonly Func<IReadOnlyList<DocumentElement>> _factory;
        private readonly string _contentKey;
        private IReadOnlyList<DocumentElement>? _renderedElements;
        private Control? _control;
        private SelectionList? _previousSelection;

        public DeferredBlockElement(
            Func<IReadOnlyList<DocumentElement>> factory,
            string contentKey)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _contentKey = contentKey ?? string.Empty;
        }

        public bool IsRealized => _control is not null;

        public override Control Control
        {
            get
            {
                EnsureRealized();
                return _control!;
            }
        }

        public override IEnumerable<DocumentElement> Children =>
            _renderedElements ?? Array.Empty<DocumentElement>();

        private void EnsureRealized()
        {
            if (_control is not null)
                return;

            var elements = _factory() ?? Array.Empty<DocumentElement>();
            _renderedElements = elements;
            foreach (var element in elements)
                element.Helper = Helper;

            if (elements.Count == 1)
            {
                _control = elements[0].Control;
                return;
            }

            var panel = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var element in elements)
                panel.Children.Add(element.Control);
            _control = panel;
        }

        public override void ReleaseControl()
        {
            if (_control is null)
                return;

            if (_control.Parent is Panel parentPanel)
                parentPanel.Children.Remove(_control);
            else if (_control.Parent is ContentControl contentControl)
                contentControl.Content = null;
            else if (_control.Parent is Decorator decorator)
                decorator.Child = null;

            if (_renderedElements is not null)
            {
                foreach (var element in _renderedElements)
                {
                    element.Helper = null;
                    element.ReleaseControl();
                }
            }

            _previousSelection = null;
            _renderedElements = null;
            _control = null;
        }

        protected override void BuildContentString(StringBuilder sb)
        {
            sb.Append(_contentKey);
        }

        public override void Select(Point from, Point to)
        {
            EnsureRealized();
            var elements = _renderedElements ?? Array.Empty<DocumentElement>();
            var selection = SelectionUtil.SelectVertical(
                Control,
                elements.ToEnumerable(),
                from,
                to);

            if (_previousSelection is not null)
            {
                foreach (var previous in _previousSelection)
                {
                    if (!selection.Any(current => ReferenceEquals(current, previous)))
                        previous.UnSelect();
                }
            }

            _previousSelection = selection;
        }

        public override void UnSelect()
        {
            if (_renderedElements is null)
                return;

            foreach (var element in _renderedElements)
                element.UnSelect();
            _previousSelection = null;
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            if (_previousSelection is null)
                return;

            foreach (var element in _previousSelection)
            {
                var previousLength = builder.Length;
                element.ConstructSelectedText(builder);
                if (builder.Length > previousLength && builder[^1] != '\n')
                    builder.Append('\n');
            }
        }
    }

    public sealed class DeferredHeadingElement : DeferredBlockElement, IDocumentHeading
    {
        public DeferredHeadingElement(
            Func<IReadOnlyList<DocumentElement>> factory,
            string contentKey,
            int level,
            string text)
            : base(factory, contentKey)
        {
            Level = level;
            Text = text;
        }

        public int Level { get; }
        public string Text { get; }
    }
}
