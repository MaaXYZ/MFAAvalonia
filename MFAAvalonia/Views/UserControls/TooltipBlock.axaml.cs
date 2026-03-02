using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.UserControls;

public class TooltipBlock : TemplatedControl
{
    private const string ElementBorder = "PART_Border";

    private Border? _border;
    private Button? _parentButton;
    private bool _isInsideButton;
    private bool _isPointerOverBorder;

    public TooltipBlock()
    {
        Opacity = NormalOpacity;
    }

    public static readonly StyledProperty<string> TooltipTextProperty =
        AvaloniaProperty.Register<TooltipBlock, string>(nameof(TooltipText), string.Empty);

    public static readonly StyledProperty<double> TooltipMaxWidthProperty =
        AvaloniaProperty.Register<TooltipBlock, double>(nameof(TooltipMaxWidth), 500);

    public static readonly StyledProperty<double> NormalOpacityProperty =
        AvaloniaProperty.Register<TooltipBlock, double>(
            nameof(NormalOpacity),
            0.7,
            coerce: CoerceOpacity);

    public static readonly StyledProperty<double> HoverOpacityProperty =
        AvaloniaProperty.Register<TooltipBlock, double>(
            nameof(HoverOpacity),
            1.0,
            coerce: CoerceOpacity);

    public static readonly StyledProperty<int> InitialShowDelayProperty =
        AvaloniaProperty.Register<TooltipBlock, int>(nameof(InitialShowDelay), 100);

    public string TooltipText
    {
        get => GetValue(TooltipTextProperty);
        set => SetValue(TooltipTextProperty, value);
    }

    public bool TooltipTextNotEmpty => !string.IsNullOrEmpty(TooltipText);

    public double TooltipMaxWidth
    {
        get => GetValue(TooltipMaxWidthProperty);
        set => SetValue(TooltipMaxWidthProperty, value);
    }

    public double NormalOpacity
    {
        get => GetValue(NormalOpacityProperty);
        set => SetValue(NormalOpacityProperty, value);
    }

    public double HoverOpacity
    {
        get => GetValue(HoverOpacityProperty);
        set => SetValue(HoverOpacityProperty, value);
    }

    public int InitialShowDelay
    {
        get => GetValue(InitialShowDelayProperty);
        set => SetValue(InitialShowDelayProperty, value);
    }

    private static double CoerceOpacity(AvaloniaObject d, double baseValue)
    {
        if (d is TooltipBlock { IsPointerOver: false } tooltipBlock)
        {
            // 如果鼠标不在控件上，直接应用NormalOpacity
            if (tooltipBlock.NormalOpacity.Equals(baseValue))
            {
                tooltipBlock.Opacity = baseValue;
            }
        }
        return baseValue;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _border = e.NameScope.Find<Border>(ElementBorder);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 检测是否位于 Button/ToggleButton 内部
        // SukiUI 的按钮模板中 ContentPresenter 设置了 IsHitTestVisible="False"，
        // 导致内部元素无法接收 PointerEntered/PointerExited 事件。
        // 通过监听父级 Button 的 PointerMoved 事件，根据坐标判断指针是否在 PART_Border 范围内来处理。
        _parentButton = this.FindAncestorOfType<Button>();
        _isInsideButton = _parentButton != null;

        if (_isInsideButton)
        {
            _parentButton!.PointerMoved += OnParentPointerMoved;
            _parentButton.PointerExited += OnParentPointerExited;
        }
        else
        {
            PointerEntered += OnPointerEnter;
            PointerExited += OnPointerLeave;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_isInsideButton && _parentButton != null)
        {
            _parentButton.PointerMoved -= OnParentPointerMoved;
            _parentButton.PointerExited -= OnParentPointerExited;
            _parentButton = null;
        }
        else
        {
            PointerEntered -= OnPointerEnter;
            PointerExited -= OnPointerLeave;
        }

        _isPointerOverBorder = false;
        _isInsideButton = false;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 当位于 Button 内部时，通过父级 Button 的 PointerMoved 事件检测指针是否在 PART_Border 上方
    /// </summary>
    private void OnParentPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_border == null) return;

        var position = e.GetPosition(_border);
        var isOver = new Rect(0, 0, _border.Bounds.Width, _border.Bounds.Height).Contains(position);

        if (isOver && !_isPointerOverBorder)
        {
            _isPointerOverBorder = true;
            _ = AnimateOpacity(HoverOpacity);
            FlyoutBase.ShowAttachedFlyout(_border);
        }
        else if (!isOver && _isPointerOverBorder)
        {
            _isPointerOverBorder = false;
            _ = AnimateOpacity(NormalOpacity);
        }
    }

    /// <summary>
    /// 当位于 Button 内部时，指针离开父级 Button 时重置状态
    /// </summary>
    private void OnParentPointerExited(object? sender, PointerEventArgs e)
    {
        if (_isPointerOverBorder)
        {
            _isPointerOverBorder = false;
            _ = AnimateOpacity(NormalOpacity);
        }
    }

    private void OnPointerEnter(object? sender, PointerEventArgs e)
    {
        _ = AnimateOpacity(HoverOpacity);
        if (_border != null)
            FlyoutBase.ShowAttachedFlyout(_border);
    }

    private void OnPointerLeave(object? sender, PointerEventArgs e)
    {
        _ = AnimateOpacity(NormalOpacity);
    }

    async private Task AnimateOpacity(double targetOpacity)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(InitialShowDelay),
            Children =
            {
                new KeyFrame
                {
                    Setters =
                    {
                        new Setter
                        {
                            Property = OpacityProperty,
                            Value = targetOpacity
                        }
                    },
                    Cue = new Cue(1d)
                }
            }
        };

        await animation.RunAsync(this);
    }
}
