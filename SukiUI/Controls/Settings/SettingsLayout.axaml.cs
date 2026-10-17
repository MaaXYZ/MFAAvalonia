using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Avalonia.Styling;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SukiUI.Controls;

public partial class SettingsLayout : ItemsControl, ISukiStackPageTitleProvider
{
    static SettingsLayout()
    {
        ItemsSourceProperty.OverrideMetadata<SettingsLayout>(new StyledPropertyMetadata<IEnumerable>
            (new List<SettingsLayout>(), BindingMode.TwoWay, EnforceItemType));
    }

    private static IEnumerable EnforceItemType(AvaloniaObject instance, IEnumerable value)
    {
        if (value is IEnumerable items)
        {
            var validItems = items.OfType<SettingsLayoutItem>().ToList();
            if (validItems.Count != items.Cast<object>().Count())
                throw new InvalidOperationException("The type of item must be SettingsLayoutItem");
            return validItems;
        }
        return value;
    }

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingsLayout, string>(nameof(Title), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DirectProperty<SettingsLayout, double> MinWidthWhetherStackShowProperty =
        AvaloniaProperty.RegisterDirect<SettingsLayout, double>(
            nameof(MinWidthWhetherStackSummaryShow), o => o.MinWidthWhetherStackSummaryShow,
            (o, v) => o.MinWidthWhetherStackSummaryShow = v, 1100);

    public static readonly StyledProperty<double> StackSummaryWidthProperty =
        AvaloniaProperty.Register<SettingsLayout, double>(nameof(StackSummaryWidth), 400);

    private ScrollViewer? _stackSummaryScrollTop;
    private Button? _stackSummaryTopScrollLeftButton;
    private Button? _stackSummaryTopScrollRightButton;
    private Control? _stackSummaryTopContainer;

    /// <summary>
    /// 顶部分类导航横向拖动的启动阈值，单位为逻辑像素。
    /// 位移小于该值时仍按点击处理，保证分类跳转不受影响。
    /// </summary>
    private const double TopSummaryDragThreshold = 4.0;

    private IPointer? _topSummaryDragPointer;
    private bool _topSummaryPointerCaptured;
    private bool _isTopSummaryDragging;
    private bool _suppressTopSummaryClick;
    private Point _topSummaryDragStartPoint;
    private double _topSummaryDragStartOffsetX;

    public SettingsLayout()
    {
        InitializeComponent();
    }

    public static readonly StyledProperty<double> ScrollAnimationSpeedProperty =
        AvaloniaProperty.Register<SettingsLayout, double>(
            nameof(ScrollAnimationSpeed),
            1.0,
            validate: ValidateSpeed);

    private static bool ValidateSpeed(double speed) => speed > 0;

    public double ScrollAnimationSpeed
    {
        get => GetValue(ScrollAnimationSpeedProperty);
        set => SetValue(ScrollAnimationSpeedProperty, Math.Max(value, 0.1));
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private double _minWidthWhetherStackSummaryShow = 1100;

    /// <summary>
    /// Get or set a value that represents the minimum width for displaying the StackSummary in the SettingsLayout.
    /// If the width of the SettingsLayout is less than this value, the StackSummary will not be displayed.
    /// The default value is 1100, and the minimum configurable value is 1.
    /// </summary>
    public double MinWidthWhetherStackSummaryShow
    {
        get => _minWidthWhetherStackSummaryShow;
        set
        {
            if (value < 1)
            {
                return;
            }
            SetAndRaise(MinWidthWhetherStackShowProperty, ref _minWidthWhetherStackSummaryShow, value);
        }
    }

    /// <summary>
    /// Get or set the width of the StackSummary. The default value is 400, and the minimum configurable value is 0.
    /// </summary>
    public double StackSummaryWidth
    {
        get => GetValue(StackSummaryWidthProperty);
        set
        {
            if (value < 0)
            {
                return;
            }
            SetValue(StackSummaryWidthProperty, value);
        }
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        UpdateItems();

        if (_stackSummaryScrollTop != null)
        {
            _stackSummaryScrollTop.PointerWheelChanged -= OnTopSummaryWheelChanged;
            _stackSummaryScrollTop.ScrollChanged -= OnTopSummaryScrollChanged;
            _stackSummaryScrollTop.RemoveHandler(InputElement.PointerPressedEvent, OnTopSummaryPointerPressed);
            _stackSummaryScrollTop.RemoveHandler(InputElement.PointerMovedEvent, OnTopSummaryPointerMoved);
            _stackSummaryScrollTop.RemoveHandler(InputElement.PointerReleasedEvent, OnTopSummaryPointerReleased);
            _stackSummaryScrollTop.RemoveHandler(InputElement.PointerCaptureLostEvent, OnTopSummaryPointerCaptureLost);
            EndTopSummaryDrag();
        }

        if (_stackSummaryTopScrollLeftButton != null)
        {
            _stackSummaryTopScrollLeftButton.Click -= OnTopSummaryScrollLeftClick;
        }

        if (_stackSummaryTopScrollRightButton != null)
        {
            _stackSummaryTopScrollRightButton.Click -= OnTopSummaryScrollRightClick;
        }

        _stackSummaryScrollTop = e.NameScope.Find<ScrollViewer>("StackSummaryScrollTop");
        _stackSummaryTopScrollLeftButton = e.NameScope.Find<Button>("StackSummaryTopScrollLeftButton");
        _stackSummaryTopScrollRightButton = e.NameScope.Find<Button>("StackSummaryTopScrollRightButton");
        _stackSummaryTopContainer = e.NameScope.Find<Control>("StackSummaryTopContainer");

        if (_stackSummaryScrollTop != null)
        {
            _stackSummaryScrollTop.PointerWheelChanged += OnTopSummaryWheelChanged;
            _stackSummaryScrollTop.ScrollChanged += OnTopSummaryScrollChanged;
            // 分类按钮会处理指针事件，因此需接收已处理事件，确保拖动逻辑能观察完整手势
            _stackSummaryScrollTop.AddHandler(InputElement.PointerPressedEvent, OnTopSummaryPointerPressed,
                Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _stackSummaryScrollTop.AddHandler(InputElement.PointerMovedEvent, OnTopSummaryPointerMoved,
                Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _stackSummaryScrollTop.AddHandler(InputElement.PointerReleasedEvent, OnTopSummaryPointerReleased,
                Avalonia.Interactivity.RoutingStrategies.Bubble, true);
            _stackSummaryScrollTop.AddHandler(InputElement.PointerCaptureLostEvent, OnTopSummaryPointerCaptureLost,
                Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        }

        if (_stackSummaryTopScrollLeftButton != null)
        {
            _stackSummaryTopScrollLeftButton.Click += OnTopSummaryScrollLeftClick;
        }

        if (_stackSummaryTopScrollRightButton != null)
        {
            _stackSummaryTopScrollRightButton.Click += OnTopSummaryScrollRightClick;
        }

        UpdateTopSummaryFade();
    }

    private void UpdateItems()
    {
        if (Items is null || !Items.Any()) return;

        var stackSummaryScroll = this.GetTemplateChildren().First(n => n.Name == "StackSummaryScroll") as ScrollViewer;
        var stackSummaryScrollTop = this.GetTemplateChildren().First(n => n.Name == "StackSummaryScrollTop") as ScrollViewer;
        if (stackSummaryScroll is not ScrollViewer || stackSummaryScrollTop is not ScrollViewer)
            return;
        var stackSummary = stackSummaryScroll.Content as StackPanel;
        var stackSummaryTop = stackSummaryScrollTop.Content as StackPanel;
        var myScroll = this.GetTemplateChildren().First(n => n.Name == "MyScroll") as ScrollViewer;

        if (myScroll?.Content is not StackPanel stackItems)
            return;

        if (stackSummary is not StackPanel || stackSummaryTop is not StackPanel)
            return;

        var radios = new List<RadioButton>();
        var borders = new List<Border>();

        stackItems.Children.Add(new Border()
        {
            Height = 8
        });

        foreach (var item in Items.OfType<SettingsLayoutItem>().Where(x => x.Header != null))
        {
            var header = new TextBlock
            {
                FontSize = 17
            };
            var content = new TextBlock
            {
                FontSize = 17
            };
            var contentTop = new TextBlock
            {
                FontSize = 18
            };
            header.Bind(TextBlock.TextProperty, new Binding(nameof(SettingsLayoutItem.Header))
            {
                Source = item
            });
            content.Bind(TextBlock.TextProperty, new Binding(nameof(SettingsLayoutItem.Header))
            {
                Source = item
            });
            contentTop.Bind(TextBlock.TextProperty, new Binding(nameof(SettingsLayoutItem.Header))
            {
                Source = item
            });
            var border = new Border
            {
                Child = new GroupBox
                {
                    Margin = new Thickness(10, 20),
                    Header = header,
                    Content = new Border
                    {
                        Margin = new Thickness(35, 12),
                        Child = item.Content
                    }
                }
            };

            borders.Add(border);
            stackItems.Children.Add(border);

            var summaryButton = new RadioButton
            {
                Content = content,
                Classes =
                {
                    "MenuChip"
                }
            };

            var summaryButtonTop = new RadioButton
            {
                Content = contentTop,
                Classes =
                {
                    "MenuChipTop"
                }
            };

            summaryButton.Click += async (sender, args) =>
            {
                if (isAnimatingScroll)
                    return;
                var x = border.TranslatePoint(new Point(), stackItems);

                if (x.HasValue)
                    await AnimateScroll(x.Value.Y);
            };

            summaryButtonTop.Click += async (sender, args) =>
            {
                if (_suppressTopSummaryClick)
                {
                    _suppressTopSummaryClick = false;
                    return;
                }

                if (isAnimatingScroll)
                    return;
                var x = border.TranslatePoint(new Point(), stackItems);

                if (x.HasValue)
                    await AnimateScroll(x.Value.Y);
            };

            radios.Add(summaryButton);
            stackSummary.Children.Add(summaryButton);
            stackSummaryTop.Children.Add(summaryButtonTop);
        }

        myScroll.ScrollChanged += (sender, args) =>
        {
            if (isAnimatingScroll)
                return;

            if (borders.Count == 0 || radios.Count == 0)
                return;

            var OffsetY = myScroll.Offset.Y;

            var l = borders.Select(b =>
            {
                var point = b.TranslatePoint(new Point(), stackItems);
                return point.HasValue ? Math.Abs(point.Value.Y - OffsetY) : double.MaxValue;
            }).ToList();

            var minValue = l.Min();
            var minIndex = l.IndexOf(minValue);

            if (minIndex >= 0 && minIndex < radios.Count)
            {
                radios[minIndex].IsChecked = true;
                if (stackSummaryTop.Children.Count > minIndex && stackSummaryTop.Children[minIndex] is RadioButton topRadio)
                {
                    topRadio.IsChecked = true;
                    topRadio.BringIntoView();
                }
            }
        };
    }

    private void OnTopSummaryWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_stackSummaryScrollTop == null)
        {
            return;
        }

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        ScrollTopSummaryBy(-Math.Sign(delta) * 30.0);
        e.Handled = true;
    }

    private void OnTopSummaryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateTopSummaryFade();
    }

    private void OnTopSummaryScrollLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollTopSummaryBy(-120.0);
    }

    private void OnTopSummaryScrollRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollTopSummaryBy(120.0);
    }

    private void ScrollTopSummaryBy(double delta)
    {
        if (_stackSummaryScrollTop == null)
        {
            return;
        }

        var offset = _stackSummaryScrollTop.Offset;
        var maxX = _stackSummaryScrollTop.ScrollBarMaximum.X;
        var nextX = Math.Clamp(offset.X + delta, 0, maxX);
        _stackSummaryScrollTop.Offset = offset.WithX(nextX);
        UpdateTopSummaryFade();
    }

    private void OnTopSummaryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_stackSummaryScrollTop == null)
        {
            return;
        }

        _suppressTopSummaryClick = false;
        _isTopSummaryDragging = false;

        if (!e.GetCurrentPoint(_stackSummaryScrollTop).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _topSummaryDragStartPoint = e.GetPosition(_stackSummaryScrollTop);
        _topSummaryDragStartOffsetX = _stackSummaryScrollTop.Offset.X;
    }

    private void OnTopSummaryPointerMoved(object? sender, PointerEventArgs e)
    {
        var scrollTop = _stackSummaryScrollTop;
        if (scrollTop == null || !e.GetCurrentPoint(scrollTop).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(scrollTop);
        var horizontalDelta = position.X - _topSummaryDragStartPoint.X;

        if (!_isTopSummaryDragging)
        {
            if (Math.Abs(horizontalDelta) < TopSummaryDragThreshold
                || Math.Abs(horizontalDelta) <= Math.Abs(position.Y - _topSummaryDragStartPoint.Y)
                || scrollTop.ScrollBarMaximum.X <= 0.5)
            {
                return;
            }

            _topSummaryDragPointer = e.Pointer;
            e.Pointer.Capture(scrollTop);
            _topSummaryPointerCaptured = e.Pointer.Captured == scrollTop;
            _isTopSummaryDragging = true;
            _suppressTopSummaryClick = true;
        }

        if (!_topSummaryPointerCaptured)
        {
            return;
        }

        var maxX = scrollTop.ScrollBarMaximum.X;
        var nextX = Math.Clamp(_topSummaryDragStartOffsetX - horizontalDelta, 0, maxX);
        scrollTop.Offset = scrollTop.Offset.WithX(nextX);
        UpdateTopSummaryFade();
        e.Handled = true;
    }

    private void OnTopSummaryPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasDragging = _isTopSummaryDragging;
        EndTopSummaryDrag();

        if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private void OnTopSummaryPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _topSummaryDragPointer = null;
        _topSummaryPointerCaptured = false;
        _isTopSummaryDragging = false;
    }

    private void EndTopSummaryDrag()
    {
        _topSummaryDragPointer?.Capture(null);
        _topSummaryDragPointer = null;
        _topSummaryPointerCaptured = false;
        _isTopSummaryDragging = false;
    }

    private void UpdateTopSummaryFade()
    {
        if (_stackSummaryScrollTop == null || _stackSummaryTopScrollLeftButton == null || _stackSummaryTopScrollRightButton == null)
        {
            return;
        }

        if (_stackSummaryTopContainer is { IsVisible: false })
        {
            _stackSummaryTopScrollLeftButton.IsVisible = false;
            _stackSummaryTopScrollRightButton.IsVisible = false;
            return;
        }

        var maxX = _stackSummaryScrollTop.ScrollBarMaximum.X;
        var offsetX = _stackSummaryScrollTop.Offset.X;

        _stackSummaryTopScrollLeftButton.IsVisible = maxX > 0.5 && offsetX > 0.5;
        _stackSummaryTopScrollRightButton.IsVisible = maxX > 0.5 && offsetX < maxX - 0.5;
    }

    private double LastDesiredSize = -1;

    private void DockPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stack = this.GetTemplateChildren().First(n => n.Name == "StackSummaryScroll");
        var stackTopContainer = this.GetTemplateChildren().First(n => n.Name == "StackSummaryTopContainer");
        var desiredSize = e.NewSize.Width > MinWidthWhetherStackSummaryShow ? StackSummaryWidth : 0;

        if (stackTopContainer is Control topContainer)
        {
            topContainer.IsVisible = desiredSize == 0;
            if (topContainer.IsVisible)
            {
                UpdateTopSummaryFade();
            }
        }

        if (LastDesiredSize == desiredSize)
            return;

        LastDesiredSize = desiredSize;

        if (stack.Width != desiredSize && (stack.Width == 0 || stack.Width == StackSummaryWidth))
            stack.Animate<double>(WidthProperty, stack.Width, desiredSize, TimeSpan.FromMilliseconds(800));
    }

    private bool isAnimatingScroll = false;

    private async Task AnimateScroll(double desiredScroll)
    {
        isAnimatingScroll = true;
        var myscroll = (ScrollViewer)this.GetTemplateChildren().First(n => n.Name == "MyScroll");

        var validatedSpeed = Math.Max(0.1, Math.Min(ScrollAnimationSpeed, 10.0));

        var startOffset = myscroll.Offset;
        var endOffset = new Vector(startOffset.X, Math.Max(desiredScroll - 30, 0));

        var animationTask = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(800 / validatedSpeed),
            FillMode = FillMode.Forward,
            Easing = new CubicEaseInOut(),
            IterationCount = new IterationCount(1),
            PlaybackDirection = PlaybackDirection.Normal,
            Children =
            {
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromMilliseconds(0),
                    Setters =
                    {
                        new Setter(ScrollViewer.OffsetProperty, startOffset)
                    }
                },
                new KeyFrame
                {
                    KeyTime = TimeSpan.FromMilliseconds(800 / validatedSpeed),
                    Setters =
                    {
                        new Setter(ScrollViewer.OffsetProperty, endOffset)
                    }
                }
            }
        }.RunAsync(myscroll);

        var abortTask = Task.Run(async () =>
        {
            await Task.Delay(Convert.ToInt32(850 / validatedSpeed));
            isAnimatingScroll = false;
        });

        await Task.WhenAll(animationTask, abortTask);
    }
}
