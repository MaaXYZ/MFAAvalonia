using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ColorDocument.Avalonia.DocumentElements
{
    /// <summary>
    /// GitHub-style alert type enumeration
    /// </summary>
    public enum AlertType
    {
        Note,
        Abstract,
        Info,
        Todo,
        Tip,
        Important,
        Success,
        Question,
        Warning,
        Caution,
        Failure,
        Danger,
        Bug,
        Example,
        Quote
    }

    /// <summary>
    /// The document element for GitHub-style alert blocks (e.g., [!TIP], [!NOTE], [!WARNING], etc.)
    /// </summary>
    public class AlertBlockElement : DocumentElement
    {
        private Lazy<Border> _block;
        private EnumerableEx<DocumentElement> _children;
        private SelectionList? _prevSelection;
        private AlertType _alertType;
        private readonly string? _title;

        public override Control Control => _block.Value;
        public override IEnumerable<DocumentElement> Children => _children;
        public AlertType AlertType => _alertType;
        public string Title => _title ?? GetDefaultTitle(_alertType);

        public AlertBlockElement(IEnumerable<DocumentElement> child, AlertType alertType, string? title = null)
        {
            _alertType = alertType;
            _title = title;
            _block = new Lazy<Border>(Create);
            _children = child.ToEnumerable();
        }

        private Border Create()
        {
            // Create icon path
            var iconPath = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 8, 0)
            };

            // Set icon and color based on alert type
            string iconData;
            string colorHex;
            string alertTitle;
            string alertClassName;

            switch (_alertType)
            {
                case AlertType.Abstract:
                    iconData = "M4 4h16v2H4V4zm0 5h16v2H4V9zm0 5h10v2H4v-2zm0 5h10v2H4v-2z";
                    colorHex = "#00a4b4";
                    alertTitle = "Abstract";
                    alertClassName = ClassNames.AlertAbstractClass;
                    break;
                case AlertType.Info:
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z";
                    colorHex = "#0969da";
                    alertTitle = "Info";
                    alertClassName = ClassNames.AlertInfoClass;
                    break;
                case AlertType.Todo:
                    iconData = "M19 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-9 14-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
                    colorHex = "#0969da";
                    alertTitle = "Todo";
                    alertClassName = ClassNames.AlertTodoClass;
                    break;
                case AlertType.Tip:
                    // Lightbulb icon
                    iconData = "M12 2C8.13 2 5 5.13 5 9c0 2.38 1.19 4.47 3 5.74V17c0 .55.45 1 1 1h6c.55 0 1-.45 1-1v-2.26c1.81-1.27 3-3.36 3-5.74 0-3.87-3.13-7-7-7zM9 21c0 .55.45 1 1 1h4c.55 0 1-.45 1-1v-1H9v1z";
                    colorHex = "#1a7f37";
                    alertTitle = "Tip";
                    alertClassName = ClassNames.AlertTipClass;
                    break;
                case AlertType.Important:
                    // Exclamation mark in circle icon
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z";
                    colorHex = "#8250df";
                    alertTitle = "Important";
                    alertClassName = ClassNames.AlertImportantClass;
                    break;
                case AlertType.Success:
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z";
                    colorHex = "#1a7f37";
                    alertTitle = "Success";
                    alertClassName = ClassNames.AlertSuccessClass;
                    break;
                case AlertType.Question:
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 17h-2v-2h2v2zm2.07-7.25-.9.92C13.45 13.4 13 14 13 15h-2v-.5c0-.8.45-1.55 1.17-2.27l1.24-1.26A2 2 0 0014 9.5a2 2 0 10-4 0H8a4 4 0 118 0c0 .88-.36 1.68-.93 2.25z";
                    colorHex = "#9a6700";
                    alertTitle = "Question";
                    alertClassName = ClassNames.AlertQuestionClass;
                    break;
                case AlertType.Warning:
                    // Warning triangle icon
                    iconData = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z";
                    colorHex = "#9a6700";
                    alertTitle = "Warning";
                    alertClassName = ClassNames.AlertWarningClass;
                    break;
                case AlertType.Caution:
                    // Stop/danger icon
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm5 11H7v-2h10v2z";
                    colorHex = "#cf222e";
                    alertTitle = "Caution";
                    alertClassName = ClassNames.AlertCautionClass;
                    break;
                case AlertType.Failure:
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm4.3 14.3L14.9 17.7 12 14.8l-2.9 2.9-1.4-1.4 2.9-2.9-2.9-2.9 1.4-1.4 2.9 2.9 2.9-2.9 1.4 1.4-2.9 2.9 2.9 2.9z";
                    colorHex = "#cf222e";
                    alertTitle = "Failure";
                    alertClassName = ClassNames.AlertFailureClass;
                    break;
                case AlertType.Danger:
                    iconData = "M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z";
                    colorHex = "#b42318";
                    alertTitle = "Danger";
                    alertClassName = ClassNames.AlertDangerClass;
                    break;
                case AlertType.Bug:
                    iconData = "M20 8h-2.81a5.985 5.985 0 00-1.82-1.96L17 4.41 15.59 3l-2.17 2.17A6.4 6.4 0 0012 5c-.5 0-.97.06-1.42.17L8.41 3 7 4.41l1.62 1.63A5.985 5.985 0 006.81 8H4v2h2.09c-.05.33-.09.66-.09 1v1H4v2h2v1c0 .34.04.67.09 1H4v2h2.81A6 6 0 0018 15v-1h2v-2h-2v-1c0-.34-.04-.67-.09-1H20V8z";
                    colorHex = "#cf222e";
                    alertTitle = "Bug";
                    alertClassName = ClassNames.AlertBugClass;
                    break;
                case AlertType.Example:
                    iconData = "M4 4h16v2H4V4zm0 7h16v2H4v-2zm0 7h16v2H4v-2z";
                    colorHex = "#8250df";
                    alertTitle = "Example";
                    alertClassName = ClassNames.AlertExampleClass;
                    break;
                case AlertType.Quote:
                    iconData = "M7 17h4l2-4V7H6v6h4l-3 4zm8 0h4l2-4V7h-7v6h4l-3 4z";
                    colorHex = "#6e7781";
                    alertTitle = "Quote";
                    alertClassName = ClassNames.AlertQuoteClass;
                    break;
                case AlertType.Note:
                default:
                    // Info icon
                    iconData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z";
                    colorHex = "#0969da";
                    alertTitle = "Note";
                    alertClassName = ClassNames.AlertNoteClass;
                    break;
            }

            var alertColor = Color.Parse(colorHex);
            var alertBrush = new SolidColorBrush(alertColor);

            iconPath.Data = Geometry.Parse(iconData);
            iconPath.Fill = alertBrush;

            // Create title text
            var titleText = new TextBlock
            {
                Text = _title ?? alertTitle,
                FontWeight = FontWeight.SemiBold,
                Foreground = alertBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };

            // Create header panel (icon + title)
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            headerPanel.Children.Add(iconPath);
            headerPanel.Children.Add(titleText);

            // Create content panel
            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Vertical
            };
            foreach (var child in Children)
                contentPanel.Children.Add(child.Control);

            // Create main panel
            var mainPanel = new StackPanel
            {
                Orientation = Orientation.Vertical
            };
            mainPanel.Classes.Add(alertClassName);
            mainPanel.Children.Add(headerPanel);
            mainPanel.Children.Add(contentPanel);

            // Create border
            var border = new Border
            {
                BorderBrush = alertBrush,
                BorderThickness = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 8, 0, 8),
                Child = mainPanel
            };
            border.Classes.Add(alertClassName);

            return border;
        }

        private static string GetDefaultTitle(AlertType alertType)
            => alertType.ToString();

        public override void Select(Point from, Point to)
        {
            var selection = SelectionUtil.SelectVertical(Control, _children, from, to);

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
            foreach (var child in _children)
                child.UnSelect();
        }

        public override void ConstructSelectedText(StringBuilder builder)
        {
            if (_prevSelection is null)
                return;

            var preLen = builder.Length;

            foreach (var para in _prevSelection)
            {
                para.ConstructSelectedText(builder);

                if (preLen == builder.Length)
                    continue;

                if (builder[builder.Length - 1] != '\n')
                    builder.Append('\n');
            }
        }
    }
}
