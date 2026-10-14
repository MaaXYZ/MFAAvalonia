using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Markdown.Avalonia
{
    /// <summary>
    /// 虚拟化 Markdown 面板，只渲染可见区域的内容
    /// </summary>
    public class VirtualizingMarkdownPanel : Panel, ILogicalScrollable
    {
        private readonly List<DocumentElement> _allElements = new();
        private readonly Dictionary<int, Control> _realizedElements = new();
        private readonly Dictionary<Control, int> _controlIndexMap = new();
        private readonly Dictionary<DocumentElement, int> _elementIndexMap = new();
        private readonly List<double> _elementHeights = new();
        private readonly List<double> _elementOffsets = new();
        private readonly HashSet<int> _measuredElements = new();
        private readonly HeightFenwickTree _heightIndex = new();

        // 动态估计的元素高度（基于已测量元素的平均值）
        private double _estimatedItemHeight = 50;
        private double _totalMeasuredHeight = 0;
        private int _measuredCount = 0;
        private int _nextEstimateRebuildSample = 1;

        private readonly record struct ScrollAnchor(int Index, double OffsetWithinElement);

        private Size _extent;
        private Vector _offset;
        private Size _viewport;
        private bool _canHorizontallyScroll;
        private bool _canVerticallyScroll = true;

        // ISelectionRenderHelper 支持
        private ISelectionRenderHelper? _selectionHelper;

        /// <summary>
        /// 是否启用虚拟化
        /// </summary>
        public static readonly StyledProperty<bool> IsVirtualizingProperty =
            AvaloniaProperty.Register<VirtualizingMarkdownPanel, bool>(nameof(IsVirtualizing), true);

        public bool IsVirtualizing
        {
            get => GetValue(IsVirtualizingProperty);
            set => SetValue(IsVirtualizingProperty, value);
        }

        /// <summary>
        /// 预加载的额外元素数量（上下各多少个）
        /// </summary>
        public static readonly StyledProperty<int> OverScanCountProperty =
            AvaloniaProperty.Register<VirtualizingMarkdownPanel, int>(nameof(OverScanCount), 5);

        public int OverScanCount
        {
            get => GetValue(OverScanCountProperty);
            set => SetValue(OverScanCountProperty, value);
        }

        /// <summary>
        /// 选择渲染助手，用于支持文本选择功能
        /// </summary>
        public ISelectionRenderHelper? SelectionHelper
        {
            get => _selectionHelper;
            set
            {
                _selectionHelper = value;
                // 更新所有已实现元素的 Helper
                foreach (var element in _allElements)
                {
                    element.Helper = value;
                }
            }
        }

        static VirtualizingMarkdownPanel()
        {
            AffectsMeasure<VirtualizingMarkdownPanel>(IsVirtualizingProperty, OverScanCountProperty);
        }

        /// <summary>
        /// 设置文档元素
        /// </summary>
        public void SetElements(IEnumerable<DocumentElement> elements)
        {
            // 清理旧元素
            ClearRealizedElements();
            _allElements.Clear();
            _elementIndexMap.Clear();
            _elementHeights.Clear();
            _elementOffsets.Clear();
            _measuredElements.Clear();
            _totalMeasuredHeight = 0;
            _measuredCount = 0;
            _nextEstimateRebuildSample = 1;

            // 添加新元素
            _allElements.AddRange(elements);
            for (var index = 0; index < _allElements.Count; index++)
                _elementIndexMap[_allElements[index]] = index;

            // 设置 Helper
            foreach (var element in _allElements)
            {
                element.Helper = _selectionHelper;
            }

            // 初始化高度估计
            double offset = 0;
            foreach (var element in _allElements)
            {
                _elementHeights.Add(_estimatedItemHeight);
                _elementOffsets.Add(offset);
                offset += _estimatedItemHeight;
            }

            _heightIndex.Rebuild(_elementHeights);

            // 更新范围
            UpdateExtent();
            InvalidateMeasure();
        }

        /// <summary>
        /// 追加文档元素
        /// </summary>
        public void AppendElements(IEnumerable<DocumentElement> elements)
        {
            double offset = _heightIndex.Total;

            foreach (var element in elements)
            {
                // 设置 Helper
                element.Helper = _selectionHelper;

                _elementIndexMap[element] = _allElements.Count;
                _allElements.Add(element);
                _elementHeights.Add(_estimatedItemHeight);
                _elementOffsets.Add(offset);
                offset += _estimatedItemHeight;
            }

            _heightIndex.Rebuild(_elementHeights);

            UpdateExtent();
            InvalidateMeasure();
        }

        /// <summary>
        /// 清除所有元素
        /// </summary>
        public void Clear()
        {
            ClearRealizedElements();
            _allElements.Clear();
            _elementIndexMap.Clear();
            _elementHeights.Clear();
            _elementOffsets.Clear();
            _measuredElements.Clear();
            _totalMeasuredHeight = 0;
            _measuredCount = 0;_extent = default;
            _nextEstimateRebuildSample = 1;
            _heightIndex.Clear();
            _offset = default;
            InvalidateScrollable();
            InvalidateMeasure();
        }

        /// <summary>
        /// 清除所有已实现的元素
        /// </summary>
        private void ClearRealizedElements()
        {
            foreach (var kvp in _realizedElements)
            {
                var control = kvp.Value;
                control.SizeChanged -= OnElementSizeChanged;
                Children.Remove(control);
                _allElements[kvp.Key].ReleaseControl();
            }
            _realizedElements.Clear();
            _controlIndexMap.Clear();
        }

        /// <summary>
        /// 更新范围大小
        /// </summary>
        private void UpdateExtent()
        {
            double totalHeight = _heightIndex.Total;

            var newExtent = new Size(Bounds.Width > 0 ? Bounds.Width : 100, totalHeight);
            if (_extent != newExtent)
            {
                _extent = newExtent;
                InvalidateScrollable();
            }
        }

        /// <summary>
        /// 更新估计的元素高度（基于已测量元素的平均值）
        /// </summary>
        private void UpdateEstimatedHeight(double measuredHeight)
        {
            _totalMeasuredHeight += measuredHeight;
            _measuredCount++;
            var newEstimate = _totalMeasuredHeight / _measuredCount;
            if (_measuredCount < _nextEstimateRebuildSample ||
                Math.Abs(_estimatedItemHeight - newEstimate) <= 0.5)
                return;

            _estimatedItemHeight = newEstimate;

            // Keep the yet-unseen part of the document in the same height
            // model as the measured part. Otherwise a long document keeps the
            // original 50px estimates forever and the scrollbar can map a
            // viewport offset to a much later element than intended.
            for (var i = 0; i < _elementHeights.Count; i++)
            {
                if (!_measuredElements.Contains(i))
                    _elementHeights[i] = _estimatedItemHeight;
            }

            _heightIndex.Rebuild(_elementHeights);
            RebuildCompatibilityOffsets();
            _nextEstimateRebuildSample = _measuredCount >= 64
                ? int.MaxValue
                : Math.Min(64, _measuredCount * 4);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_allElements.Count == 0)
            {
                return new Size(0, 0);
            }

            if (!IsVirtualizing)
            {
                // 非虚拟化模式：测量所有元素
                return MeasureAllElements(availableSize);
            }

            // 虚拟化模式：只测量可见元素
            return MeasureVisibleElements(availableSize);
        }

        private Size MeasureAllElements(Size availableSize)
        {
            double totalHeight = 0;
            double maxWidth = 0;

            // 确保所有元素都已实现
            for (int i = 0; i < _allElements.Count; i++)
            {
                var control = RealizeElement(i);
                if (control != null)
                {
                    control.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                    var desiredSize = control.DesiredSize;

                    // 更新实际高度
                    if (i < _elementHeights.Count)
                    {
                        SetElementHeight(i, desiredSize.Height);
                        _elementOffsets[i] = totalHeight;
                    }

                    totalHeight += desiredSize.Height;
                    maxWidth = Math.Max(maxWidth, desiredSize.Width);
                }
            }

            _extent = new Size(maxWidth, totalHeight);
            InvalidateScrollable();

            return new Size(
                double.IsInfinity(availableSize.Width) ? maxWidth : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? totalHeight : availableSize.Height
            );
        }

        private Size MeasureVisibleElements(Size availableSize)
        {
            var anchor = CaptureScrollAnchor();

            // 计算可见范围
            double viewportTop = _offset.Y;
            double viewportBottom = viewportTop + (double.IsInfinity(availableSize.Height) ? 1000 : availableSize.Height);

            // 找到可见元素的索引范围
            int startIndex = FindElementIndexAtOffset(viewportTop);
            int endIndex = FindElementIndexAtOffset(viewportBottom);

            // 添加预加载
            startIndex = Math.Max(0, startIndex - OverScanCount);
            endIndex = Math.Min(_allElements.Count - 1, endIndex + OverScanCount);

            // 回收不可见的元素
            var indicesToRemove = _realizedElements.Keys
                .Where(i => i < startIndex || i > endIndex)
                .ToList();

            foreach (var index in indicesToRemove)
            {
                VirtualizeElement(index);
            }

            // 实现可见元素
            double maxWidth = 0;
            bool heightsChanged = false;

            for (int i = startIndex; i <= endIndex && i < _allElements.Count; i++)
            {
                var control = RealizeElement(i);
                if (control != null)
                {
                    control.Measure(new Size(availableSize.Width, double.PositiveInfinity));
                    var desiredSize = control.DesiredSize;

                    // 更新实际高度（如果与估计值不同）
                    if (i < _elementHeights.Count)
                    {
                        double oldHeight = _elementHeights[i];
                        if (Math.Abs(oldHeight - desiredSize.Height) > 0.5)
                        {
                            // 如果是首次测量，更新估计高度
                            if (!_measuredElements.Contains(i))
                            {
                                _measuredElements.Add(i);
                                UpdateEstimatedHeight(desiredSize.Height);
                            }

                            SetElementHeight(i, desiredSize.Height);
                            heightsChanged = true;
                        }
                    }

                    maxWidth = Math.Max(maxWidth, desiredSize.Width);
                }
            }

            // 如果高度发生变化，重新计算所有偏移
            if (heightsChanged)
            {
                UpdateExtent();
                RestoreScrollAnchor(anchor);
                InvalidateMeasure();
                InvalidateArrange();
            }

            return new Size(
                double.IsInfinity(availableSize.Width) ? maxWidth : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height
            );
        }

        /// <summary>
        /// 重新计算所有元素的偏移量
        /// </summary>
        private void RecalculateOffsets()
        {
            _heightIndex.Rebuild(_elementHeights);
            RebuildCompatibilityOffsets();
        }

        private void RebuildCompatibilityOffsets()
        {
            double offset = 0;
            for (int i = 0; i < _elementOffsets.Count; i++)
            {
                _elementOffsets[i] = offset;
                offset += _elementHeights[i];
            }
        }

        private void SetElementHeight(int index, double height)
        {
            if (index < 0 || index >= _elementHeights.Count ||
                double.IsNaN(height) || height < 0)
                return;

            var oldHeight = _elementHeights[index];
            if (Math.Abs(oldHeight - height) <= 0.01)
                return;

            _elementHeights[index] = height;
            _heightIndex.Add(index, height - oldHeight);
        }

        private double GetElementOffset(int index)
            => _heightIndex.PrefixSum(index);

        private ScrollAnchor CaptureScrollAnchor()
        {
            if (_elementHeights.Count == 0)
                return new ScrollAnchor(-1, 0);

            var index = FindElementIndexAtOffset(_offset.Y);
            var withinElement = Math.Max(0, _offset.Y - GetElementOffset(index));
            return new ScrollAnchor(index, withinElement);
        }

        private void RestoreScrollAnchor(ScrollAnchor anchor)
        {
            if (anchor.Index < 0 ||
                anchor.Index >= _elementHeights.Count)
                return;

            var withinElement = Math.Min(
                anchor.OffsetWithinElement,
                Math.Max(0, _elementHeights[anchor.Index]));
            var maxY = Math.Max(0, _extent.Height - _viewport.Height);
            var anchoredY = Math.Clamp(
                GetElementOffset(anchor.Index) + withinElement,
                0,
                maxY);

            if (Math.Abs(_offset.Y - anchoredY) <= 0.01)
                return;

            _offset = new Vector(_offset.X, anchoredY);
            InvalidateScrollable();
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _viewport = finalSize;

            foreach (var kvp in _realizedElements)
            {
                int index = kvp.Key;
                var control = kvp.Value;

                if (index < _elementHeights.Count)
                {
                    double y = GetElementOffset(index) - _offset.Y;
                    // Never arrange a realized element below the height it asked
                    // for during measure.  In particular, multiline code blocks
                    // must not be clipped to the virtualization estimate (50px).
                    double height = Math.Max(_elementHeights[index], control.DesiredSize.Height);
                    control.Arrange(new Rect(0, y, finalSize.Width, height));
                }
            }

            return finalSize;
        }

        /// <summary>
        /// 实现（创建）指定索引的元素
        /// </summary>
        private Control? RealizeElement(int index)
        {
            if (index < 0 || index >= _allElements.Count)
                return null;

            if (_realizedElements.TryGetValue(index, out var existing))
                return existing;

            var element = _allElements[index];
            var control = element.Control;

            // 检查控件是否已经有父级
            if (control.Parent != null)
            {
                // 如果控件已经在其他地方，我们需要先移除它
                if (control.Parent is Panel parentPanel)
                {
                    parentPanel.Children.Remove(control);
                }
                else if (control.Parent is ContentControl contentControl)
                {
                    contentControl.Content = null;
                }
                else if (control.Parent is Decorator decorator)
                {
                    decorator.Child = null;
                }
            }

            if (!Children.Contains(control))
            {
                Children.Add(control);
            }

            _realizedElements[index] = control;
            _controlIndexMap[control] = index;
            control.SizeChanged -= OnElementSizeChanged;
            control.SizeChanged += OnElementSizeChanged;
            return control;
        }

        /// <summary>
        /// 虚拟化（移除）指定索引的元素
        /// </summary>
        private void VirtualizeElement(int index)
        {
            if (_realizedElements.TryGetValue(index, out var control))
            {
                control.SizeChanged -= OnElementSizeChanged;
                Children.Remove(control);
                _realizedElements.Remove(index);
                _controlIndexMap.Remove(control);
                _allElements[index].ReleaseControl();
            }
        }

        private void OnElementSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is not Control control)
                return;

            if (!_controlIndexMap.TryGetValue(control, out var index))
                return;

            if (index < 0 || index >= _elementHeights.Count)
                return;

            // Bounds may reflect the previous estimated arrangement.  Treat the
            // measured desired height as the lower bound so SizeChanged cannot
            // shrink multiline content back to the estimate.
            var newHeight = Math.Max(control.Bounds.Height, control.DesiredSize.Height);
            if (double.IsNaN(newHeight) || newHeight <= 0)
                return;

            var oldHeight = _elementHeights[index];
            if (Math.Abs(oldHeight - newHeight) <= 0.5)
                return;

            var anchor = CaptureScrollAnchor();

            if (!_measuredElements.Contains(index))
            {
                _measuredElements.Add(index);
                UpdateEstimatedHeight(newHeight);
            }

            SetElementHeight(index, newHeight);
            UpdateExtent();
            RestoreScrollAnchor(anchor);
            InvalidateMeasure();
            InvalidateArrange();
        }

        /// <summary>
        /// 查找指定偏移位置的元素索引
        /// </summary>
        private int FindElementIndexAtOffset(double offset)
        {
            if (_elementHeights.Count == 0)
                return 0;

            if (offset <= 0)
                return 0;

            if (offset >= _heightIndex.Total)
                return _elementHeights.Count - 1;

            // 二分查找
            return _heightIndex.FindIndexAtOffset(offset);
        }

        /// <summary>
        /// 获取指定索引的元素
        /// </summary>
        public DocumentElement? GetElement(int index)
        {
            if (index >= 0 && index < _allElements.Count)
                return _allElements[index];
            return null;
        }

        /// <summary>
        /// 获取元素数量
        /// </summary>
        public int ElementCount => _allElements.Count;

        /// <summary>
        /// 获取所有元素（用于文本选择等功能）
        /// </summary>
        public IReadOnlyList<DocumentElement> AllElements => _allElements;

        #region ILogicalScrollable 实现

        public bool CanHorizontallyScroll
        {
            get => _canHorizontallyScroll;
            set
            {
                if (_canHorizontallyScroll != value)
                {
                    _canHorizontallyScroll = value;
                    InvalidateScrollable();
                }
            }
        }

        public bool CanVerticallyScroll
        {
            get => _canVerticallyScroll;
            set
            {
                if (_canVerticallyScroll != value)
                {
                    _canVerticallyScroll = value;
                    InvalidateScrollable();
                }
            }
        }

        public bool IsLogicalScrollEnabled => IsVirtualizing && _allElements.Count > 0;

        public Size ScrollSize => new Size(16, _estimatedItemHeight);

        public Size PageScrollSize => new Size(_viewport.Width, Math.Max(_viewport.Height - _estimatedItemHeight, _estimatedItemHeight));

        public Size Extent => _extent;

        public Vector Offset
        {
            get => _offset;
            set
            {
                // 限制偏移范围
                var maxX = Math.Max(0, _extent.Width - _viewport.Width);
                var maxY = Math.Max(0, _extent.Height - _viewport.Height);
                var newOffset = new Vector(
                    Math.Max(0, Math.Min(value.X, maxX)),
                    Math.Max(0, Math.Min(value.Y, maxY))
                );

                if (_offset != newOffset)
                {
                    _offset = newOffset;
                    InvalidateMeasure();
                    InvalidateArrange();
                    InvalidateScrollable();
                }
            }
        }

        public Size Viewport => _viewport;

        public event EventHandler? ScrollInvalidated;

        public bool ScrollToElement(DocumentElement target)
        {
            if (!_elementIndexMap.TryGetValue(target, out var index) ||
                index >= _elementHeights.Count)
                return false;

            Offset = new Vector(_offset.X, GetElementOffset(index));
            return true;
        }

        public bool TryGetElementBounds(DocumentElement target, out Rect bounds)
        {
            if (!_elementIndexMap.TryGetValue(target, out var index) ||
                index < 0 ||
                index >= _elementHeights.Count)
            {
                bounds = default;
                return false;
            }

            bounds = new Rect(
                0,
                GetElementOffset(index),
                Math.Max(Bounds.Width, _viewport.Width),
                _elementHeights[index]);
            return true;
        }

        public bool BringIntoView(Control target, Rect targetRect)
        {
            // 查找目标元素的索引
            var current = target;
            while (current is not null)
            {
                if (_controlIndexMap.TryGetValue(current, out var index))
                {
                    double elementTop = GetElementOffset(index);
                    double elementBottom = elementTop + _elementHeights[index];

                    // 计算需要滚动到的位置
                    double newOffsetY = _offset.Y;

                    if (elementTop < _offset.Y)
                    {
                        // 元素在视口上方，滚动到元素顶部
                        newOffsetY = elementTop;
                    }
                    else if (elementBottom > _offset.Y + _viewport.Height)
                    {
                        // 元素在视口下方，滚动到元素底部可见
                        newOffsetY = elementBottom - _viewport.Height;
                    }

                    if (newOffsetY != _offset.Y)
                    {Offset = new Vector(_offset.X, newOffsetY);
                    }

                    return true;
                }

                current = current.Parent as Control;
            }

            return false;
        }

        public Control? GetControlInDirection(NavigationDirection direction, Control? from)
        {
            if (from == null || _allElements.Count == 0)
                return null;

            // 查找当前元素的索引
            int currentIndex = -1;
            var current = from;
            while (current is not null)
            {
                if (_controlIndexMap.TryGetValue(current, out currentIndex))
                {
                    break;
                }

                current = current.Parent as Control;
            }

            if (currentIndex < 0)
                return null;

            int targetIndex = currentIndex;
            switch (direction)
            {
                case NavigationDirection.Up:
                case NavigationDirection.Previous:
                    targetIndex = currentIndex - 1;
                    break;
                case NavigationDirection.Down:
                case NavigationDirection.Next:
                    targetIndex = currentIndex + 1;
                    break;
                case NavigationDirection.First:
                    targetIndex = 0;
                    break;
                case NavigationDirection.Last:
                    targetIndex = _allElements.Count - 1;
                    break;
            }

            if (targetIndex >= 0 && targetIndex < _allElements.Count)
            {
                // 确保目标元素已实现
                var control = RealizeElement(targetIndex);
                return control;
            }

            return null;
        }

        public void RaiseScrollInvalidated(EventArgs e)
        {
            ScrollInvalidated?.Invoke(this, e);
        }

        private void InvalidateScrollable()
        {
            ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        }

        #endregion

        #region 文本选择支持

        /// <summary>
        /// 选择指定范围内的文本
        /// </summary>
        public void Select(Point from, Point to)
        {
            // 调整坐标以考虑滚动偏移
            var adjustedFrom = new Point(from.X, from.Y + _offset.Y);
            var adjustedTo = new Point(to.X, to.Y + _offset.Y);

            // 找到涉及的元素范围
            int startIndex = FindElementIndexAtOffset(Math.Min(adjustedFrom.Y, adjustedTo.Y));
            int endIndex = FindElementIndexAtOffset(Math.Max(adjustedFrom.Y, adjustedTo.Y));

            // 确保这些元素已实现
            for (int i = startIndex; i <= endIndex && i < _allElements.Count; i++)
            {
                RealizeElement(i);
            }

            // 对每个涉及的元素调用 Select
            for (int i = startIndex; i <= endIndex && i < _allElements.Count; i++)
            {
                var element = _allElements[i];
                var elementTop = GetElementOffset(i);
                // 计算相对于元素的坐标
                var relativeFrom = new Point(from.X, adjustedFrom.Y - elementTop);
                var relativeTo = new Point(to.X, adjustedTo.Y - elementTop);

                element.Select(relativeFrom, relativeTo);
            }
        }

        /// <summary>
        /// 取消选择
        /// </summary>
        public void UnSelect()
        {
            foreach (var element in _allElements)
            {
                element.UnSelect();
            }
        }

        /// <summary>
        /// 获取选中的文本
        /// </summary>
        public string GetSelectedText()
        {
            var builder = new System.Text.StringBuilder();
            foreach (var element in _allElements)
            {
                element.ConstructSelectedText(builder);
            }
            return builder.ToString();
        }

        #endregion

        private sealed class HeightFenwickTree
        {
            private double[] _tree = [0d];

            public double Total => PrefixSum(_tree.Length - 1);

            public void Clear()
            {
                _tree = [0d];
            }

            public void Rebuild(IReadOnlyList<double> values)
            {
                _tree = new double[values.Count + 1];
                for (var index = 0; index < values.Count; index++)
                    Add(index, values[index]);
            }

            public void Add(int index, double delta)
            {
                for (var treeIndex = index + 1;
                     treeIndex < _tree.Length;
                     treeIndex += treeIndex & -treeIndex)
                {
                    _tree[treeIndex] += delta;
                }
            }

            public double PrefixSum(int count)
            {
                count = Math.Clamp(count, 0, _tree.Length - 1);
                var sum = 0d;
                for (var treeIndex = count;
                     treeIndex > 0;
                     treeIndex -= treeIndex & -treeIndex)
                {
                    sum += _tree[treeIndex];
                }
                return sum;
            }

            public int FindIndexAtOffset(double offset)
            {
                var index = 0;
                var accumulated = 0d;
                var bit = 1;
                while ((bit << 1) < _tree.Length)
                    bit <<= 1;

                for (; bit != 0; bit >>= 1)
                {
                    var next = index + bit;
                    if (next < _tree.Length && accumulated + _tree[next] <= offset)
                    {
                        index = next;
                        accumulated += _tree[next];
                    }
                }

                return Math.Min(index, Math.Max(0, _tree.Length - 2));
            }
        }
    }
}
