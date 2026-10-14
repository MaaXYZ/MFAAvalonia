using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Views.UserControls;

public class TooltipBlock : TemplatedControl
{
    private const string ElementBorder = "PART_Border";

    private Border? _border;
    private FlyoutBase? _attachedFlyout;
    private Control? _flyoutContentControl;
    private TopLevel? _topLevel;
    private Button? _parentButton;
    private bool _isInsideButton;
    private bool _isPointerOverBorder;
    private bool _isPointerOverFlyout;
    private bool _androidTooltipOpen;
    private CancellationTokenSource? _closeDelayCts;

    private const int CloseDelayMs = 300;

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

        UnhookFlyoutEvents();
        _border = e.NameScope.Find<Border>(ElementBorder);
        if (_border != null)
            HookFlyoutEvents(_border);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _topLevel.PointerMoved += OnTopLevelPointerMoved;
            _topLevel.PointerExited += OnTopLevelPointerExited;
            if (OperatingSystem.IsAndroid())
                _topLevel.AddHandler(
                    PointerPressedEvent,
                    OnAndroidTopLevelPointerPressed,
                    RoutingStrategies.Tunnel,
                    handledEventsToo: true);
        }

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
        if (_topLevel != null)
        {
            _topLevel.PointerMoved -= OnTopLevelPointerMoved;
            _topLevel.PointerExited -= OnTopLevelPointerExited;
            if (OperatingSystem.IsAndroid())
                _topLevel.RemoveHandler(PointerPressedEvent, OnAndroidTopLevelPointerPressed);
            _topLevel = null;
        }

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
        _isPointerOverFlyout = false;
        _isInsideButton = false;
        CancelPendingClose();
        UnhookFlyoutEvents();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 当位于 Button 内部时，通过父级 Button 的 PointerMoved 事件检测指针是否在 PART_Border 上方
    /// </summary>
    private void OnParentPointerMoved(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (_border == null) return;

        var position = e.GetPosition(_border);
        var isOver = new Rect(0, 0, _border.Bounds.Width, _border.Bounds.Height).Contains(position);

        if (isOver && !_isPointerOverBorder)
        {
            _isPointerOverBorder = true;
            CancelPendingClose();
            _ = AnimateOpacity(HoverOpacity);
            ShowFlyout();
        }
        else if (!isOver && _isPointerOverBorder)
        {
            _isPointerOverBorder = false;
            UpdateOpacityByState();
            _ = TryCloseFlyoutWithDelayAsync();
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
            UpdateOpacityByState();
            _ = TryCloseFlyoutWithDelayAsync();
        }
    }

    private void OnPointerEnter(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
            return;
        _isPointerOverBorder = true;
        CancelPendingClose();
        _ = AnimateOpacity(HoverOpacity);
        ShowFlyout();
    }

    private void OnPointerLeave(object? sender, PointerEventArgs e)
    {
        if (IsPointerEventStillInsideBorder(e))
            return;

        _isPointerOverBorder = false;
        UpdateOpacityByState();
        _ = TryCloseFlyoutWithDelayAsync();
    }

    private void OnTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (sender is not TopLevel topLevel)
            return;

        var wasActive = _isPointerOverBorder || _isPointerOverFlyout;

        _isPointerOverBorder = IsPointerInControlScreenRegion(_border, topLevel, e);
        _isPointerOverFlyout = IsPointerInControlScreenRegion(_flyoutContentControl, topLevel, e);

        if (_isPointerOverBorder || _isPointerOverFlyout)
        {
            CancelPendingClose();
            if (_isPointerOverBorder)
                ShowFlyout();

            if (!wasActive)
                _ = AnimateOpacity(HoverOpacity);
            return;
        }

        if (wasActive)
        {
            UpdateOpacityByState();
            _ = TryCloseFlyoutWithDelayAsync();
        }
    }

    private void OnTopLevelPointerExited(object? sender, PointerEventArgs e)
    {
        if (OperatingSystem.IsAndroid())
            return;
        if (!_isPointerOverBorder && !_isPointerOverFlyout)
            return;

        _isPointerOverBorder = false;
        _isPointerOverFlyout = false;
        UpdateOpacityByState();
        _ = TryCloseFlyoutWithDelayAsync();
    }

    private bool IsPointerEventStillInsideBorder(PointerEventArgs e)
    {
        if (_border == null || _border.Bounds.Width <= 0 || _border.Bounds.Height <= 0)
            return false;

        var position = e.GetPosition(_border);
        return new Rect(0, 0, _border.Bounds.Width, _border.Bounds.Height).Contains(position);
    }

    private bool IsPointerInControlScreenRegion(Control? control, TopLevel topLevel, PointerEventArgs e)
    {
        if (control == null || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return false;

        if (control.GetVisualRoot() is not TopLevel controlTopLevel)
            return false;

        var pointerInTopLevel = e.GetPosition(topLevel);
        var pointerScreen = topLevel.PointToScreen(pointerInTopLevel);

        PixelPoint controlTopLeft;
        try
        {
            controlTopLeft = control.PointToScreen(default);
        }
        catch (ArgumentException)
        {
            // 控件可能在该帧已脱离视觉树，按未命中处理
            return false;
        }

        var controlScaling = controlTopLevel.RenderScaling;

        var controlRect = new Rect(
            controlTopLeft.X,
            controlTopLeft.Y,
            control.Bounds.Width * controlScaling,
            control.Bounds.Height * controlScaling);

        return controlRect.Contains(new Point(pointerScreen.X, pointerScreen.Y));
    }

    private void HookFlyoutEvents(Border border)
    {
        _attachedFlyout = FlyoutBase.GetAttachedFlyout(border);
        if (_attachedFlyout == null)
            return;

        _attachedFlyout.Opened += OnFlyoutOpened;
        _attachedFlyout.Closed += OnFlyoutClosed;

        if (_attachedFlyout is Flyout { Content: Control contentControl })
        {
            _flyoutContentControl = contentControl;
            _flyoutContentControl.PointerEntered += OnFlyoutPointerEntered;
            _flyoutContentControl.PointerExited += OnFlyoutPointerExited;
        }
    }

    private void UnhookFlyoutEvents()
    {
        if (_attachedFlyout != null)
        {
            _attachedFlyout.Opened -= OnFlyoutOpened;
            _attachedFlyout.Closed -= OnFlyoutClosed;
            _attachedFlyout = null;
        }

        if (_flyoutContentControl != null)
        {
            _flyoutContentControl.PointerEntered -= OnFlyoutPointerEntered;
            _flyoutContentControl.PointerExited -= OnFlyoutPointerExited;
            _flyoutContentControl = null;
        }
    }

    private void OnFlyoutOpened(object? sender, EventArgs e)
    {
        CancelPendingClose();
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        _androidTooltipOpen = false;
        _isPointerOverFlyout = false;
        if (!_isPointerOverBorder)
            _ = AnimateOpacity(NormalOpacity);
    }

    private void OnFlyoutPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerOverFlyout = true;
        CancelPendingClose();
        _ = AnimateOpacity(HoverOpacity);
    }

    private void OnFlyoutPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerOverFlyout = false;
        UpdateOpacityByState();
        _ = TryCloseFlyoutWithDelayAsync();
    }

    private void ShowFlyout()
    {
        if (_border == null)
            return;

        FlyoutBase.ShowAttachedFlyout(_border);
    }

    private void OnAndroidTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsAndroid() || sender is not TopLevel topLevel)
            return;

        var isInside = IsPointerInControlScreenRegion(_border, topLevel, e);
        if (isInside)
        {
            if (_androidTooltipOpen)
            {
                _attachedFlyout?.Hide();
                return;
            }

            _isPointerOverBorder = true;
            _androidTooltipOpen = true;
            CancelPendingClose();
            _ = AnimateOpacity(HoverOpacity);
            ShowFlyout();
            return;
        }

        // Touch has no hover exit event. Treat a tap outside both the indicator and its
        // flyout as an explicit dismissal while preserving scrolling inside the content.
        if (_androidTooltipOpen && !IsPointerInControlScreenRegion(_flyoutContentControl, topLevel, e))
            _attachedFlyout?.Hide();
    }

    private void CancelPendingClose()
    {
        _closeDelayCts?.Cancel();
        _closeDelayCts?.Dispose();
        _closeDelayCts = null;
    }

    private void UpdateOpacityByState()
    {
        _ = AnimateOpacity(_isPointerOverBorder || _isPointerOverFlyout ? HoverOpacity : NormalOpacity);
    }

    private async Task TryCloseFlyoutWithDelayAsync()
    {
        if (_attachedFlyout == null)
            return;

        CancelPendingClose();
        _closeDelayCts = new CancellationTokenSource();
        var token = _closeDelayCts.Token;

        try
        {
            await Task.Delay(CloseDelayMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _isPointerOverBorder || _isPointerOverFlyout)
            return;

        _attachedFlyout.Hide();
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
