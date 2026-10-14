using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Reactive;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using MFAAvalonia.Extensions;

namespace MFAAvalonia.Views.UserControls.Dashboard;

/// <summary>
/// 网格化 Dashboard 容器：根据 Rows/Columns 将可用区域划分为格子，子项使用 DashboardCard 的
/// GridRow/GridColumn/GridRowSpan/GridColumnSpan 来定位；拖拽与缩放会吸附到格子坐标。
/// </summary>
public sealed class DashboardCardGrid : Panel
{
    public bool HasSavedLayout() => ConfigurationManager.Current.ContainsKey(GetLayoutKey());
    public static readonly StyledProperty<string> GridIdProperty =
        AvaloniaProperty.Register<DashboardCardGrid, string>(nameof(GridId), string.Empty);

    public string GridId
    {
        get => GetValue(GridIdProperty);
        set => SetValue(GridIdProperty, value);
    }

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<DashboardCardGrid, int>(nameof(Columns), 3);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<DashboardCardGrid, int>(nameof(Rows), 2);

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly StyledProperty<double> CellSpacingProperty =
        AvaloniaProperty.Register<DashboardCardGrid, double>(nameof(CellSpacing), 10);

    public double CellSpacing
    {
        get => GetValue(CellSpacingProperty);
        set => SetValue(CellSpacingProperty, value);
    }

    public static readonly StyledProperty<Thickness> PaddingProperty =
        AvaloniaProperty.Register<DashboardCardGrid, Thickness>(nameof(Padding), new Thickness(15, 5, 15, 25));

    public Thickness Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private DashboardCard? _draggingCard;
    private DashboardCard? _maximizedCard;
    private HiddenLayout? _maximizedLayout;
    private bool _isApplyingMaximize;
    private Point _dragStartPosition;
    private readonly Dictionary<DashboardCard, Rect> _lastArranged = new();
    private readonly Dictionary<DashboardCard, Rect> _pendingTransitions = new();
    private readonly Dictionary<DashboardCard, Rect> _activeRectAnimations = new();
    private readonly Dictionary<DashboardCard, CancellationTokenSource> _transitionTokens = new();
    private const int LayoutTransitionDurationMs = 250;
    private int _dragStartCol;
    private int _dragStartRow;
    private Rect? _dragPreviewRect;
    private Border? _dragPreviewBorder;
    private IBrush? _dragPreviewFill;
    private IBrush? _dragPreviewStroke;
    private bool _isUpdatingPreviewHost;
    private bool _layoutLoaded;
    private bool _suppressLayoutSave;
    private DashboardCard? _resizingCard;
    private int _resizeStartCol;
    private int _resizeStartRow;
    private int _resizeStartColSpan;
    private int _resizeStartRowSpan;
    private Rect _resizeStartRect;
    private double _resizeDeltaX;
    private double _resizeDeltaY;
    private bool _isResizing;
    private readonly Dictionary<DashboardCard, HiddenLayout> _hiddenCards = new();
    private readonly Dictionary<DashboardCard, IDisposable> _visibilitySubscriptions = new();
    private readonly HashSet<DashboardCard> _restoringHiddenCards = new();
    private readonly List<Thumb> _columnSplitters = new();
    private readonly List<Thumb> _rowSplitters = new();
    private bool _isSyncingRowSplitters;
    private bool _isUpdatingSplitterHost;
    private SplitterDragContext? _activeSplitterContext;
    private SplitterLayout? _activeSplitterLayout;
    private RowSplitterDragContext? _activeRowSplitterContext;
    private Thumb? _activeSplitterThumb;
    private Thumb? _activeRowSplitterThumb;
    private bool _isSplitterDragging;
    private bool _isRowSplitterDragging;

    public DashboardCardGrid()
    {
        Children.CollectionChanged += OnChildrenCollectionChanged;

        _dragPreviewBorder = new Border
        {
            IsHitTestVisible = false,
            IsVisible = false,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        _dragPreviewBorder.ZIndex = 10;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        AttachToAllCards();
        EnsureLayoutsLoaded();
        EnsureMaximizedCardState();
        EnsureDragPreviewHost();
        EnsureSplitters();
        EnsureRowSplitters();
        EnsureSplitterHost();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ColumnsProperty)
        {
            _activeSplitterContext = null;
            EnsureSplitters();
            EnsureSplitterHost();
            InvalidateMeasure();
            InvalidateArrange();
        }

        if (change.Property == RowsProperty)
        {
            _activeRowSplitterContext = null;
            EnsureRowSplitters();
            EnsureSplitterHost();
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        DetachFromAllCards();
    }

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<DashboardCard>())
            {
                DetachCard(item);
            }
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<DashboardCard>())
            {
                AttachCard(item);
            }
        }

        if (!_isUpdatingPreviewHost)
        {
            EnsureDragPreviewHost();
        }

        EnsureSplitters();
        EnsureRowSplitters();
        EnsureSplitterHost();

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void AttachToAllCards()
    {
        foreach (var card in Children.OfType<DashboardCard>())
        {
            AttachCard(card);
        }
    }

    private void DetachFromAllCards()
    {
        foreach (var card in Children.OfType<DashboardCard>())
        {
            DetachCard(card);
        }
    }

    private void AttachCard(DashboardCard card)
    {
        card.DragStarted -= OnCardDragStarted;
        card.DragMoved -= OnCardDragMoved;
        card.DragEnded -= OnCardDragEnded;
        card.ResizeStarted -= OnCardResizeStarted;
        card.Resized -= OnCardResized;
        card.ResizeCompleted -= OnCardResizeCompleted;
        card.CollapseStateChanged -= OnCardCollapseStateChanged;
        card.MaximizeStateChanged -= OnCardMaximizeStateChanged;

        card.DragStarted += OnCardDragStarted;
        card.DragMoved += OnCardDragMoved;
        card.DragEnded += OnCardDragEnded;
        card.ResizeStarted += OnCardResizeStarted;
        card.Resized += OnCardResized;
        card.ResizeCompleted += OnCardResizeCompleted;
        card.CollapseStateChanged += OnCardCollapseStateChanged;
        card.MaximizeStateChanged += OnCardMaximizeStateChanged;

        if (_visibilitySubscriptions.TryGetValue(card, out var subscription))
        {
            subscription.Dispose();
            _visibilitySubscriptions.Remove(card);
        }

        _visibilitySubscriptions[card] = card
            .GetObservable(IsVisibleProperty)
            .Subscribe(new AnonymousObserver<bool>(isVisible => OnCardVisibilityChanged(card, isVisible)));

        OnCardVisibilityChanged(card, card.IsVisible);
    }

    private void DetachCard(DashboardCard card)
    {
        card.DragStarted -= OnCardDragStarted;
        card.DragMoved -= OnCardDragMoved;
        card.DragEnded -= OnCardDragEnded;
        card.ResizeStarted -= OnCardResizeStarted;
        card.Resized -= OnCardResized;
        card.ResizeCompleted -= OnCardResizeCompleted;
        card.CollapseStateChanged -= OnCardCollapseStateChanged;
        card.MaximizeStateChanged -= OnCardMaximizeStateChanged;

        if (_visibilitySubscriptions.TryGetValue(card, out var subscription))
        {
            subscription.Dispose();
            _visibilitySubscriptions.Remove(card);
        }

        _hiddenCards.Remove(card);

        if (ReferenceEquals(_maximizedCard, card))
        {
            _maximizedCard = null;
            _maximizedLayout = null;
        }
    }

    private void OnCardCollapseStateChanged(object? sender, bool e)
    {
        if (sender is not DashboardCard card)
        {
            return;
        }

        if (card.IsMaximized)
        {
            return;
        }

        var col = ClampIndex(card.GridColumn, Columns);
        var row = ClampIndex(card.GridRow, Rows);
        var colSpan = Math.Max(1, card.GridColumnSpan);
        var rowSpan = Math.Max(1, card.GridRowSpan);

        var fromRect = GetRectFromGrid(col, row, colSpan, rowSpan);

        if (card.IsCollapsed)
        {
            card.ExpandedRowSpan = Math.Max(1, card.GridRowSpan);
            card.ExpandedColumnSpan = Math.Max(1, card.GridColumnSpan);
            card.GridRowSpan = 1;
        }
        else
        {
            var desiredRowSpan = Math.Max(1, card.ExpandedRowSpan);
            var maxRowSpan = GetMaxAvailableRowSpan(card, col, row, colSpan);
            card.GridRowSpan = Math.Clamp(desiredRowSpan, 1, Math.Max(1, maxRowSpan));
        }

        InvalidateMeasure();
        InvalidateArrange();

        var toRect = GetRectFromGrid(col, row, colSpan, card.GridRowSpan);
        StartRectTransition(card, fromRect, toRect, SaveLayouts);
    }


    private int GetMaxAvailableRowSpan(DashboardCard card, int col, int row, int colSpan)
    {
        if (Rows <= 0)
        {
            return 1;
        }

        var occupied = BuildOccupied(exclude: card);
        var max = 0;

        for (var r = row; r < Rows; r++)
        {
            var blocked = false;
            for (var c = col; c < col + colSpan; c++)
            {
                if (occupied[c, r])
                {
                    blocked = true;
                    break;
                }
            }

            if (blocked)
            {
                break;
            }

            max++;
        }

        return Math.Max(1, max);
    }

    private void OnCardDragStarted(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DashboardCard card)
        {
            return;
        }

        if (card.IsMaximized)
        {
            return;
        }

        _draggingCard = card;
        _dragStartPosition = e.GetPosition(this);
        _dragStartCol = ClampIndex(card.GridColumn, Columns);
        _dragStartRow = ClampIndex(card.GridRow, Rows);

        var colSpan = Math.Max(1, card.GridColumnSpan);
        var rowSpan = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);
        UpdateDragPreview(_dragStartCol, _dragStartRow, colSpan, rowSpan);
    }

    private void OnCardDragMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingCard == null)
        {
            return;
        }
        var metrics = GetMetrics(Bounds.Size);
        if (metrics.CellPitchX <= 0 || metrics.CellPitchY <= 0)
        {
            ClearDragPreview();
            return;
        }


        var currentPosition = e.GetPosition(this);
        var deltaX = currentPosition.X - _dragStartPosition.X;
        var deltaY = currentPosition.Y - _dragStartPosition.Y;

        var deltaCol = (int)Math.Round(deltaX / metrics.CellPitchX);
        var deltaRow = (int)Math.Round(deltaY / metrics.CellPitchY);

        var targetCol = Math.Clamp(_dragStartCol + deltaCol, 0, Columns - 1);
        var targetRow = Math.Clamp(_dragStartRow + deltaRow, 0, Rows - 1);

        var colSpan = Math.Max(1, _draggingCard.GridColumnSpan);
        var rowSpan = Math.Max(1, _draggingCard.IsCollapsed ? 1 : _draggingCard.GridRowSpan);

        targetCol = Math.Clamp(targetCol, 0, Columns - colSpan);
        targetRow = Math.Clamp(targetRow, 0, Rows - rowSpan);

        if (!CanPlace(_draggingCard, targetCol, targetRow, colSpan, rowSpan))
        {
            ClearDragPreview();
            return;
        }

        UpdateDragPreview(targetCol, targetRow, colSpan, rowSpan);

        _draggingCard.GridColumn = targetCol;
        _draggingCard.GridRow = targetRow;
        InvalidateArrange();
    }

    private void OnCardDragEnded(object? sender, PointerReleasedEventArgs e)
    {
        _draggingCard = null;
        ClearDragPreview();
        SaveLayouts();
    }

    private void OnCardResizeStarted(object? sender, DashboardCardResizeEventArgs e)
    {
        if (sender is not DashboardCard card)
        {
            return;
        }

        if (card.IsMaximized)
        {
            return;
        }

        _resizingCard = card;
        _resizeStartCol = ClampIndex(card.GridColumn, Columns);
        _resizeStartRow = ClampIndex(card.GridRow, Rows);
        _resizeStartColSpan = Math.Max(1, card.GridColumnSpan);
        _resizeStartRowSpan = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);
        _resizeDeltaX = 0;
        _resizeDeltaY = 0;
        _isResizing = true;

        _resizeStartRect = GetRectFromGrid(_resizeStartCol, _resizeStartRow, _resizeStartColSpan, _resizeStartRowSpan);
        UpdateDragPreview(_resizeStartCol, _resizeStartRow, _resizeStartColSpan, _resizeStartRowSpan);
    }

    private void OnCardResized(object? sender, DashboardCardResizeEventArgs e)
    {
        if (sender is not DashboardCard card || !_isResizing || !ReferenceEquals(card, _resizingCard))
        {
            return;
        }

        _resizeDeltaX = e.HorizontalChange;
        _resizeDeltaY = e.VerticalChange;

        if (!TryBuildResizeLayout(card, e.Direction, _resizeDeltaX, _resizeDeltaY,
                out var newCol, out var newRow, out var newColSpan, out var newRowSpan))
        {
            ClearDragPreview();
            return;
        }

        UpdateDragPreview(newCol, newRow, newColSpan, newRowSpan);
    }

    private void OnCardResizeCompleted(object? sender, DashboardCardResizeEventArgs e)
    {
        if (sender is not DashboardCard card || !_isResizing || !ReferenceEquals(card, _resizingCard))
        {
            return;
        }

        if (TryBuildResizeLayout(card, e.Direction, _resizeDeltaX, _resizeDeltaY,
                out var newCol, out var newRow, out var newColSpan, out var newRowSpan))
        {
            card.GridColumn = newCol;
            card.GridRow = newRow;
            card.GridColumnSpan = newColSpan;
            card.GridRowSpan = card.IsCollapsed ? 1 : newRowSpan;

            if (!card.IsCollapsed)
            {
                card.ExpandedColumnSpan = newColSpan;
                card.ExpandedRowSpan = newRowSpan;
            }

            InvalidateMeasure();
            InvalidateArrange();
            SaveLayouts();
        }

        _isResizing = false;
        _resizingCard = null;
        ClearDragPreview();
    }

    private bool TryBuildResizeLayout(
        DashboardCard card,
        string direction,
        double horizontalChange,
        double verticalChange,
        out int newCol,
        out int newRow,
        out int newColSpan,
        out int newRowSpan)
    {
        newCol = _resizeStartCol;
        newRow = _resizeStartRow;
        newColSpan = Math.Max(1, _resizeStartColSpan);
        newRowSpan = Math.Max(1, card.IsCollapsed ? 1 : _resizeStartRowSpan);

        var metrics = GetMetrics(Bounds.Size);
        if (metrics.CellPitchX <= 0 || metrics.CellPitchY <= 0)
        {
            return false;
        }

        var rect = _resizeStartRect;
        var x = rect.X;
        var y = rect.Y;
        var w = rect.Width;
        var h = rect.Height;

        if (string.Equals(direction, "br", StringComparison.OrdinalIgnoreCase))
        {
            horizontalChange = -horizontalChange;
            verticalChange = -verticalChange;
        }
        else if (string.Equals(direction, "bl", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(direction, "tr", StringComparison.OrdinalIgnoreCase))
        {
            (horizontalChange, verticalChange) = (verticalChange, horizontalChange);
            if (string.Equals(direction, "bl", StringComparison.OrdinalIgnoreCase))
                verticalChange = -verticalChange;
            if (string.Equals(direction, "tr", StringComparison.OrdinalIgnoreCase))
                horizontalChange = -horizontalChange;
        }

        switch (direction)
        {
            case "r":
            case "tr":
            case "br":
                w += horizontalChange;
                break;
            case "l":
            case "tl":
            case "bl":
                x += horizontalChange;
                w -= horizontalChange;
                break;
        }

        if (!card.IsCollapsed)
        {
            switch (direction)
            {
                case "b":
                case "bl":
                case "br":
                    h += verticalChange;
                    break;
                case "t":
                case "tl":
                case "tr":
                    y += verticalChange;
                    h -= verticalChange;
                    break;
            }
        }

        w = Math.Max(0, w);
        h = Math.Max(0, h);

        newCol = (int)Math.Round((x - Padding.Left) / metrics.CellPitchX);
        newRow = (int)Math.Round((y - Padding.Top) / metrics.CellPitchY);
        newColSpan = (int)Math.Round((w + CellSpacing) / metrics.CellPitchX);
        newRowSpan = (int)Math.Round((h + CellSpacing) / metrics.CellPitchY);

        newColSpan = Math.Max(1, newColSpan);
        newRowSpan = Math.Max(1, newRowSpan);

        if (card.IsCollapsed)
        {
            newRow = _resizeStartRow;
            newRowSpan = 1;
        }

        newCol = Math.Clamp(newCol, 0, Columns - 1);
        newRow = Math.Clamp(newRow, 0, Rows - 1);
        newColSpan = Math.Clamp(newColSpan, 1, Columns - newCol);
        newRowSpan = Math.Clamp(newRowSpan, 1, Rows - newRow);

        return CanPlace(card, newCol, newRow, newColSpan, newRowSpan);
    }

    private Rect GetRectFromGrid(int col, int row, int colSpan, int rowSpan)
    {
        var metrics = GetMetrics(Bounds.Size);
        var x = Padding.Left + col * metrics.CellPitchX;
        var y = Padding.Top + row * metrics.CellPitchY;
        var w = colSpan * metrics.CellWidth + (colSpan - 1) * CellSpacing;
        var h = rowSpan * metrics.CellHeight + (rowSpan - 1) * CellSpacing;
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private bool CanPlace(DashboardCard movingCard, int col, int row, int colSpan, int rowSpan)
    {
        if (Columns <= 0 || Rows <= 0)
        {
            return false;
        }

        if (col < 0 || row < 0 || col + colSpan > Columns || row + rowSpan > Rows)
        {
            return false;
        }

        var occupied = BuildOccupied(exclude: movingCard);

        for (var c = col; c < col + colSpan; c++)
        {
            for (var r = row; r < row + rowSpan; r++)
            {
                if (occupied[c, r])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool[,] BuildOccupied(DashboardCard? exclude)
    {
        var cols = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        var occupied = new bool[cols, rows];

        foreach (var card in Children.OfType<DashboardCard>())
        {
            if (ReferenceEquals(card, exclude))
            {
                continue;
            }

            if (!card.IsVisible)
            {
                continue;
            }

            var col = ClampIndex(card.GridColumn, cols);
            var row = ClampIndex(card.GridRow, rows);

            var colSpan = Math.Max(1, card.GridColumnSpan);
            var rowSpan = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);

            colSpan = Math.Clamp(colSpan, 1, cols - col);
            rowSpan = Math.Clamp(rowSpan, 1, rows - row);

            for (var c = col; c < col + colSpan; c++)
            {
                for (var r = row; r < row + rowSpan; r++)
                {
                    occupied[c, r] = true;
                }
            }
        }

        return occupied;
    }

    private bool HasOverlaps(IEnumerable<DashboardCardLayout> layouts, int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        var occupied = new bool[columns, rows];

        foreach (var layout in layouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                continue;
            }

            // Hidden cards do not occupy dashboard cells. This is especially important for
            // LiveView: when it is disabled, another card may intentionally be moved into its
            // saved position. Treating the hidden card as occupied here would reject that valid
            // layout on the next launch and silently restore the default layout.
            var card = Children.OfType<DashboardCard>()
                .FirstOrDefault(c => string.Equals(c.CardId, layout.Id, StringComparison.OrdinalIgnoreCase));
            if (card is { IsVisible: false })
            {
                continue;
            }

            var colSpan = Math.Max(1, layout.ColSpan);
            var rowSpan = layout.IsCollapsed ? 1 : Math.Max(1, layout.RowSpan);

            if (layout.Col < 0 || layout.Row < 0)
            {
                return true;
            }

            if (layout.Col >= columns || layout.Row >= rows)
            {
                return true;
            }

            if (layout.Col + colSpan > columns || layout.Row + rowSpan > rows)
            {
                return true;
            }

            for (var c = layout.Col; c < layout.Col + colSpan; c++)
            {
                for (var r = layout.Row; r < layout.Row + rowSpan; r++)
                {
                    if (occupied[c, r])
                    {
                        return true;
                    }

                    occupied[c, r] = true;
                }
            }
        }

        return false;
    }
    private void EnsureLayoutsLoaded()
    {
        if (_layoutLoaded)
        {
            return;
        }

        _layoutLoaded = true;

        var defaults = GetDefaultLayouts();
        var key = GetLayoutKey();
        var layouts = LoadConfigLayouts(defaults, key, out var hasConfigLayouts);
        var layoutMeta = LoadLayoutMeta();
        var resourceLayout = TryLoadResourceLayout(out var resourceLayoutHash);
        var layoutHashKey = GetResourceLayoutHashKey();
        var storedLayoutHash = ConfigurationManager.Current.GetValue(layoutHashKey, string.Empty);
        var resourceLayoutChanged = !string.IsNullOrWhiteSpace(resourceLayoutHash)
            && !string.Equals(resourceLayoutHash, storedLayoutHash, StringComparison.OrdinalIgnoreCase);

        if (resourceLayoutChanged && resourceLayout != null)
        {
            ApplyResourceLayout(resourceLayout, defaults);
            SaveLayouts();
            ConfigurationManager.Current.SetValue(layoutHashKey, resourceLayoutHash);
            ToastHelper.Info(LangKeys.ResourceLayoutUpdatedTitle.ToLocalization(),
                LangKeys.ResourceLayoutUpdatedContent.ToLocalization());
            return;
        }

        if (hasConfigLayouts)
        {
            var effectiveColumns = layoutMeta?.Columns > 0 ? layoutMeta.Columns : Columns;
            var effectiveRows = layoutMeta?.Rows > 0 ? layoutMeta.Rows : Rows;

            if (HasOverlaps(layouts, effectiveColumns, effectiveRows))
            {
                layouts = defaults;
                hasConfigLayouts = false;
            }
        }

        if (resourceLayout == null && !hasConfigLayouts)
        {
            ApplyLayouts(defaults);
            EnsureResourceLayoutFile(defaults);
            return;
        }

        if (!hasConfigLayouts && resourceLayout != null)
        {
            ApplyResourceLayout(resourceLayout, defaults);
            SaveLayouts();
            if (!string.IsNullOrWhiteSpace(resourceLayoutHash))
            {
                ConfigurationManager.Current.SetValue(layoutHashKey, resourceLayoutHash);
            }
            return;
        }

        if (resourceLayout != null
            && layoutMeta != null
            && layoutMeta.Rows > 0
            && layoutMeta.Columns > 0
            && (layoutMeta.Rows != resourceLayout.Rows || layoutMeta.Columns != resourceLayout.Columns))
        {
            ApplyResourceLayout(resourceLayout, defaults);
            SaveLayouts();
            if (!string.IsNullOrWhiteSpace(resourceLayoutHash))
            {
                ConfigurationManager.Current.SetValue(layoutHashKey, resourceLayoutHash);
            }
            return;
        }

        ApplyLayoutMeta(layoutMeta);
        ApplyLayouts(layouts);
        ApplyLayouts(layouts);
    }

    private List<DashboardCardLayout> GetDefaultLayouts()
    {
        return Children
            .OfType<DashboardCard>()
            .Where(card => !string.IsNullOrWhiteSpace(card.CardId))
            .Select(card => new DashboardCardLayout
            {
                Id = card.CardId,
                Col = card.GridColumn,
                Row = card.GridRow,
                ColSpan = card.GridColumnSpan,
                RowSpan = card.GridRowSpan,
                IsCollapsed = card.IsCollapsed,
                IsMaximized = false,
                ExpandedColSpan = card.ExpandedColumnSpan,
                ExpandedRowSpan = card.ExpandedRowSpan
            })
            .ToList();
    }

    private void ApplyLayouts(IEnumerable<DashboardCardLayout> layouts)
    {
        _suppressLayoutSave = true;

        foreach (var layout in layouts)
        {
            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                continue;
            }

            var card = Children.OfType<DashboardCard>()
                .FirstOrDefault(c => string.Equals(c.CardId, layout.Id, StringComparison.OrdinalIgnoreCase));

            if (card == null)
            {
                continue;
            }

            card.IsMaximized = false;
            card.GridColumn = layout.Col;
            card.GridRow = layout.Row;
            card.GridColumnSpan = Math.Max(1, layout.ColSpan);
            card.ExpandedColumnSpan = Math.Max(1, layout.ExpandedColSpan);
            card.ExpandedRowSpan = Math.Max(1, layout.ExpandedRowSpan);
            card.IsCollapsed = layout.IsCollapsed;
            card.GridRowSpan = layout.IsCollapsed ? 1 : Math.Max(1, layout.RowSpan);
        }

        _suppressLayoutSave = false;
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void SaveLayouts()
    {
        if (_suppressLayoutSave)
        {
            return;
        }

        var layouts = Children
            .OfType<DashboardCard>()
            .Where(card => !string.IsNullOrWhiteSpace(card.CardId))
            .Select(card => new DashboardCardLayout
            {
                Id = card.CardId,
                Col = card.GridColumn,
                Row = card.GridRow,
                ColSpan = card.GridColumnSpan,
                RowSpan = card.GridRowSpan,
                IsCollapsed = card.IsCollapsed,
                IsMaximized = false,
                ExpandedColSpan = card.ExpandedColumnSpan,
                ExpandedRowSpan = card.ExpandedRowSpan
            })
            .ToList();

        ConfigurationManager.Current.SetValue(GetLayoutKey(), layouts);
        SaveLayoutMeta();
    }

    private string GetLayoutKey()
    {
        return string.IsNullOrWhiteSpace(GridId)
            ? ConfigurationKeys.DashboardCardGridLayout
            : $"{ConfigurationKeys.DashboardCardGridLayout}.{GridId}";
    }

    private string GetLayoutMetaKey()
    {
        return string.IsNullOrWhiteSpace(GridId)
            ? ConfigurationKeys.TaskQueueDashboardLayout
            : $"{ConfigurationKeys.TaskQueueDashboardLayout}.{GridId}";
    }

    private List<DashboardCardLayout> LoadConfigLayouts(List<DashboardCardLayout> defaults, string key, out bool hasConfigLayouts)
    {
        var layouts = ConfigurationManager.Current.GetValue(key, new List<DashboardCardLayout>());
        hasConfigLayouts = ConfigurationManager.Current.ContainsKey(key) && layouts is { Count: > 0 };

        if (layouts == null || layouts.Count == 0)
        {
            layouts = defaults;

            if (!string.Equals(key, ConfigurationKeys.DashboardCardGridLayout, StringComparison.OrdinalIgnoreCase))
            {
                var legacy = ConfigurationManager.Current.GetValue(ConfigurationKeys.DashboardCardGridLayout, defaults);
                if (legacy != null && legacy.Count > 0)
                {
                    layouts = legacy;
                }
            }
        }

        return layouts;
    }

    private sealed class DashboardGridMeta
    {
        [JsonProperty("rows")]
        public int Rows { get; set; }

        [JsonProperty("columns")]
        public int Columns { get; set; }

        [JsonProperty("spacing")]
        public double Spacing { get; set; }
    }

    private DashboardGridMeta? LoadLayoutMeta()
    {
        var key = GetLayoutMetaKey();
        if (!ConfigurationManager.Current.ContainsKey(key))
        {
            return null;
        }

        var meta = ConfigurationManager.Current.GetValue(key, new DashboardGridMeta());
        if (meta.Rows <= 0 || meta.Columns <= 0)
        {
            return null;
        }

        return meta;
    }

    private void SaveLayoutMeta()
    {
        var key = GetLayoutMetaKey();
        var meta = new DashboardGridMeta
        {
            Rows = Rows,
            Columns = Columns,
            Spacing = CellSpacing
        };

        ConfigurationManager.Current.SetValue(key, meta);
    }

    private void ApplyLayoutMeta(DashboardGridMeta? meta)
    {
        if (meta == null)
        {
            return;
        }

        if (meta.Columns > 0)
        {
            Columns = meta.Columns;
        }

        if (meta.Rows > 0)
        {
            Rows = meta.Rows;
        }

        if (meta.Spacing > 0)
        {
            CellSpacing = meta.Spacing;
        }
    }

    private sealed class LayoutCell
    {
        public int Row { get; init; }
        public int Col { get; init; }
        public int RowSpan { get; init; }
        public int ColSpan { get; init; }
    }

    private sealed class ResourceLayoutDefinition
    {
        public int Rows { get; init; }
        public int Columns { get; init; }
        public double Spacing { get; init; }
        public Dictionary<string, LayoutCell> Cards { get; init; } = new();
    }

    private ResourceLayoutDefinition? TryLoadResourceLayout(out string? layoutHash)
    {
        layoutHash = null;

        if (!TryGetResourceLayoutPath(out var layoutPath, forWrite: false) || !File.Exists(layoutPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(layoutPath);
            layoutHash = ComputeLayoutHash(json);
            var root = JObject.Parse(json);

            var rows = root["rows"]?.Value<int>() ?? 0;
            var columns = root["columns"]?.Value<int>() ?? 0;
            var spacing = root["spacing"]?.Value<double>() ?? 0;

            var cards = new Dictionary<string, LayoutCell>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.Properties())
            {
                if (string.Equals(property.Name, "rows", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "columns", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "spacing", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value is not JObject cardObject)
                {
                    continue;
                }

                var row = cardObject["row"]?.Value<int>() ?? 0;
                var col = cardObject["col"]?.Value<int>() ?? 0;
                var rowSpan = cardObject["row_span"]?.Value<int>()
                    ?? cardObject["rowSpan"]?.Value<int>()
                    ?? 1;
                var colSpan = cardObject["col_span"]?.Value<int>()
                    ?? cardObject["colSpan"]?.Value<int>()
                    ?? 1;

                cards[property.Name] = new LayoutCell
                {
                    Row = row,
                    Col = col,
                    RowSpan = rowSpan,
                    ColSpan = colSpan
                };
            }

            return new ResourceLayoutDefinition
            {
                Rows = rows > 0 ? rows : Rows,
                Columns = columns > 0 ? columns : Columns,
                Spacing = spacing > 0 ? spacing : CellSpacing,
                Cards = cards
            };
        }
        catch
        {
            layoutHash = null;
            return null;
        }
    }

    private void ApplyResourceLayout(ResourceLayoutDefinition layout, List<DashboardCardLayout> defaults)
    {
        _suppressLayoutSave = true;

        Columns = layout.Columns > 0 ? layout.Columns : Columns;
        Rows = layout.Rows > 0 ? layout.Rows : Rows;
        CellSpacing = layout.Spacing > 0 ? layout.Spacing : CellSpacing;

        ApplyLayouts(defaults);

        foreach (var card in Children.OfType<DashboardCard>())
        {
            if (string.IsNullOrWhiteSpace(card.CardId))
            {
                continue;
            }

            if (!layout.Cards.TryGetValue(card.CardId, out var cell))
            {
                continue;
            }

            var colSpan = Math.Max(1, cell.ColSpan);
            var rowSpan = Math.Max(1, cell.RowSpan);

            card.GridColumn = cell.Col;
            card.GridRow = cell.Row;
            card.GridColumnSpan = colSpan;
            card.GridRowSpan = card.IsCollapsed ? 1 : rowSpan;
            card.ExpandedColumnSpan = colSpan;
            card.ExpandedRowSpan = rowSpan;
        }

        _suppressLayoutSave = false;
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void EnsureResourceLayoutFile(List<DashboardCardLayout> defaults)
    {
        if (!TryGetResourceLayoutPath(out var layoutPath, forWrite: true))
        {
            return;
        }

        if (File.Exists(layoutPath))
        {
            return;
        }

        try
        {
            var layout = BuildResourceLayout(defaults);
            var json = JsonConvert.SerializeObject(layout, Formatting.Indented);
            File.WriteAllText(layoutPath, json);
        }
        catch
        {
            // 忽略生成失败
        }
    }

    private string GetResourceLayoutHashKey()
    {
        return string.IsNullOrWhiteSpace(GridId)
            ? ConfigurationKeys.DashboardCardGridResourceLayoutHash
            : $"{ConfigurationKeys.DashboardCardGridResourceLayoutHash}.{GridId}";
    }

    private static string ComputeLayoutHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private bool TryGetResourceLayoutPath(out string layoutPath, bool forWrite)
    {
        layoutPath = string.Empty;

        var resourceRoot = MaaProcessor.Resource;

        if (forWrite)
        {
            if (!Directory.Exists(resourceRoot))
            {
                Directory.CreateDirectory(resourceRoot);
            }

            layoutPath = Path.Combine(resourceRoot, "mfa_layout.json");
            return true;
        }

        if (Directory.Exists(resourceRoot))
        {
            var candidate = Directory
                .EnumerateFiles(resourceRoot, "mfa_layout.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                layoutPath = candidate;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetCurrentResourcePaths()
    {
        var model = Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel;
        if (model == null)
        {
            return Array.Empty<string>();
        }

        var current = model.CurrentResources.FirstOrDefault(r => r.Name == model.CurrentResource);
        var paths = current?.ResolvedPath ?? current?.Path ?? new List<string>();

        return paths;
    }

    private JObject BuildResourceLayout(IEnumerable<DashboardCardLayout> defaults)
    {
        var obj = new JObject
        {
            ["rows"] = Rows,
            ["columns"] = Columns,
            ["spacing"] = CellSpacing
        };

        foreach (var layout in defaults)
        {
            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                continue;
            }

            var card = new JObject
            {
                ["row"] = layout.Row,
                ["col"] = layout.Col,
                ["row_span"] = Math.Max(1, layout.RowSpan),
                ["col_span"] = Math.Max(1, layout.ColSpan)
            };

            obj[layout.Id] = card;
        }

        return obj;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        var sizeForMetrics = new Size(
            double.IsInfinity(availableSize.Width) ? Bounds.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Bounds.Height : availableSize.Height);

        var metrics = GetMetrics(sizeForMetrics);

        foreach (var child in Children)
        {
            if (child is not Control c)
            {
                continue;
            }

            if (ReferenceEquals(child, _dragPreviewBorder))
            {
                var rect = _dragPreviewRect ?? new Rect();
                c.Measure(new Size(Math.Max(0, rect.Width), Math.Max(0, rect.Height)));
                continue;
            }

            if (child is Thumb splitter && _columnSplitters.Contains(splitter))
            {
                c.Measure(new Size(16, Math.Max(0, sizeForMetrics.Height)));
                continue;
            }

            if (child is Thumb rowSplitter && _rowSplitters.Contains(rowSplitter))
            {
                c.Measure(new Size(Math.Max(0, sizeForMetrics.Width), 16));
                continue;
            }

            if (child is DashboardCard card)
            {
                if (!card.IsVisible)
                {
                    c.Measure(new Size(0, 0));
                    continue;
                }

                if (card.IsMaximized)
                {
                    var maxWidth = Math.Max(0, sizeForMetrics.Width - Padding.Left - Padding.Right);
                    var maxHeight = Math.Max(0, sizeForMetrics.Height - Padding.Top - Padding.Bottom);
                    c.Measure(new Size(maxWidth, maxHeight));
                    continue;
                }

                var colSpan = Math.Max(1, card.GridColumnSpan);
                var rowSpan = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);
                colSpan = Math.Clamp(colSpan, 1, columns);
                rowSpan = Math.Clamp(rowSpan, 1, rows);

                var w = colSpan * metrics.CellWidth + (colSpan - 1) * CellSpacing;
                var h = rowSpan * metrics.CellHeight + (rowSpan - 1) * CellSpacing;

                c.Measure(new Size(Math.Max(0, w), Math.Max(0, h)));
                continue;
            }

            c.Measure(availableSize);
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);
        var metrics = GetMetrics(finalSize);
        var arrangedRects = new Dictionary<DashboardCard, Rect>();
        Rect? maximizedRect = null;

        foreach (var child in Children.OfType<Control>())
        {
            if (ReferenceEquals(child, _dragPreviewBorder))
            {
                continue;
            }

            if (child is Thumb splitter && _columnSplitters.Contains(splitter))
            {
                continue;
            }

            if (child is Thumb rowSplitter && _rowSplitters.Contains(rowSplitter))
            {
                continue;
            }

            if (child is not DashboardCard card)
            {
                child.Arrange(new Rect(finalSize));
                continue;
            }

            if (!card.IsVisible)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            var isMaximizeVisual = card.IsMaximized
                || (ReferenceEquals(card, _maximizedCard) && card.IsMaximizeTransitionActive);

            if (isMaximizeVisual)
            {
                var maxWidth = Math.Max(0, finalSize.Width - Padding.Left - Padding.Right);
                var maxHeight = Math.Max(0, finalSize.Height - Padding.Top - Padding.Bottom);
                var rect1 = new Rect(Padding.Left, Padding.Top, maxWidth, maxHeight);
                var arranged1 = _activeRectAnimations.GetValueOrDefault(card, rect1);
                child.Arrange(arranged1);
                UpdateLastArranged(card, rect1);
                HandlePendingTransition(card, rect1);
                arrangedRects[card] = arranged1;
                maximizedRect = arranged1;
                continue;
            }

            var col = ClampIndex(card.GridColumn, columns);
            var row = ClampIndex(card.GridRow, rows);

            var colSpan = Math.Max(1, card.GridColumnSpan);
            var rowSpan = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);

            colSpan = Math.Clamp(colSpan, 1, columns - col);
            rowSpan = Math.Clamp(rowSpan, 1, rows - row);

            var x = Padding.Left + col * metrics.CellPitchX;
            var y = Padding.Top + row * metrics.CellPitchY;

            var w = colSpan * metrics.CellWidth + (colSpan - 1) * CellSpacing;
            var h = rowSpan * metrics.CellHeight + (rowSpan - 1) * CellSpacing;

            card.GridColumn = col;
            card.GridRow = row;
            card.GridColumnSpan = colSpan;
            card.GridRowSpan = rowSpan;

            var rect = new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
            var arranged = _activeRectAnimations.GetValueOrDefault(card, rect);
            child.Arrange(arranged);
            UpdateLastArranged(card, rect);
            HandlePendingTransition(card, rect);
            arrangedRects[card] = arranged;
        }

        ApplyMaximizeOpacityMask(maximizedRect, arrangedRects);
        ArrangeDragPreview();
        ArrangeColumnSplitters(finalSize);
        ArrangeRowSplitters(finalSize);

        return finalSize;
    }

    private (double CellWidth, double CellHeight, double CellPitchX, double CellPitchY) GetMetrics(Size size)
    {
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        var innerW = Math.Max(0, size.Width - Padding.Left - Padding.Right);
        var innerH = Math.Max(0, size.Height - Padding.Top - Padding.Bottom);

        var w = Math.Max(0, innerW - (columns - 1) * CellSpacing) / columns;
        var h = Math.Max(0, innerH - (rows - 1) * CellSpacing) / rows;

        return (w, h, w + CellSpacing, h + CellSpacing);
    }
    private static int ClampIndex(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return Math.Clamp(index, 0, count - 1);
    }

    private void OnCardVisibilityChanged(DashboardCard card, bool isVisible)
    {
        if (!isVisible)
        {
            if (_restoringHiddenCards.Contains(card))
            {
                InvalidateMeasure();
                InvalidateArrange();
                return;
            }

            if (!_hiddenCards.ContainsKey(card))
            {
                _hiddenCards[card] = HiddenLayout.FromCard(card);
            }

            InvalidateMeasure();
            InvalidateArrange();
            return;
        }

        if (!_hiddenCards.TryGetValue(card, out var layout))
        {
            return;
        }

        _restoringHiddenCards.Add(card);

        try
        {
            var col = ClampIndex(layout.Col, Columns);
            var row = ClampIndex(layout.Row, Rows);
            var colSpan = Math.Max(1, layout.ColSpan);
            var rowSpan = layout.IsCollapsed ? 1 : Math.Max(1, layout.RowSpan);

            colSpan = Math.Clamp(colSpan, 1, Columns - col);
            rowSpan = Math.Clamp(rowSpan, 1, Rows - row);

            var placed = TryPlaceCard(card, col, row, colSpan, rowSpan)
                         || TryPlaceFirstAvailable(card, colSpan, rowSpan);

            if (!placed)
            {
                if (IsLiveViewCard(card))
                {
                    placed = TryPlaceBestAvailableWithin(card, colSpan, rowSpan);
                    if (!placed)
                    {
                        _hiddenCards[card] = layout;
                        card.IsVisible = false;
                        ToastHelper.Warn(
                            LangKeys.Warning.ToLocalization(),
                            "实时画面已开启，但当前布局没有可用空间，请调整主页面卡片布局。");
                        return;
                    }
                }
                else
                {
                    placed = TryPlaceFirstAvailable(card, 1, 1);
                    if (!placed)
                    {
                        placed = TryPlaceCard(card, 0, 0, 1, 1);
                    }
                }
            }

            if (!placed)
            {
                return;
            }

            _hiddenCards.Remove(card);
            card.ExpandedColumnSpan = Math.Max(1, layout.ExpandedColSpan);
            card.ExpandedRowSpan = Math.Max(1, layout.ExpandedRowSpan);
            card.IsCollapsed = layout.IsCollapsed;

            InvalidateMeasure();
            InvalidateArrange();
            SaveLayouts();
        }
        finally
        {
            _restoringHiddenCards.Remove(card);
        }
    }

    private static bool IsLiveViewCard(DashboardCard card)
    {
        return string.Equals(card.CardId, "live_view", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryPlaceCard(DashboardCard card, int col, int row, int colSpan, int rowSpan)
    {
        col = Math.Clamp(col, 0, Math.Max(0, Columns - 1));
        row = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
        colSpan = Math.Clamp(colSpan, 1, Math.Max(1, Columns - col));
        rowSpan = Math.Clamp(rowSpan, 1, Math.Max(1, Rows - row));

        if (!CanPlace(card, col, row, colSpan, rowSpan))
        {
            return false;
        }

        card.GridColumn = col;
        card.GridRow = row;
        card.GridColumnSpan = colSpan;
        card.GridRowSpan = rowSpan;
        return true;
    }

    private bool TryPlaceFirstAvailable(DashboardCard card, int colSpan, int rowSpan)
    {
        if (Columns <= 0 || Rows <= 0)
        {
            return false;
        }

        colSpan = Math.Clamp(colSpan, 1, Columns);
        rowSpan = Math.Clamp(rowSpan, 1, Rows);

        for (var row = 0; row <= Rows - rowSpan; row++)
        {
            for (var col = 0; col <= Columns - colSpan; col++)
            {
                if (CanPlace(card, col, row, colSpan, rowSpan))
                {
                    card.GridColumn = col;
                    card.GridRow = row;
                    card.GridColumnSpan = colSpan;
                    card.GridRowSpan = rowSpan;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryPlaceBestAvailableWithin(DashboardCard card, int maxColSpan, int maxRowSpan)
    {
        if (Columns <= 0 || Rows <= 0)
        {
            return false;
        }

        maxColSpan = Math.Clamp(maxColSpan, 1, Columns);
        maxRowSpan = Math.Clamp(maxRowSpan, 1, Rows);

        for (var area = maxColSpan * maxRowSpan - 1; area >= 1; area--)
        {
            for (var rowSpan = maxRowSpan; rowSpan >= 1; rowSpan--)
            {
                for (var colSpan = maxColSpan; colSpan >= 1; colSpan--)
                {
                    if (colSpan * rowSpan != area)
                    {
                        continue;
                    }

                    if (TryPlaceFirstAvailable(card, colSpan, rowSpan))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private sealed class HiddenLayout
    {
        public int Col { get; init; }
        public int Row { get; init; }
        public int ColSpan { get; init; }
        public int RowSpan { get; init; }
        public int ExpandedColSpan { get; init; }
        public int ExpandedRowSpan { get; init; }
        public bool IsCollapsed { get; init; }

        public static HiddenLayout FromCard(DashboardCard card)
        {
            return new HiddenLayout
            {
                Col = card.GridColumn,
                Row = card.GridRow,
                ColSpan = card.GridColumnSpan,
                RowSpan = card.GridRowSpan,
                ExpandedColSpan = card.ExpandedColumnSpan,
                ExpandedRowSpan = card.ExpandedRowSpan,
                IsCollapsed = card.IsCollapsed
            };
        }
    }

    private void OnCardMaximizeStateChanged(object? sender, bool isMaximized)
    {
        if (sender is not DashboardCard card || _isApplyingMaximize)
        {
            return;
        }

        if (isMaximized)
        {
            ApplyMaximize(card);
        }
        else
        {
            RestoreMaximize(card);
        }
    }

    private void EnsureMaximizedCardState()
    {
        if (_maximizedCard != null)
        {
            return;
        }

        var maximized = Children.OfType<DashboardCard>()
            .FirstOrDefault(card => card.IsMaximized);

        if (maximized != null)
        {
            ApplyMaximize(maximized);
        }
    }


    private void ApplyMaximize(DashboardCard card)
    {
        if (_maximizedCard != null && !ReferenceEquals(_maximizedCard, card))
        {
            _isApplyingMaximize = true;
            _maximizedCard.IsMaximized = false;
            _isApplyingMaximize = false;
            RestoreMaximize(_maximizedCard);
        }

        if (ReferenceEquals(_maximizedCard, card))
        {
            return;
        }

        _suppressLayoutSave = true;
        _maximizedCard = card;
        _maximizedLayout = HiddenLayout.FromCard(card);
        card.IsMaximizeTransitionActive = true;
        card.ZIndex = 100;

        card.IsCollapsed = false;

        var fromRect = _lastArranged.TryGetValue(card, out var last)
            ? last
            : GetRectFromGrid(
                ClampIndex(card.GridColumn, Columns),
                ClampIndex(card.GridRow, Rows),
                Math.Max(1, card.GridColumnSpan),
                Math.Max(1, card.GridRowSpan));

        var toRect = new Rect(
            Padding.Left,
            Padding.Top,
            Math.Max(0, Bounds.Width - Padding.Left - Padding.Right),
            Math.Max(0, Bounds.Height - Padding.Top - Padding.Bottom));

        _suppressLayoutSave = false;
        StartRectTransition(card, fromRect, toRect, () =>
        {
            foreach (var other in Children.OfType<DashboardCard>())
            {
                if (!ReferenceEquals(other, card))
                {
                    other.IsVisible = false;
                }
            }
        });
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void RestoreMaximize(DashboardCard card)
    {
        if (!ReferenceEquals(_maximizedCard, card))
        {
            return;
        }

        _suppressLayoutSave = true;

        if (_maximizedLayout != null)
        {
            card.ExpandedColumnSpan = Math.Max(1, _maximizedLayout.ExpandedColSpan);
            card.ExpandedRowSpan = Math.Max(1, _maximizedLayout.ExpandedRowSpan);
            card.IsCollapsed = _maximizedLayout.IsCollapsed;
        }

        foreach (var other in Children.OfType<DashboardCard>())
        {
            if (!ReferenceEquals(other, card))
            {
                other.IsVisible = true;
            }
        }

        card.IsMaximizeTransitionActive = true;
        card.ZIndex = 100;

        var fromRect = _lastArranged.TryGetValue(card, out var last)
            ? last
            : new Rect(
                Padding.Left,
                Padding.Top,
                Math.Max(0, Bounds.Width - Padding.Left - Padding.Right),
                Math.Max(0, Bounds.Height - Padding.Top - Padding.Bottom));

        var toRect = GetRectFromGrid(
            ClampIndex(card.GridColumn, Columns),
            ClampIndex(card.GridRow, Rows),
            Math.Max(1, card.GridColumnSpan),
            Math.Max(1, card.GridRowSpan));

        StartRectTransition(card, fromRect, toRect, () =>
        {
            card.IsMaximizeTransitionActive = false;
            card.ZIndex = 0;
            _maximizedCard = null;
            _maximizedLayout = null;
            _suppressLayoutSave = false;
            InvalidateMeasure();
            InvalidateArrange();
        });
    }

    private void UpdateDragPreview(int col, int row, int colSpan, int rowSpan)
    {
        var metrics = GetMetrics(Bounds.Size);
        if (metrics.CellPitchX <= 0 || metrics.CellPitchY <= 0)
        {
            ClearDragPreview();
            return;
        }

        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        colSpan = Math.Clamp(colSpan, 1, columns - col);
        rowSpan = Math.Clamp(rowSpan, 1, rows - row);

        var x = Padding.Left + col * metrics.CellPitchX;
        var y = Padding.Top + row * metrics.CellPitchY;

        var w = colSpan * metrics.CellWidth + (colSpan - 1) * CellSpacing;
        var h = rowSpan * metrics.CellHeight + (rowSpan - 1) * CellSpacing;

        _dragPreviewRect = new Rect(x, y, Math.Max(0, w), Math.Max(0, h));
        EnsureDragPreviewResources();

        if (_dragPreviewBorder != null)
        {
            _dragPreviewBorder.IsVisible = true;
        }

        InvalidateArrange();
    }

    private void ClearDragPreview()
    {
        if (_dragPreviewRect == null)
        {
            return;
        }

        _dragPreviewRect = null;
        if (_dragPreviewBorder != null)
        {
            _dragPreviewBorder.IsVisible = false;
        }

        InvalidateArrange();
    }

    private void EnsureDragPreviewResources()
    {
        if (_dragPreviewBorder == null)
        {
            return;
        }

        if (_dragPreviewFill != null && _dragPreviewStroke != null)
        {
            return;
        }

        if (TryGetResource("SukiPrimaryColor25", ActualThemeVariant, out var fill))
        {
            _dragPreviewFill = fill switch
            {
                IBrush brush => brush,
                Color color => new SolidColorBrush(color),
                _ => _dragPreviewFill
            };
        }

        _dragPreviewFill ??= new SolidColorBrush(Color.FromArgb(40, 64, 128, 255));

        if (TryGetResource("SukiPrimaryColor", ActualThemeVariant, out var stroke))
        {
            _dragPreviewStroke = stroke switch
            {
                IBrush brush => brush,
                Color color => new SolidColorBrush(color),
                _ => _dragPreviewStroke
            };
        }

        _dragPreviewStroke ??= new SolidColorBrush(Colors.DodgerBlue);

        _dragPreviewBorder.Background = _dragPreviewFill;
        _dragPreviewBorder.BorderBrush = _dragPreviewStroke;
    }

    private void ArrangeDragPreview()
    {
        if (_dragPreviewBorder == null)
        {
            return;
        }

        if (_dragPreviewRect is { } rect && _dragPreviewBorder.IsVisible)
        {
            _dragPreviewBorder.Arrange(rect);
        }
        else
        {
            _dragPreviewBorder.Arrange(new Rect(0, 0, 0, 0));
        }
    }

    private void QueueTransitionFromLastRect(DashboardCard card)
    {
        if (_lastArranged.TryGetValue(card, out var rect)
            && rect.Width > 0
            && rect.Height > 0)
        {
            _pendingTransitions[card] = rect;
        }
    }

    private void UpdateLastArranged(DashboardCard card, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        _lastArranged[card] = rect;
    }
    private void HandlePendingTransition(DashboardCard card, Rect rect)
    {
        if (!_pendingTransitions.TryGetValue(card, out var from))
        {
            return;
        }

        _pendingTransitions.Remove(card);
        StartRectTransition(card, from, rect);
    }

    private void StartRectTransition(DashboardCard card, Rect from, Rect to, Action? onCompleted = null)
    {
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0)
        {
            return;
        }

        if (from == to)
        {
            return;
        }

        if (_transitionTokens.TryGetValue(card, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
            _transitionTokens.Remove(card);
        }

        var cts = new CancellationTokenSource();
        _transitionTokens[card] = cts;

        _activeRectAnimations[card] = from;

        var duration = TimeSpan.FromMilliseconds(LayoutTransitionDurationMs);
        var stopwatch = Stopwatch.StartNew();
        var token = cts.Token;

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    timer.Stop();
                    CleanupTransition(card, token);
                    return;
                }

                var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
                _activeRectAnimations[card] = LerpRect(from, to, progress);
                InvalidateArrange();

                if (progress >= 1d)
                {
                    timer.Stop();
                    _activeRectAnimations.Remove(card);
                    CleanupTransition(card, token);
                    try
                    {
                        onCompleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LoggerHelper.Error($"DashboardCardGrid animation completion failed: {ex.Message}", ex);
                    }
                    InvalidateArrange();
                }
            }
            catch (Exception ex)
            {
                timer.Stop();
                _activeRectAnimations.Remove(card);
                CleanupTransition(card, token);
                LoggerHelper.Error($"DashboardCardGrid animation tick failed: {ex.Message}", ex);
            }
        };

        token.Register(() => timer.Stop());
        timer.Start();
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }

    private static Rect LerpRect(Rect from, Rect to, double progress)
    {
        return new Rect(
            Lerp(from.X, to.X, progress),
            Lerp(from.Y, to.Y, progress),
            Lerp(from.Width, to.Width, progress),
            Lerp(from.Height, to.Height, progress));
    }

    private void CleanupTransition(DashboardCard card, CancellationToken token)
    {
        if (_transitionTokens.TryGetValue(card, out var cts) && cts.Token == token)
        {
            cts.Dispose();
            _transitionTokens.Remove(card);
        }
    }

    private void EnsureDragPreviewHost()
    {
        if (_dragPreviewBorder == null)
        {
            return;
        }

        if (_isUpdatingPreviewHost)
        {
            return;
        }

        var index = Children.IndexOf(_dragPreviewBorder);
        if (index == -1)
        {
            _isUpdatingPreviewHost = true;
            Children.Add(_dragPreviewBorder);
            _isUpdatingPreviewHost = false;
            return;
        }

        if (index != Children.Count - 1)
        {
            _isUpdatingPreviewHost = true;
            Children.RemoveAt(index);
            Children.Add(_dragPreviewBorder);
            _isUpdatingPreviewHost = false;
        }
    }

    private void ApplyMaximizeOpacityMask(Rect? maximizedRect, Dictionary<DashboardCard, Rect> arrangedRects)
    {
        if (maximizedRect is not { } maxRect)
        {
            foreach (var card in arrangedRects.Keys)
            {
                card.OpacityMask = null;
            }
            return;
        }

        foreach (var (card, rect) in arrangedRects)
        {
            if (card.IsMaximized || ReferenceEquals(card, _maximizedCard))
            {
                card.OpacityMask = null;
                continue;
            }

            var intersection = rect.Intersect(maxRect);
            if (intersection.Width <= 0 || intersection.Height <= 0)
            {
                card.OpacityMask = null;
                continue;
            }

            var topLeft = this.TranslatePoint(intersection.Position, card);
            var bottomRight = this.TranslatePoint(intersection.BottomRight, card);
            if (topLeft == null || bottomRight == null)
            {
                card.OpacityMask = null;
                continue;
            }

            var left = Math.Min(topLeft.Value.X, bottomRight.Value.X);
            var top = Math.Min(topLeft.Value.Y, bottomRight.Value.Y);
            var right = Math.Max(topLeft.Value.X, bottomRight.Value.X);
            var bottom = Math.Max(topLeft.Value.Y, bottomRight.Value.Y);

            var localIntersect = new Rect(new Point(left, top), new Point(right, bottom));
            var cardRect = new Rect(0, 0, rect.Width, rect.Height);

            var clipGeometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(cardRect),
                new RectangleGeometry(localIntersect));

            card.OpacityMask = new DrawingBrush
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Drawing = new GeometryDrawing
                {
                    Brush = Brushes.White,
                    Geometry = clipGeometry
                }
            };
        }
    }


    private void EnsureSplitters()
    {
        var required = Math.Max(0, Columns - 1);

        for (var i = _columnSplitters.Count - 1; i >= required; i--)
        {
            var splitter = _columnSplitters[i];
            splitter.DragDelta -= OnSplitterDragDelta;
            splitter.DragStarted -= OnSplitterDragStarted;
            splitter.DragCompleted -= OnSplitterDragCompleted;
            splitter.PointerEntered -= OnSplitterPointerEntered;
            splitter.PointerExited -= OnSplitterPointerExited;
            _columnSplitters.RemoveAt(i);
            Children.Remove(splitter);
        }

        for (var i = _columnSplitters.Count; i < required; i++)
        {
            var splitter = new Thumb
            {
                Tag = i,
                IsVisible = false,
                Background = Brushes.Transparent,
                ZIndex = 20,
                Opacity = 0,
                Classes =
                {
                    "DashboardGridSplitter"
                }
            };
            splitter.PointerEntered += OnSplitterPointerEntered;
            splitter.PointerExited += OnSplitterPointerExited;
            splitter.DragDelta += OnSplitterDragDelta;
            splitter.DragStarted += OnSplitterDragStarted;
            splitter.DragCompleted += OnSplitterDragCompleted;
            _columnSplitters.Add(splitter);
            Children.Add(splitter);
        }

        for (var i = 0; i < _columnSplitters.Count; i++)
        {
            _columnSplitters[i].Tag = i;
        }
    }

    private void EnsureRowSplitters()
    {
        if (_isRowSplitterDragging)
        {
            return;
        }

        var layouts = BuildRowSplitterLayouts();
        SyncRowSplitters(layouts);
    }

    private void SyncRowSplitters(IReadOnlyList<RowSplitterLayout> layouts)
    {
        if (_isSyncingRowSplitters)
        {
            return;
        }

        _isSyncingRowSplitters = true;
        try
        {
            var required = Math.Max(0, layouts.Count);

            for (var i = _rowSplitters.Count - 1; i >= required; i--)
            {
                var splitter = _rowSplitters[i];
                splitter.DragDelta -= OnRowSplitterDragDelta;
                splitter.DragStarted -= OnRowSplitterDragStarted;
                splitter.DragCompleted -= OnRowSplitterDragCompleted;
                splitter.PointerEntered -= OnRowSplitterPointerEntered;
                splitter.PointerExited -= OnRowSplitterPointerExited;
                _rowSplitters.RemoveAt(i);
                Children.Remove(splitter);
            }

            for (var i = _rowSplitters.Count; i < required; i++)
            {
                var splitter = new Thumb
                {
                    Tag = null,
                    IsVisible = false,
                    Background = Brushes.Transparent,
                    ZIndex = 20,
                    Opacity = 0,
                    Classes =
                    {
                        "DashboardGridSplitterHorizontal"
                    }
                };
                splitter.PointerEntered += OnRowSplitterPointerEntered;
                splitter.PointerExited += OnRowSplitterPointerExited;
                splitter.DragDelta += OnRowSplitterDragDelta;
                splitter.DragStarted += OnRowSplitterDragStarted;
                splitter.DragCompleted += OnRowSplitterDragCompleted;
                _rowSplitters.Add(splitter);
                Children.Add(splitter);
            }

            var assignCount = Math.Min(required, _rowSplitters.Count);
            for (var i = 0; i < assignCount; i++)
            {
                _rowSplitters[i].Tag = layouts[i];
            }
        }
        finally
        {
            _isSyncingRowSplitters = false;
        }
    }

    private void EnsureSplitterHost()
    {
        if (_isUpdatingSplitterHost)
        {
            return;
        }

        if (_columnSplitters.Count == 0 && _rowSplitters.Count == 0)
        {
            return;
        }

        _isUpdatingSplitterHost = true;
        foreach (var splitter in _columnSplitters)
        {
            var index = Children.IndexOf(splitter);
            if (index == -1)
            {
                Children.Add(splitter);
            }
        }

        foreach (var splitter in _rowSplitters)
        {
            var index = Children.IndexOf(splitter);
            if (index == -1)
            {
                Children.Add(splitter);
            }
        }
        _isUpdatingSplitterHost = false;
    }

    private void ArrangeColumnSplitters(Size finalSize)
    {
        if (_columnSplitters.Count == 0)
        {
            return;
        }

        var metrics = GetMetrics(finalSize);
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        if (metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            foreach (var splitter in _columnSplitters)
            {
                splitter.Arrange(new Rect(0, 0, 0, 0));
            }
            return;
        }

        var height = rows * metrics.CellHeight + (rows - 1) * CellSpacing;
        var top = Padding.Top;

        for (var i = 0; i < _columnSplitters.Count; i++)
        {
            var splitter = _columnSplitters[i];
            var isActive = _isSplitterDragging && ReferenceEquals(splitter, _activeSplitterThumb);
            SplitterLayout? layout = null;

            if (isActive)
            {
                layout = _activeSplitterLayout;
            }
            else if (!TryBuildSplitterLayout(i, out var resolved))
            {
                splitter.IsVisible = false;
                splitter.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            else
            {
                layout = resolved;
            }

            if (layout == null)
            {
                splitter.IsVisible = false;
                splitter.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            splitter.IsVisible = true;
            var boundaryOffset = isActive ? _activeSplitterContext?.LastDelta ?? 0 : 0;
            var boundary = Math.Clamp(i + boundaryOffset, 0, Math.Max(0, Columns - 2));
            var centerX = Padding.Left + (boundary + 1) * metrics.CellPitchX - CellSpacing * 0.5;
            var width = Math.Max(12, CellSpacing);
            var x = centerX - width / 2;

            var leftMin = int.MaxValue;
            var leftMax = int.MinValue;
            var rightMin = int.MaxValue;
            var rightMax = int.MinValue;

            foreach (var card in layout.LeftCards)
            {
                var row = ClampIndex(card.GridRow, rows);
                var span = Math.Clamp(Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan), 1, rows - row);
                leftMin = Math.Min(leftMin, row);
                leftMax = Math.Max(leftMax, row + span);
            }

            foreach (var card in layout.RightCards)
            {
                var row = ClampIndex(card.GridRow, rows);
                var span = Math.Clamp(Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan), 1, rows - row);
                rightMin = Math.Min(rightMin, row);
                rightMax = Math.Max(rightMax, row + span);
            }

            if (leftMin != int.MaxValue && leftMin == rightMin && leftMax == rightMax && rightMax > rightMin)
            {
                var y = Padding.Top + leftMin * metrics.CellPitchY;
                var h = (leftMax - leftMin) * metrics.CellHeight + (leftMax - leftMin - 1) * CellSpacing;
                splitter.Arrange(new Rect(x, y, width, Math.Max(0, h)));
            }
            else
            {
                splitter.Arrange(new Rect(x, top, width, Math.Max(0, height)));
            }
        }
    }

    private void ArrangeRowSplitters(Size finalSize)
    {
        var layouts = _isRowSplitterDragging
            ? _rowSplitters.Select(splitter => splitter.Tag).OfType<RowSplitterLayout>().ToList()
            : BuildRowSplitterLayouts();

        if (layouts.Count == 0)
        {
            foreach (var splitter in _rowSplitters)
            {
                splitter.Arrange(new Rect(0, 0, 0, 0));
            }
            return;
        }

        if (!_isRowSplitterDragging && _rowSplitters.Count != layouts.Count)
        {
            SyncRowSplitters(layouts);
        }

        var metrics = GetMetrics(finalSize);
        var columns = Math.Max(1, Columns);

        if (metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            foreach (var splitter in _rowSplitters)
            {
                splitter.Arrange(new Rect(0, 0, 0, 0));
            }
            return;
        }

        for (var i = 0; i < layouts.Count && i < _rowSplitters.Count; i++)
        {
            var splitter = _rowSplitters[i];
            var layout = layouts[i];

            splitter.IsVisible = true;
            if (!_isRowSplitterDragging)
            {
                splitter.Tag = layout;
            }

            var boundaryOffset = _isRowSplitterDragging && ReferenceEquals(splitter, _activeRowSplitterThumb)
                ? _activeRowSplitterContext?.LastDelta ?? 0
                : 0;
            var boundary = Math.Clamp(layout.Boundary + boundaryOffset, 0, Math.Max(0, Rows - 2));
            var centerY = Padding.Top + (boundary + 1) * metrics.CellPitchY - CellSpacing * 0.5;
            var height = Math.Max(12, CellSpacing);
            var y = centerY - height / 2;

            var startColumn = Math.Clamp(layout.StartColumn, 0, Math.Max(0, columns - 1));
            var span = Math.Clamp(layout.ColumnSpan, 1, Math.Max(1, columns - startColumn));
            var x = Padding.Left + startColumn * metrics.CellPitchX;
            var w = span * metrics.CellWidth + (span - 1) * CellSpacing;

            splitter.Arrange(new Rect(x, y, Math.Max(0, w), height));
        }
    }

    private void OnSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        if (sender is not Thumb splitter || splitter.Tag is not int boundary)
        {
            return;
        }

        if (!TryBuildSplitterLayout(boundary, out var layout))
        {
            return;
        }

        _isSplitterDragging = true;
        _activeSplitterThumb = splitter;
        _activeSplitterLayout = layout;
        splitter.ZIndex = 60;
        splitter.Opacity = 1;
        _activeSplitterContext = SplitterDragContext.FromLayout(layout, Columns);
        _activeSplitterContext.LastDelta = 0;
    }

    private void OnRowSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        if (sender is not Thumb splitter || splitter.Tag is not RowSplitterLayout layout)
        {
            return;
        }

        _isRowSplitterDragging = true;
        _activeRowSplitterThumb = splitter;
        splitter.ZIndex = 60;
        splitter.Opacity = 1;
        _activeRowSplitterContext = RowSplitterDragContext.FromLayout(layout, Rows);
        _activeRowSplitterContext.LastDelta = 0;
    }

    private void OnSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (_activeSplitterContext == null)
        {
            return;
        }

        var metrics = GetMetrics(Bounds.Size);
        if (metrics.CellPitchX <= 0)
        {
            return;
        }

        var rawDelta = (int)Math.Round(e.Vector.X / metrics.CellPitchX);
        var totalDelta = _activeSplitterContext.LastDelta + rawDelta;
        var clamped = _activeSplitterContext.ClampDelta(totalDelta);

        if (clamped == _activeSplitterContext.LastDelta)
        {
            return;
        }

        _activeSplitterContext.ApplyDelta(clamped);
        _activeSplitterContext.LastDelta = clamped;

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnRowSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        if (_activeRowSplitterContext == null)
        {
            return;
        }

        var metrics = GetMetrics(Bounds.Size);
        if (metrics.CellPitchY <= 0)
        {
            return;
        }

        var rawDelta = (int)Math.Round(e.Vector.Y / metrics.CellPitchY);
        var totalDelta = _activeRowSplitterContext.LastDelta + rawDelta;
        var clamped = _activeRowSplitterContext.ClampDelta(totalDelta);

        if (clamped == _activeRowSplitterContext.LastDelta)
        {
            return;
        }

        _activeRowSplitterContext.ApplyDelta(clamped);
        _activeRowSplitterContext.LastDelta = clamped;

        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_activeSplitterContext == null)
        {
            return;
        }

        _isSplitterDragging = false;
        if (sender is Thumb splitter)
        {
            splitter.ZIndex = 20;
            splitter.Opacity = splitter.IsPointerOver ? 1 : 0;
        }

        SaveLayouts();
        _activeSplitterContext = null;
        _activeSplitterLayout = null;
        _activeSplitterThumb = null;
        EnsureSplitters();
        EnsureSplitterHost();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void OnRowSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_activeRowSplitterContext == null)
        {
            return;
        }

        _isRowSplitterDragging = false;
        if (sender is Thumb splitter)
        {
            splitter.ZIndex = 20;
            splitter.Opacity = splitter.IsPointerOver ? 1 : 0;
        }

        SaveLayouts();
        _activeRowSplitterContext = null;
        _activeRowSplitterThumb = null;
        EnsureRowSplitters();
        EnsureSplitterHost();
        InvalidateMeasure();
        InvalidateArrange();
    }


    private void OnSplitterPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Thumb splitter)
        {
            splitter.Opacity = 1;
        }
    }

    private void OnSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_isSplitterDragging)
        {
            return;
        }

        if (sender is Thumb splitter)
        {
            splitter.Opacity = 0;
        }
    }

    private void OnRowSplitterPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Thumb splitter)
        {
            splitter.Opacity = 1;
        }
    }

    private void OnRowSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_isRowSplitterDragging)
        {
            return;
        }

        if (sender is Thumb splitter)
        {
            splitter.Opacity = 0;
        }
    }

    private bool TryBuildSplitterLayout(int boundary, out SplitterLayout layout)
    {
        layout = new SplitterLayout(boundary);
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        if (boundary < 0 || boundary >= columns - 1)
        {
            return false;
        }

        var leftCards = new Dictionary<int, DashboardCard>();
        var rightCards = new Dictionary<int, DashboardCard>();

        foreach (var card in Children.OfType<DashboardCard>())
        {
            if (!card.IsVisible)
            {
                continue;
            }

            var col = ClampIndex(card.GridColumn, columns);
            var row = ClampIndex(card.GridRow, rows);
            var colSpan = Math.Clamp(Math.Max(1, card.GridColumnSpan), 1, columns - col);
            var rowSpan = Math.Clamp(Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan), 1, rows - row);

            var leftEdge = col;
            var rightEdge = col + colSpan - 1;

            if (leftEdge <= boundary && rightEdge >= boundary + 1)
            {
                return false;
            }

            if (rightEdge == boundary || leftEdge == boundary + 1)
            {
                for (var r = row; r < row + rowSpan; r++)
                {
                    if (r < 0 || r >= rows)
                    {
                        continue;
                    }

                    if (rightEdge == boundary)
                    {
                        if (leftCards.TryGetValue(r, out var existing) && !ReferenceEquals(existing, card))
                        {
                            return false;
                        }

                        leftCards[r] = card;
                    }

                    if (leftEdge == boundary + 1)
                    {
                        if (rightCards.TryGetValue(r, out var existing) && !ReferenceEquals(existing, card))
                        {
                            return false;
                        }

                        rightCards[r] = card;
                    }
                }
            }
        }

        var rowsWithBoth = 0;
        for (var r = 0; r < rows; r++)
        {
            var hasLeft = leftCards.ContainsKey(r);
            var hasRight = rightCards.ContainsKey(r);

            if (hasLeft && hasRight)
            {
                rowsWithBoth++;
            }
        }

        if (rowsWithBoth == 0)
        {
            return false;
        }

        layout.LeftCards = leftCards.Values.Distinct().ToList();
        layout.RightCards = rightCards.Values.Distinct().ToList();
        return layout.LeftCards.Count > 0 && layout.RightCards.Count > 0;
    }

    private List<RowSplitterLayout> BuildRowSplitterLayouts()
    {
        var layouts = new List<RowSplitterLayout>();
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);

        if (rows < 2)
        {
            return layouts;
        }

        for (var boundary = 0; boundary < rows - 1; boundary++)
        {
            var topCards = new Dictionary<int, DashboardCard>();
            var bottomCards = new Dictionary<int, DashboardCard>();

            foreach (var card in Children.OfType<DashboardCard>())
            {
                if (!card.IsVisible)
                {
                    continue;
                }

                var col = ClampIndex(card.GridColumn, columns);
                var row = ClampIndex(card.GridRow, rows);
                var colSpan = Math.Clamp(Math.Max(1, card.GridColumnSpan), 1, columns - col);
                var rowSpan = Math.Clamp(Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan), 1, rows - row);

                var topEdge = row;
                var bottomEdge = row + rowSpan - 1;

                if (topEdge <= boundary && bottomEdge >= boundary + 1)
                {
                    continue;
                }

                if (bottomEdge == boundary || topEdge == boundary + 1)
                {
                    for (var c = col; c < col + colSpan; c++)
                    {
                        if (c < 0 || c >= columns)
                        {
                            continue;
                        }

                        if (bottomEdge == boundary)
                        {
                            if (topCards.TryGetValue(c, out var existing) && !ReferenceEquals(existing, card))
                            {
                                topCards.Clear();
                                bottomCards.Clear();
                                goto NextBoundary;
                            }

                            topCards[c] = card;
                        }

                        if (topEdge == boundary + 1)
                        {
                            if (bottomCards.TryGetValue(c, out var existing) && !ReferenceEquals(existing, card))
                            {
                                topCards.Clear();
                                bottomCards.Clear();
                                goto NextBoundary;
                            }

                            bottomCards[c] = card;
                        }
                    }
                }
            }

            var groupedPairs = new Dictionary<(int Col, int Span), (DashboardCard Top, DashboardCard Bottom)>();

            for (var c = 0; c < columns; c++)
            {
                if (!topCards.TryGetValue(c, out var top) || !bottomCards.TryGetValue(c, out var bottom))
                {
                    continue;
                }

                var topCol = ClampIndex(top.GridColumn, columns);
                var topSpan = Math.Clamp(Math.Max(1, top.GridColumnSpan), 1, columns - topCol);

                if (topCol != ClampIndex(bottom.GridColumn, columns) || topSpan != Math.Clamp(Math.Max(1, bottom.GridColumnSpan), 1, columns - ClampIndex(bottom.GridColumn, columns)))
                {
                    continue;
                }

                groupedPairs.TryAdd((topCol, topSpan), (top, bottom));
            }

            foreach (var group in groupedPairs.OrderBy(item => item.Key.Col).ThenBy(item => item.Key.Span))
            {
                var layout = new RowSplitterLayout(boundary)
                {
                    StartColumn = group.Key.Col,
                    ColumnSpan = group.Key.Span,
                    TopCards = new List<DashboardCard>
                    {
                        group.Value.Top
                    },
                    BottomCards = new List<DashboardCard>
                    {
                        group.Value.Bottom
                    }
                };
                layouts.Add(layout);
            }

            NextBoundary:
            continue;
        }

        return layouts;
    }

    private sealed class SplitterLayout
    {
        public SplitterLayout(int boundary)
        {
            Boundary = boundary;
        }

        public int Boundary { get; }
        public List<DashboardCard> LeftCards { get; set; } = new();
        public List<DashboardCard> RightCards { get; set; } = new();
    }

    private sealed class RowSplitterLayout
    {
        public RowSplitterLayout(int boundary)
        {
            Boundary = boundary;
        }

        public int Boundary { get; }
        public int StartColumn { get; set; }
        public int ColumnSpan { get; set; }
        public List<DashboardCard> TopCards { get; set; } = new();
        public List<DashboardCard> BottomCards { get; set; } = new();
    }

    private sealed class SplitterDragContext
    {
        public int Boundary { get; init; }
        public int LastDelta { get; set; }
        public int MinDelta { get; init; }
        public int MaxDelta { get; init; }
        public Dictionary<DashboardCard, CardSnapshot> LeftCards { get; init; } = new();
        public Dictionary<DashboardCard, CardSnapshot> RightCards { get; init; } = new();

        public int ClampDelta(int delta)
        {
            return Math.Clamp(delta, MinDelta, MaxDelta);
        }

        public void ApplyDelta(int delta)
        {
            foreach (var (card, snapshot) in LeftCards)
            {
                card.GridColumnSpan = snapshot.ColumnSpan + delta;
            }

            foreach (var (card, snapshot) in RightCards)
            {
                card.GridColumn = snapshot.Column + delta;
                card.GridColumnSpan = snapshot.ColumnSpan - delta;
            }
        }

        public static SplitterDragContext FromLayout(SplitterLayout layout, int columns)
        {
            var leftSnapshots = new Dictionary<DashboardCard, CardSnapshot>();
            var rightSnapshots = new Dictionary<DashboardCard, CardSnapshot>();

            var minDelta = int.MinValue;
            var maxDelta = int.MaxValue;

            foreach (var card in layout.LeftCards)
            {
                var span = Math.Max(1, card.GridColumnSpan);
                leftSnapshots[card] = new CardSnapshot(card.GridColumn, span);
                minDelta = Math.Max(minDelta, 1 - span);
            }

            foreach (var card in layout.RightCards)
            {
                var span = Math.Max(1, card.GridColumnSpan);
                rightSnapshots[card] = new CardSnapshot(card.GridColumn, span);
                maxDelta = Math.Min(maxDelta, span - 1);
            }

            minDelta = Math.Max(minDelta, -(layout.Boundary + 1));
            maxDelta = Math.Min(maxDelta, columns - 1 - layout.Boundary);

            return new SplitterDragContext
            {
                Boundary = layout.Boundary,
                MinDelta = minDelta,
                MaxDelta = maxDelta,
                LeftCards = leftSnapshots,
                RightCards = rightSnapshots
            };
        }
    }

    private sealed class RowSplitterDragContext
    {
        public int Boundary { get; init; }
        public int LastDelta { get; set; }
        public int MinDelta { get; init; }
        public int MaxDelta { get; init; }
        public Dictionary<DashboardCard, RowSnapshot> TopCards { get; init; } = new();
        public Dictionary<DashboardCard, RowSnapshot> BottomCards { get; init; } = new();

        public int ClampDelta(int delta)
        {
            return Math.Clamp(delta, MinDelta, MaxDelta);
        }

        public void ApplyDelta(int delta)
        {
            foreach (var (card, snapshot) in TopCards)
            {
                card.GridRowSpan = snapshot.RowSpan + delta;
            }

            foreach (var (card, snapshot) in BottomCards)
            {
                card.GridRow = snapshot.Row + delta;
                card.GridRowSpan = snapshot.RowSpan - delta;
            }
        }

        public static RowSplitterDragContext FromLayout(RowSplitterLayout layout, int rows)
        {
            var topSnapshots = new Dictionary<DashboardCard, RowSnapshot>();
            var bottomSnapshots = new Dictionary<DashboardCard, RowSnapshot>();

            var minDelta = int.MinValue;
            var maxDelta = int.MaxValue;

            foreach (var card in layout.TopCards)
            {
                var span = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);
                topSnapshots[card] = new RowSnapshot(card.GridRow, span);
                minDelta = Math.Max(minDelta, 1 - span);
            }

            foreach (var card in layout.BottomCards)
            {
                var span = Math.Max(1, card.IsCollapsed ? 1 : card.GridRowSpan);
                bottomSnapshots[card] = new RowSnapshot(card.GridRow, span);
                maxDelta = Math.Min(maxDelta, span - 1);
            }

            minDelta = Math.Max(minDelta, -(layout.Boundary + 1));
            maxDelta = Math.Min(maxDelta, rows - 1 - layout.Boundary);

            return new RowSplitterDragContext
            {
                Boundary = layout.Boundary,
                MinDelta = minDelta,
                MaxDelta = maxDelta,
                TopCards = topSnapshots,
                BottomCards = bottomSnapshots
            };
        }
    }

    private sealed class CardSnapshot
    {
        public CardSnapshot(int column, int columnSpan)
        {
            Column = column;
            ColumnSpan = columnSpan;
        }

        public int Column { get; }
        public int ColumnSpan { get; }
    }

    private sealed class RowSnapshot
    {
        public RowSnapshot(int row, int rowSpan)
        {
            Row = row;
            RowSpan = rowSpan;
        }

        public int Row { get; }
        public int RowSpan { get; }
    }
}
