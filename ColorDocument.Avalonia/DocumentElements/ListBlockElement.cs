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
    public class ListBlockElement : DocumentElement
    {
        private Lazy<Grid> _control;
        private EnumerableEx<ListItemElement> _items;
        private SelectionList? _prevSelection;
        private readonly int _orderedStart;

        public override Control Control => _control.Value;
        public override IEnumerable<DocumentElement> Children => _items;

        public ListBlockElement(
            TextMarkerStyle marker,
            IEnumerable<ListItemElement> items,
            int orderedStart = 1)
        {
            _orderedStart = Math.Max(1, orderedStart);
            _control = new Lazy<Grid>(() => CreateList(marker));
            _items = items.ToEnumerable();
        }

        public override void Select(Point from, Point to)
        {
            var selection = SelectionUtil.SelectVertical(Control, _items, from, to);

            if (_prevSelection is not null)
            {
                foreach (var ps in _prevSelection)
                {
                    if (!selection.Any(cs => ReferenceEquals(cs, ps)))
                    {
                        ps.UnSelect();
                    }
                }
            }

            _prevSelection = selection;
        }

        public override void UnSelect()
        {
            foreach (var c in _items)
                c.UnSelect();
        }

        private Grid CreateList(TextMarkerStyle marker)
        {
            var grid = new Grid();
            grid.Classes.Add(ClassNames.ListClass);
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            int index = 0;
            foreach (var item in _items)
            {
                var itemCtrl = item.Control;
                Control markerControl;

                if (item.TaskChecked is bool isChecked)
                {
                    var taskMarker = new CheckBox
                    {
                        IsChecked = isChecked,
                        IsEnabled = false,
                        IsHitTestVisible = false,
                        Focusable = false,
                        Width = 14,
                        Height = 14,
                        MinWidth = 0,
                        MinHeight = 0,
                        Padding = new Thickness(0),
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right
                    };
                    taskMarker.Classes.Add(ClassNames.TaskListMarkerClass);
                    markerControl = taskMarker;
                    item.MarkerText = isChecked ? "[x]" : "[ ]";
                }
                else
                {
                    var markerIndex = IsOrderedMarker(marker)
                        ? index + _orderedStart - 1
                        : index;
                    var markerTxt = new CTextBlock(marker.CreateMakerText(markerIndex));
                    item.MarkerText = markerTxt.Text;

                    if (FindFirstFrom(itemCtrl) is { } controlTxt)
                        markerTxt.ObserveBaseHeightOf(controlTxt);

                    markerTxt.TextAlignment = TextAlignment.Right;
                    markerTxt.TextWrapping = TextWrapping.NoWrap;
                    markerTxt.Classes.Add(ClassNames.ListMarkerClass);
                    markerControl = markerTxt;
                }

                grid.RowDefinitions.Add(new RowDefinition());

                Grid.SetRow(markerControl, index);
                Grid.SetColumn(markerControl, 0);
                grid.Children.Add(markerControl);

                Grid.SetRow(itemCtrl, index);
                Grid.SetColumn(itemCtrl, 1);
                grid.Children.Add(itemCtrl);

                ++index;
            }

            return grid;

            static CTextBlock? FindFirstFrom(Control ctrl)
            {
                if (ctrl is Panel pnl)
                {
                    foreach (var chld in pnl.Children)
                    {
                        var res = FindFirstFrom(chld);
                        if (res != null) return res;
                    }
                }
                if (ctrl is CTextBlock ctxt)
                {
                    return ctxt;
                }
                return null;
            }

            static bool IsOrderedMarker(TextMarkerStyle markerStyle) => markerStyle is
                TextMarkerStyle.Decimal or
                TextMarkerStyle.LowerLatin or
                TextMarkerStyle.LowerRoman or
                TextMarkerStyle.UpperLatin or
                TextMarkerStyle.UpperRoman;
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            if (_prevSelection is null)
                return;

            foreach (var para in _prevSelection.Cast<ListItemElement>())
            {
                builder.Append(para.MarkerText).Append(' ');

                var listElmTxt = para.GetSelectedText().Replace("\r\n", "\n").Replace('\r', '\n');
                builder.Append(listElmTxt);

                if (!listElmTxt.EndsWith("\n"))
                    builder.Append('\n');
            }
        }
    }
}
