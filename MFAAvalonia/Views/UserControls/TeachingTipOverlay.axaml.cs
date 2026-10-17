using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MFAAvalonia.Views.UserControls;

public partial class TeachingTipOverlay : UserControl
{
    private int _currentStep;
    private List<TutorialStep>? _steps;
    private Border? _tipBorder;
    private Border? _overlayMask;
    private TextBlock? _titleBlock;
    private TextBlock? _descBlock;
    private Button? _prevButton;
    private Button? _nextButton;
    private Button? _closeButton;
    private Canvas? _overlayCanvas;
    private Polygon? _arrow;
    private Grid? _overlayRoot;
    private DispatcherTimer? _trackingTimer;
    private Rect _lastTargetBounds;
    private Size _lastTipSize;
    private int _loggedStep = -1;
    private Rect _lastLoggedBounds;
    private int _loggedCountForStep;
    private int _scrollRequestsForStep;
    private DateTime _lastScrollRequestAt = DateTime.MinValue;

    /// <summary>
    /// Indicates whether a tutorial is currently running.
    /// Used to prevent re-triggering while active.
    /// </summary>
    public bool IsRunning { get; private set; }

    public TeachingTipOverlay()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _overlayRoot = this.FindControl<Grid>("OverlayRoot");
        _overlayCanvas = this.FindControl<Canvas>("OverlayCanvas");
        _overlayMask = this.FindControl<Border>("OverlayMask");
        _tipBorder = this.FindControl<Border>("TipBorder");
        _titleBlock = this.FindControl<TextBlock>("TipTitle");
        _descBlock = this.FindControl<TextBlock>("TipDescription");
        _prevButton = this.FindControl<Button>("PrevButton");
        _nextButton = this.FindControl<Button>("NextButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _arrow = this.FindControl<Polygon>("TipArrow");

        if (_prevButton != null) _prevButton.Click += (_, _) => GoToPreviousStep();
        if (_nextButton != null) _nextButton.Click += (_, _) => GoToNextStep();
        if (_closeButton != null) _closeButton.Click += (_, _) => CloseTutorial();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        StopTracking();
    }

    public void StartTutorial(List<TutorialStep> steps)
    {
        if (steps == null || steps.Count == 0) return;
        if (IsRunning) return; // Prevent re-triggering while active
        _steps = steps;
        _currentStep = 0;
        IsRunning = true;
        IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            ShowCurrentStep();
            StartTracking();
        }, DispatcherPriority.Loaded);
    }

    private void StartTracking()
    {
        if (_trackingTimer != null) return;
        _trackingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _trackingTimer.Tick += OnTrackingTick;
        _trackingTimer.Start();
    }

    private void StopTracking()
    {
        if (_trackingTimer == null) return;
        _trackingTimer.Stop();
        _trackingTimer.Tick -= OnTrackingTick;
        _trackingTimer = null;
    }
    private void OnTrackingTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _steps == null || _currentStep < 0 || _currentStep >= _steps.Count)
            return;

        var step = _steps[_currentStep];
        var targets = ResolveTargets(step);
        if (targets == null)
        {
            ClearCutout();
            return;
        }

        if (_overlayRoot == null || _tipBorder == null) return;

        try
        {
            var tb = GetTargetsBoundsInOverlay(targets);
            if (tb == null)
            {
                ClearCutout();
                return;
            }

            var inflated = tb.Value.Inflate(step.CutoutPadding);

            var overlayBounds = new Rect(0, 0, _overlayRoot.Bounds.Width, _overlayRoot.Bounds.Height);
            if (!overlayBounds.Intersects(inflated))
            {
                ClearCutout();
                RequestScrollIfNeeded(targets, tb.Value);
                return;
            }

            RequestScrollIfNeeded(targets, tb.Value);

            // Check if tip size changed (content may have been laid out since last tick)
            _tipBorder.InvalidateMeasure();
            _tipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var currentTipSize = _tipBorder.DesiredSize;
            bool tipSizeChanged = Math.Abs(currentTipSize.Width - _lastTipSize.Width) > 1 || Math.Abs(currentTipSize.Height - _lastTipSize.Height) > 1;

            bool targetMoved = Math.Abs(inflated.X - _lastTargetBounds.X) > 1
                || Math.Abs(inflated.Y - _lastTargetBounds.Y) > 1
                || Math.Abs(inflated.Width - _lastTargetBounds.Width) > 1
                || Math.Abs(inflated.Height - _lastTargetBounds.Height) > 1;

            if (!targetMoved && !tipSizeChanged)
                return;

            PositionTipAtTarget(step);
        }
        catch
        {
            // Ignore tracking errors
        }

    }

    private void ShowCurrentStep()
    {
        if (_steps == null || _currentStep < 0 || _currentStep >= _steps.Count) return;

        var step = _steps[_currentStep];

        if (_titleBlock != null)
            _titleBlock.Text = step.TitleKey.ToLocalization();
        if (_descBlock != null)
            _descBlock.Text = step.DescriptionKey.ToLocalization();

        if (_prevButton != null)
        {
            _prevButton.IsVisible = _currentStep > 0;
            _prevButton.Content = LangKeys.TutorialPrevious.ToLocalization();
        }

        if (_nextButton != null)
        {
            bool isLast = _currentStep == _steps.Count - 1;
            _nextButton.Content = isLast
                ? LangKeys.TutorialFinish.ToLocalization()
                : LangKeys.TutorialNext.ToLocalization();
        }

        // Reset last tip size so tracking timer will re-position after layout settles
        _lastTipSize = default;
        _loggedStep = -1;
        _scrollRequestsForStep = 0;
        _lastTargetBounds = default;

        // Defer positioning to after layout pass so tip has correct DesiredSize
        Dispatcher.UIThread.Post(() => PositionTipAtTarget(step), DispatcherPriority.Render);
    }

    /// <summary>
    /// Transforms target control bounds into overlay-local coordinates using TopLevel as intermediate.
    /// </summary>
    private Rect? GetTargetBoundsInOverlay(Control target)
    {
        if (_overlayRoot == null)
            return null;

        if (target.Bounds.Width < 0.5 || target.Bounds.Height < 0.5)
            return null;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var targetToWindow = target.TransformToVisual((Visual)topLevel);
        if (targetToWindow == null) return null;

        var overlayToWindow = _overlayRoot.TransformToVisual((Visual)topLevel);
        if (overlayToWindow == null) return null;

        var targetBounds = new Rect(0, 0, target.Bounds.Width, target.Bounds.Height);
        var tbInWindow = targetBounds.TransformToAABB(targetToWindow.Value);

        var overlayOrigin = new Point(0, 0).Transform(overlayToWindow.Value);

        return new Rect(
            tbInWindow.X - overlayOrigin.X,
            tbInWindow.Y - overlayOrigin.Y,
            tbInWindow.Width,
            tbInWindow.Height);
    }

    /// <summary>读取步骤目标；多目标时高亮它们外接矩形的并集。</summary>
    private static List<Control>? ResolveTargets(TutorialStep step)
    {
        var controls = new List<Control>();

        if (step.FindTargets != null)
        {
            foreach (var control in step.FindTargets())
            {
                if (control != null)
                    controls.Add(control);
            }
        }
        else if (step.FindTarget?.Invoke() is { } single)
        {
            controls.Add(single);
        }

        return controls.Count > 0 ? controls : null;
    }

    private Rect? GetTargetsBoundsInOverlay(IReadOnlyList<Control> targets)
    {
        Rect? merged = null;
        foreach (var target in targets)
        {
            var bounds = GetTargetBoundsInOverlay(target);
            if (bounds == null) continue;
            merged = merged == null ? bounds : merged.Value.Union(bounds.Value);
        }

        return merged;
    }

    private void RequestScrollIfNeeded(IReadOnlyList<Control> targets, Rect targetBounds)
    {
        if (_overlayRoot == null)
            return;

        var overlayBounds = new Rect(0, 0, _overlayRoot.Bounds.Width, _overlayRoot.Bounds.Height);
        if (overlayBounds.Contains(targetBounds))
        {
            _scrollRequestsForStep = 0;
            return;
        }

        if (_scrollRequestsForStep >= 20
            || (DateTime.UtcNow - _lastScrollRequestAt).TotalMilliseconds < 300)
        {
            return;
        }

        _lastScrollRequestAt = DateTime.UtcNow;
        _scrollRequestsForStep++;

        foreach (var target in targets)
        {
            try
            {
                target.BringIntoView();
            }
            catch
            {
                // 控件脱离视觉树时忽略滚动请求失败。
            }
        }
    }

    private void ClearCutout()
    {
        _lastTargetBounds = default;
        if (_arrow != null)
            _arrow.IsVisible = false;
        if (_overlayMask == null || _overlayRoot == null)
            return;

        var width = _overlayRoot.Bounds.Width;
        var height = _overlayRoot.Bounds.Height;
        if (width < 1 || height < 1)
            return;

        _overlayMask.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
    }

    private void LogStepTarget(TutorialStep step, IReadOnlyList<Control> targets, Rect targetBounds, Rect cutout)
    {
        if (_loggedStep != _currentStep)
        {
            _loggedStep = _currentStep;
            _loggedCountForStep = 0;
        }

        if (_loggedCountForStep > 0)
        {
            var moved = Math.Abs(cutout.X - _lastLoggedBounds.X) > 8
                || Math.Abs(cutout.Y - _lastLoggedBounds.Y) > 8
                || Math.Abs(cutout.Width - _lastLoggedBounds.Width) > 8
                || Math.Abs(cutout.Height - _lastLoggedBounds.Height) > 8;
            if (!moved || _loggedCountForStep >= 4)
                return;
        }

        _loggedCountForStep++;
        _lastLoggedBounds = cutout;

        var targetNames = string.Join("，", targets.Select(DescribeControl));
        var maskSize = $"{_overlayRoot?.Bounds.Width ?? 0}x{_overlayRoot?.Bounds.Height ?? 0}";
        LoggerHelper.Debug(
            $"[教学提示] 步骤#{_currentStep + 1} 标题={step.TitleKey}，目标={targetNames}，" +
            $"目标矩形={targetBounds}，高亮矩形={cutout}，遮罩={maskSize}");
    }

    private static string DescribeControl(Control control)
    {
        var name = string.IsNullOrWhiteSpace(control.Name) ? "<未命名>" : control.Name;
        return $"{control.GetType().Name}[{name}]";
    }

    private void PositionTipAtTarget(TutorialStep step)
    {
        var targets = ResolveTargets(step);
        if (targets == null)
        {
            ClearCutout();
            return;
        }

        if (_tipBorder == null || _overlayRoot == null || _arrow == null || _overlayMask == null)
            return;

        try
        {
            var tb = GetTargetsBoundsInOverlay(targets);
            if (tb == null)
            {
                ClearCutout();
                return;
            }

            var cutout = tb.Value.Inflate(step.CutoutPadding);
            LogStepTarget(step, targets, tb.Value, cutout);

            var maskW = _overlayRoot.Bounds.Width;
            var maskH = _overlayRoot.Bounds.Height;

            if (maskW < 1 || maskH < 1)
            {
                ClearCutout();
                return;
            }

            _lastTargetBounds = cutout;

            UpdateOverlayClip(cutout, maskW, maskH);

            _tipBorder.InvalidateMeasure();
            _tipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var tipSize = _tipBorder.DesiredSize;
            if (tipSize.Width < 10) tipSize = new Size(320, 150);
            _lastTipSize = tipSize;

            var placement = CalculateBestPlacement(cutout, tipSize, maskW, maskH, step.PreferredPlacement);
            PositionArrow(placement, cutout, tipSize, out double tipX, out double tipY);

            tipX = Math.Max(8, Math.Min(tipX, maskW - tipSize.Width - 8));
            tipY = Math.Max(8, Math.Min(tipY, maskH - tipSize.Height - 8));

            // Re-adjust arrow so it stays attached to the (possibly clamped) tip border
            AdjustArrowToTip(placement, cutout, tipX, tipY, tipSize);

            Canvas.SetLeft(_tipBorder, tipX);
            Canvas.SetTop(_tipBorder, tipY);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"更新教学提示位置失败：原因={ex.Message}", ex);
        }
    }

    private void UpdateOverlayClip(Rect cutout, double totalW, double totalH)
    {
        if (_overlayMask == null) return;

        var fullRect = new RectangleGeometry(new Rect(0, 0, totalW, totalH));
        var cutoutRect = new RectangleGeometry(cutout)
        {
            RadiusX = 8,
            RadiusY = 8
        };

        var combined = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, cutoutRect);
        _overlayMask.Clip = combined;
    }

    private TipPlacement CalculateBestPlacement(
        Rect targetBounds,
        Size tipSize,
        double canvasW,
        double canvasH,
        TipPlacement preferred)
    {
        if (HasSpace(preferred, targetBounds, tipSize, canvasW, canvasH))
            return preferred;

        TipPlacement[] fallbacks =
            [TipPlacement.Bottom, TipPlacement.Top, TipPlacement.Left, TipPlacement.Right];
        foreach (var p in fallbacks)
        {
            if (HasSpace(p, targetBounds, tipSize, canvasW, canvasH))
                return p;
        }

        return TipPlacement.Bottom;
    }

    private static bool HasSpace(TipPlacement placement, Rect target, Size tip, double cw, double ch)
    {
        const double arrowSize = 12;
        return placement switch
        {
            TipPlacement.Top => target.Y - tip.Height - arrowSize > 0,
            TipPlacement.Bottom => target.Bottom + tip.Height + arrowSize < ch,
            TipPlacement.Left => target.X - tip.Width - arrowSize > 0,
            TipPlacement.Right => target.Right + tip.Width + arrowSize < cw,
            _ => true
        };
    }

    /// <summary>
    /// Calculates initial tip position and sets arrow shape for the given placement.
    /// The arrow fills the space between target and tip (arrowSize == gap).
    /// </summary>
    private void PositionArrow(TipPlacement placement,
        Rect targetBounds,
        Size tipSize,
        out double tipX,
        out double tipY)
    {
        const double arrowSize = 12;

        if (_arrow == null)
        {
            tipX = tipY = 0;
            return;
        }

        switch (placement)
        {
            case TipPlacement.Bottom:
                tipX = targetBounds.Center.X - tipSize.Width / 2;
                tipY = targetBounds.Bottom + arrowSize;
                _arrow.Points =
                [
                    new Point(0, arrowSize),
                    new Point(arrowSize, 0),
                    new Point(arrowSize * 2, arrowSize)
                ];
                _arrow.IsVisible = true;
                break;

            case TipPlacement.Top:
                tipX = targetBounds.Center.X - tipSize.Width / 2;
                tipY = targetBounds.Y - tipSize.Height - arrowSize;
                _arrow.Points =
                [
                    new Point(0, 0),
                    new Point(arrowSize, arrowSize),
                    new Point(arrowSize * 2, 0)
                ];
                _arrow.IsVisible = true;
                break;

            case TipPlacement.Left:
                tipX = targetBounds.X - tipSize.Width - arrowSize;
                tipY = targetBounds.Center.Y - tipSize.Height / 2;
                _arrow.Points =
                [
                    new Point(0, 0),
                    new Point(arrowSize, arrowSize),
                    new Point(0, arrowSize * 2)
                ];
                _arrow.IsVisible = true;
                break;

            case TipPlacement.Right:
                tipX = targetBounds.Right + arrowSize;
                tipY = targetBounds.Center.Y - tipSize.Height / 2;
                _arrow.Points =
                [
                    new Point(arrowSize, 0),
                    new Point(0, arrowSize),
                    new Point(arrowSize, arrowSize * 2)
                ];
                _arrow.IsVisible = true;
                break;

            default:
                tipX = tipY = 0;
                _arrow.IsVisible = false;
                break;
        }
    }

    /// <summary>
    /// Positions the arrow so its base is flush with the (clamped) tip border edge.
    /// The arrow overlaps the tip by a few pixels to eliminate any visible seam.
    /// Arrow lateral position tracks the target center, clamped within tip bounds
    /// (away from rounded corners).
    /// </summary>
    private void AdjustArrowToTip(TipPlacement placement,
        Rect targetBounds,
        double tipX,
        double tipY,
        Size tipSize)
    {
        if (_arrow == null) return;

        const double arrowSize = 12;
        const double cornerMargin = 20; // keep arrow away from rounded corners (CornerRadius=10)
        const double overlap = 3; // overlap into tip to eliminate anti-aliasing seam

        switch (placement)
        {
            case TipPlacement.Bottom:
            {
                var arrowX = Clamp(targetBounds.Center.X - arrowSize,
                    tipX + cornerMargin, tipX + tipSize.Width - cornerMargin - arrowSize * 2);
                Canvas.SetLeft(_arrow, arrowX);
                Canvas.SetTop(_arrow, tipY - arrowSize + overlap);
                break;
            }
            case TipPlacement.Top:
            {
                var arrowX = Clamp(targetBounds.Center.X - arrowSize,
                    tipX + cornerMargin, tipX + tipSize.Width - cornerMargin - arrowSize * 2);
                Canvas.SetLeft(_arrow, arrowX);
                Canvas.SetTop(_arrow, tipY + tipSize.Height - overlap);
                break;
            }
            case TipPlacement.Right:
            {
                var arrowY = Clamp(targetBounds.Center.Y - arrowSize,
                    tipY + cornerMargin, tipY + tipSize.Height - cornerMargin - arrowSize * 2);
                Canvas.SetLeft(_arrow, tipX - arrowSize + overlap);
                Canvas.SetTop(_arrow, arrowY);
                break;
            }
            case TipPlacement.Left:
            {
                var arrowY = Clamp(targetBounds.Center.Y - arrowSize,
                    tipY + cornerMargin, tipY + tipSize.Height - cornerMargin - arrowSize * 2);
                Canvas.SetLeft(_arrow, tipX + tipSize.Width - overlap);
                Canvas.SetTop(_arrow, arrowY);
                break;
            }
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (min > max) return (min + max) / 2; // fallback: center if range is invalid
        return Math.Max(min, Math.Min(value, max));
    }

    private async void GoToNextStep()
    {
        if (_steps == null) return;
        if (_currentStep >= _steps.Count - 1)
        {
            CloseTutorial();
            return;
        }

        // Leave current step
        _steps[_currentStep].OnLeave?.Invoke();

        _currentStep++;

        // Enter new step (e.g. navigate to a page)
        var newStep = _steps[_currentStep];
        newStep.OnEnter?.Invoke();

        // If step has OnEnter, wait for UI to settle
        if (newStep.OnEnter != null)
            await WaitForTarget(newStep);

        ShowCurrentStep();
    }

    private async void GoToPreviousStep()
    {
        if (_steps == null || _currentStep <= 0) return;

        // Leave current step
        _steps[_currentStep].OnLeave?.Invoke();

        _currentStep--;

        // Enter previous step (e.g. navigate back)
        var prevStep = _steps[_currentStep];
        prevStep.OnEnter?.Invoke();

        // If step has OnEnter, wait for UI to settle
        if (prevStep.OnEnter != null)
            await WaitForTarget(prevStep);

        ShowCurrentStep();
    }

    private async System.Threading.Tasks.Task WaitForTarget(TutorialStep step, int maxRetries = 15, int delayMs = 200)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            await System.Threading.Tasks.Task.Delay(delayMs);
            var targets = ResolveTargets(step);
            if (targets == null || targets.Any(target => target.Bounds.Width <= 1 || target.Bounds.Height <= 1))
                continue;

            var tb = GetTargetsBoundsInOverlay(targets);
            if (tb == null || _overlayRoot == null)
                continue;

            var overlayBounds = new Rect(0, 0, _overlayRoot.Bounds.Width, _overlayRoot.Bounds.Height);
            if (overlayBounds.Contains(tb.Value))
                return;

            foreach (var target in targets)
            {
                try
                {
                    target.BringIntoView();
                }
                catch
                {
                    // 控件脱离视觉树时忽略滚动请求失败。
                }
            }
        }
    }

    private void CloseTutorial()
    {
        // Leave current step if needed
        if (_steps != null && _currentStep >= 0 && _currentStep < _steps.Count)
            _steps[_currentStep].OnLeave?.Invoke();

        StopTracking();
        IsRunning = false;
        IsVisible = false;
        ConfigurationManager.Current.SetValue(
            ConfigurationKeys.HasCompletedFirstUseTutorial, true);
    }
}

public class TutorialStep
{
    public string TitleKey { get; set; } = string.Empty;
    public string DescriptionKey { get; set; } = string.Empty;
    /// <summary>
    /// 每次需要时重新查找目标控件，避免导航重建页面后继续使用旧引用。
    /// </summary>
    public Func<Control?>? FindTarget { get; set; }
    /// <summary>需要同时高亮的一组控件；高亮区域取这些控件边界的并集。</summary>
    public Func<IEnumerable<Control?>>? FindTargets { get; set; }
    public TipPlacement PreferredPlacement { get; set; } = TipPlacement.Bottom;
    /// <summary>
    /// Extra padding around the target control for the cutout highlight area.
    /// </summary>
    public Thickness CutoutPadding { get; set; } = new(0);
    /// <summary>
    /// Called when entering this step (e.g. navigate to a page).
    /// </summary>
    public Action? OnEnter { get; set; }
    /// <summary>
    /// Called when leaving this step (e.g. navigate back before going to previous step).
    /// </summary>
    public Action? OnLeave { get; set; }
}

public enum TipPlacement
{
    Top,
    Bottom,
    Left,
    Right
}
