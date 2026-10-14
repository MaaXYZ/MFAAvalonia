using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.Threading;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.StyleCollections;
using Markdown.Avalonia.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MdStyle = Markdown.Avalonia.MarkdownStyle;

namespace Markdown.Avalonia
{
    public class MarkdownScrollViewer : Control, IDisposable
    {
        #region IDisposable 实现

        /// <summary>
        /// 跟踪是否已经释放资源
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// 释放资源的核心方法（清理模式）
        /// </summary>
        /// <param name="disposing">
        /// true: 由 Dispose() 调用，释放托管和非托管资源
        /// false: 由终结器调用，仅释放非托管资源
        /// </param>
        protected void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // 释放托管资源
                // 调用 Cleanup 方法来清理所有托管资源和事件订阅
                Cleanup();
            }

            // 释放非托管资源
            // CancellationTokenSource 包装了非托管资源，需要显式释放
            if (_progressiveRenderCts != null)
            {
                _progressiveRenderCts.Cancel();
                _progressiveRenderCts.Dispose();
                _progressiveRenderCts = null;
            }

            _disposed = true;
        }

        /// <summary>
        /// 实现 IDisposable.Dispose 方法
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            // 告诉 GC 不需要调用终结器，因为资源已经被释放
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 终结器（析构函数）- 作为安全网，防止用户忘记调用 Dispose
        /// </summary>
        ~MarkdownScrollViewer()
        {
            Dispose(disposing: false);
        }

        #endregion

        public static readonly DirectProperty<MarkdownScrollViewer, Uri?> SourceDirectProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, Uri?>(
                nameof(Source),
                o => o.Source,
                (o, v) => o.Source = v);

        public static readonly AvaloniaProperty<Uri?> SourceProperty = SourceDirectProperty;

        private static readonly DirectProperty<MarkdownScrollViewer, string?> MarkdownDirectProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, string?>(
                nameof(Markdown),
                o => o.Markdown,
                (o, v) => o.Markdown = v);

        public static readonly AvaloniaProperty<string?> MarkdownProperty = MarkdownDirectProperty;

        private static readonly AvaloniaProperty<IStyle> MarkdownStyleProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, IStyle>(
                nameof(MarkdownStyle),
                o => o.MarkdownStyle,
                (o, v) => o.MarkdownStyle = v);

        public static readonly AvaloniaProperty<string?> MarkdownStyleNameProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, string?>(
                nameof(MarkdownStyleName),
                o => o.MarkdownStyleName,
                (o, v) => o.MarkdownStyleName = v);

        public static readonly AvaloniaProperty<string?> AssetPathRootProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, string?>(
                nameof(AssetPathRoot),
                o => o.AssetPathRoot,
                (o, v) => o.AssetPathRoot = v);

        public static readonly StyledProperty<bool> SaveScrollValueWhenContentUpdatedProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, bool>(
                nameof(SaveScrollValueWhenContentUpdated),
                defaultValue: false);

        public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, ScrollBarVisibility>(
                nameof(HorizontalScrollBarVisibility),
                defaultValue: ScrollBarVisibility.Disabled);

        public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, ScrollBarVisibility>(
                nameof(VerticalScrollBarVisibility),
                defaultValue: ScrollBarVisibility.Auto);

        public static readonly StyledProperty<bool> EnableProgressiveRenderingProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, bool>(
                nameof(EnableProgressiveRendering),
                defaultValue: true);

        public static readonly StyledProperty<bool> EnableVirtualizationProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, bool>(
                nameof(EnableVirtualization),
                defaultValue: false); // 默认关闭虚拟化，因为可能影响文本选择

        public static readonly StyledProperty<int> InitialRenderLinesProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, int>(
                nameof(InitialRenderLines),
                defaultValue: 300); // 初始渲染300行，确保用户能看到足够内容

        public static readonly StyledProperty<int> ProgressiveRenderBatchSizeProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, int>(
                nameof(ProgressiveRenderBatchSize),
                defaultValue: 500); // 每批次渲染500行，加快渲染速度

        public static readonly StyledProperty<int> ProgressiveRenderDelayMsProperty =
            AvaloniaProperty.Register<MarkdownScrollViewer, int>(
                nameof(ProgressiveRenderDelayMs),
                defaultValue: 1); // 最小延迟，让 UI 有机会响应

        public static readonly AvaloniaProperty<Vector> ScrollValueProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, Vector>(
                nameof(ScrollValue),
                owner => owner.ScrollValue,
                (owner, v) => owner.ScrollValue = v);

        public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
            SelectableTextBlock.SelectionBrushProperty.AddOwner<MarkdownScrollViewer>();

        public static readonly AvaloniaProperty<bool> SelectionEnabledProperty =
            AvaloniaProperty.RegisterDirect<MarkdownScrollViewer, bool>(
                nameof(SelectionEnabled),
                owner => owner.SelectionEnabled,
                (owner, v) => owner.SelectionEnabled = v);

        private static readonly HttpClient s_httpclient = new();

        /// <summary>
        /// 预编译的换行符分割正则
        /// </summary>
        private static readonly Regex s_newlineSplitter = new(@"\r\n|\r|\n", RegexOptions.Compiled);

        #region 内容哈希（用于检测内容变化）

        /// <summary>
        /// 计算 Markdown 内容的哈希值（用于检测内容是否变化）
        /// </summary>
        private static string ComputeContentHash(string content)
        {
            return MarkdownDocumentCache.ComputeContentHash(content);
        }

        /// <summary>
        /// 清除文档缓存（已弃用：缓存 Avalonia 控件会导致内存泄漏）
        /// </summary>
        [Obsolete("文档缓存已禁用，因为缓存 Avalonia 控件会导致内存泄漏")]
        public static void ClearDocumentCache()
        {
            // 不再使用缓存
        }

        /// <summary>
        /// 获取当前缓存大小（已弃用）
        /// </summary>
        [Obsolete("文档缓存已禁用，因为缓存 Avalonia 控件会导致内存泄漏")]
        public static int CacheSize => 0;

        /// <summary>
        /// 获取缓存统计信息（已弃用）
        /// </summary>
        [Obsolete("文档缓存已禁用，因为缓存 Avalonia 控件会导致内存泄漏")]
        public static CacheStatistics GetCacheStatistics()
        {
            return new CacheStatistics();
        }

        #endregion

        #region 渐进式渲染

        /// <summary>
        /// 大文件阈值（行数），超过此值启用渐进式渲染
        /// </summary>
        private const int LargeFileThreshold = 200;
        private const int LargeFileCharacterThreshold = 512 * 1024;

        /// <summary>
        /// 当前渐进式渲染的取消令牌源
        /// </summary>
        private CancellationTokenSource? _progressiveRenderCts;

        /// <summary>
        /// 是否正在进行渐进式渲染
        /// </summary>
        private bool _isProgressiveRendering;

        /// <summary>
        /// 渐进式渲染完成事件
        /// </summary>
        public event EventHandler? ProgressiveRenderingCompleted;

        /// <summary>
        /// 渐进式渲染进度变化事件
        /// </summary>
        public event EventHandler<ProgressiveRenderingProgressEventArgs>? ProgressiveRenderingProgress;

        /// <summary>
        /// 已渲染的行数（用于增量渲染）
        /// </summary>
        private int _renderedLineCount;

        private readonly SemaphoreSlim _largeDocumentParseGate = new(1, 1);

        /// <summary>
        /// 渲染性能计时器
        /// </summary>
        private readonly Stopwatch _renderStopwatch = new Stopwatch();

        #endregion

        private readonly ScrollViewer _viewer;
        private SetupInfo _setup;
        private DocumentElement? _document;
        private IBrush? _selectionBrush;
        private Wrapper _wrapper;
        private IDocumentAnchorProvider? _anchorProvider;
        private string? _pendingAnchor;

        public MarkdownScrollViewer()
        {
            _plugins = new MdAvPlugins();
            _setup = Plugins.Info;

            var md = new Markdown();
            md.CascadeResources.SetParent(this);
            md.UseResource = _useResource;
            md.Plugins = _plugins;

            _engine = md;
            AttachAnchorProvider(_engine);

            if (nvl(ThemeDetector.IsFluentAvaloniaUsed))
            {
                _markdownStyleName = nameof(MdStyle.FluentAvalonia);
                _markdownStyle = MdStyle.FluentAvalonia;
            }
            else if (nvl(ThemeDetector.IsFluentUsed))
            {
                _markdownStyleName = nameof(MdStyle.FluentTheme);
                _markdownStyle = MdStyle.FluentTheme;
            }
            else if (nvl(ThemeDetector.IsSimpleUsed))
            {
                _markdownStyleName = nameof(MdStyle.SimpleTheme);
                _markdownStyle = MdStyle.SimpleTheme;
            }
            else
            {
                _markdownStyleName = nameof(MdStyle.Standard);
                _markdownStyle = MdStyle.Standard;
            }
            Styles.Insert(0, _markdownStyle);
            TrySetupSelectionBrush(_markdownStyle);

            _viewer = new ScrollViewer()
            {
                // TODO: ScrollViewer does not seem to take Padding into account in 11.0.0-preview1
                Padding = new Thickness(0),
            };
            _viewer.Bind(
                ScrollViewer.HorizontalScrollBarVisibilityProperty,
                this.GetObservable(HorizontalScrollBarVisibilityProperty));
            _viewer.Bind(
                ScrollViewer.VerticalScrollBarVisibilityProperty,
                this.GetObservable(VerticalScrollBarVisibilityProperty));

            ((ISetLogicalParent)_viewer).SetParent(this);
            VisualChildren.Add(_viewer);
            LogicalChildren.Add(_viewer);

            EditStyle(_markdownStyle);

            static bool nvl(bool? vl) => vl.HasValue && vl.Value;

            // 使用命名方法而非匿名 lambda，便于取消订阅
            _viewer.ScrollChanged += Viewer_ScrollChanged;
            _viewer.PointerPressed += _viewer_PointerPressed;
            _viewer.PointerMoved += _viewer_PointerMoved;
            _viewer.PointerReleased += _viewer_PointerReleased;

            _wrapper = new Wrapper(this);
            _wrapper.UseVirtualization = EnableVirtualization;
            _viewer.Content = _wrapper;
        }

        /// <summary>
        /// 清理所有资源，取消事件订阅
        /// </summary>
        public void Cleanup()
        {
            // 如果已经释放，直接返回
            if (_disposed)
                return;

            // 取消渐进式渲染（但不释放 CancellationTokenSource，由 Dispose 处理）
            _progressiveRenderCts?.Cancel();

            // 取消事件订阅
            _viewer.ScrollChanged -= Viewer_ScrollChanged;
            _viewer.PointerPressed -= _viewer_PointerPressed;
            _viewer.PointerMoved -= _viewer_PointerMoved;
            _viewer.PointerReleased -= _viewer_PointerReleased;

            // 清理文档内容
            if (_wrapper.ContentControl is Control contentControl)
            {
                contentControl.SizeChanged -= OnViewportSizeChanged;
            }

            // 清理 Wrapper（这会清理内部的文档和事件订阅）
            _wrapper.Cleanup();

            // 清理 ScrollViewer 的内容引用和从视觉树中移除
            _viewer.Content = null;
            VisualChildren.Remove(_viewer);
            LogicalChildren.Remove(_viewer);

            _document = null;
            _headerRects = null;
            _currentContentHash = null;
            _eventArgs = null;
            _markdown = null;
            _source = null;
            _AssetPathRoot = null;
            DetachAnchorProvider();

            // 清理样式引用
            if (_markdownStyle != null)
            {
                Styles.Remove(_markdownStyle);
                _markdownStyle = null!;
            }

            // 清理选择相关状态
            _selectionBrush = null;
            _isLeftButtonPressed = false;

            // 清理渐进式渲染事件
            ProgressiveRenderingCompleted = null;
            ProgressiveRenderingProgress = null;
            HeaderScrolled = null;

            // 清理 Markdown 引擎的 CascadeResources 引用，断开对父控件的引用
            _engine.CascadeResources.Parent = null;
            // 清理引擎的资源字典
            _engine.CascadeResources.Owner = new ResourceDictionary();

            // 清理插件引用
            _plugins = null!;
            _setup = null!;
        }


        #region text selection

        private bool _isLeftButtonPressed;
        private Point _startPoint;

        private void Viewer_ScrollChanged(object? sender, ScrollChangedEventArgs e) => OnScrollChanged();

        private void _viewer_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_document is null) return;
            if (!SelectionEnabled) return;

            // 获取用于坐标计算的控件（虚拟化模式下使用虚拟化面板）
            var targetControl = GetSelectionTargetControl();
            if (targetControl == null) return;

            var point = e.GetCurrentPoint(targetControl);
            if (point.Properties.IsLeftButtonPressed)
            {
                _isLeftButtonPressed = true;
                _startPoint = point.Position;
                PerformSelection(_startPoint, point.Position);

                this.Focus();
            }
        }

        private void _viewer_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_document is null) return;

            // 获取用于坐标计算的控件
            var targetControl = GetSelectionTargetControl();
            if (targetControl == null) return;

            var point = e.GetCurrentPoint(targetControl);
            if (_isLeftButtonPressed && point.Properties.IsLeftButtonPressed)
            {
                PerformSelection(_startPoint, point.Position);
            }
        }

        private void _viewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_document is null) return;

            // 获取用于坐标计算的控件
            var targetControl = GetSelectionTargetControl();
            if (targetControl == null) return;

            var point = e.GetCurrentPoint(targetControl);
            if (_isLeftButtonPressed && !point.Properties.IsLeftButtonPressed)
            {
                _isLeftButtonPressed = false;
                PerformSelection(_startPoint, point.Position);
            }
        }

        /// <summary>
        /// 获取用于文本选择坐标计算的目标控件
        /// </summary>
        private Control? GetSelectionTargetControl()
        {
            if (_wrapper.UseVirtualization && _wrapper.VirtualizingPanel != null)
            {
                return _wrapper.VirtualizingPanel;
            }
            return _document?.Control;
        }

        /// <summary>
        /// 执行文本选择
        /// </summary>
        private void PerformSelection(Point from, Point to)
        {
            if (_wrapper.UseVirtualization && _wrapper.VirtualizingPanel != null)
            {
                _wrapper.VirtualizingPanel.Select(from, to);
            }
            else if (_document != null)
            {
                _document.Select(from, to);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!SelectionEnabled) return;

            // Ctrl+C
            if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                string? selectedText = null;

                if (_wrapper.UseVirtualization && _wrapper.VirtualizingPanel != null)
                {
                    selectedText = _wrapper.VirtualizingPanel.GetSelectedText();
                }
                else if (_document != null)
                {
                    selectedText = _document.GetSelectedText();
                }

                if (!string.IsNullOrEmpty(selectedText)
                    && TopLevel.GetTopLevel(this) is TopLevel top
                    && top.Clipboard is IClipboard clipboard)
                {
                    clipboard.SetTextAsync(selectedText);
                }
            }
        }

        #endregion

        public event HeaderScrolled? HeaderScrolled;
        private List<HeaderRect>? _headerRects;
        private HeaderScrolledEventArgs? _eventArgs;

        private void OnViewportSizeChanged(object? obj, EventArgs arg)
        {
            _headerRects = null;
            _wrapper.Restructure();
        }

        private void OnScrollChanged()
        {
            if (HeaderScrolled is null) return;

            double offsetY = _viewer.Offset.Y;
            double viewHeight = _viewer.Viewport.Height;

            if (_headerRects is null)
            {
                if (_document is null) return;

                _headerRects = new List<HeaderRect>();
                foreach (var heading in _document.Children.OfType<IDocumentHeading>())
                {
                    var element = (DocumentElement)heading;
                    Rect rect;
                    if (_wrapper.VirtualizingPanel?.TryGetElementBounds(element, out var virtualBounds) == true)
                    {
                        rect = virtualBounds;
                    }
                    else
                    {
                        var visualBounds = element.GetRect(this);
                        rect = new Rect(
                            visualBounds.Left,
                            visualBounds.Top + offsetY,
                            visualBounds.Width,
                            visualBounds.Height);
                    }

                    _headerRects.Add(new HeaderRect(rect, heading.Level, heading.Text));
                }
            }

            var tree = new Header?[5];
            var viewing = new List<Header>();

            tree[0] = _headerRects.Where(rct => rct.Level == 1)
                .Select(rct => CreateHeader(rct))
                .FirstOrDefault();

            foreach (var headerRect in _headerRects)
            {
                var boundY = headerRect.BaseBound.Bottom - offsetY;

                if (boundY < 0)
                {
                    var header = CreateHeader(headerRect);
                    tree[header.Level - 1] = header;

                    for (var i = header.Level; i < tree.Length; ++i)
                        tree[i] = null;
                }
                else if (0 <= boundY && boundY < viewHeight)
                {
                    viewing.Add(CreateHeader(headerRect));
                }
                else break;
            }

            var newEvArg = new HeaderScrolledEventArgs(tree.OfType<Header>().ToList(), viewing);
            if (_eventArgs != newEvArg)
            {
                _eventArgs = newEvArg;
                HeaderScrolled(this, _eventArgs);
            }

            static Header CreateHeader(HeaderRect headerRect)
            {
                return new Header(headerRect.Level, headerRect.Text);
            }
        }

        private void EditStyle(IStyle mdstyle)
        {
            if (mdstyle is INamedStyle { IsEditted: false } nameStyle
                && mdstyle is Styles styles)
            {
                foreach (var edit in _setup.StyleEdits)
                    edit.Edit(nameStyle.Name, styles);

                nameStyle.IsEditted = true;
            }
        }

        private void TrySetupSelectionBrush(IStyle style)
        {
            _selectionBrush = null;

            var key = "MarkdownScrollViewer.SelectionBrush";
            if (style.TryGetResource(key, null, out var brushObj)
                && brushObj is IBrush brush)
            {
                _selectionBrush = brush;
            }
        }

        /// <summary>
        /// 当前内容的哈希值（用于缓存检查）
        /// </summary>
        private string? _currentContentHash;

        /// <summary>
        /// 是否启用渐进式渲染
        /// </summary>
        public bool EnableProgressiveRendering
        {
            get => GetValue(EnableProgressiveRenderingProperty);
            set => SetValue(EnableProgressiveRenderingProperty, value);
        }

        /// <summary>
        /// 是否启用虚拟化
        /// </summary>
        public bool EnableVirtualization
        {
            get => GetValue(EnableVirtualizationProperty);
            set
            {
                SetValue(EnableVirtualizationProperty, value);
                // 同步更新 Wrapper 的虚拟化设置
                if (_wrapper != null)
                {
                    _wrapper.UseVirtualization = value;
                }
            }
        }

        /// <summary>
        /// 初始渲染的行数
        /// </summary>
        public int InitialRenderLines
        {
            get => GetValue(InitialRenderLinesProperty);
            set => SetValue(InitialRenderLinesProperty, value);
        }

        /// <summary>
        /// 渐进式渲染每批次的行数
        /// </summary>
        public int ProgressiveRenderBatchSize
        {
            get => GetValue(ProgressiveRenderBatchSizeProperty);
            set => SetValue(ProgressiveRenderBatchSizeProperty, value);
        }

        /// <summary>
        /// 渐进式渲染批次之间的延迟（毫秒）
        /// </summary>
        public int ProgressiveRenderDelayMs
        {
            get => GetValue(ProgressiveRenderDelayMsProperty);
            set => SetValue(ProgressiveRenderDelayMsProperty, value);
        }

        /// <summary>
        /// 是否正在进行渐进式渲染
        /// </summary>
        public bool IsProgressiveRendering => _isProgressiveRendering;

        /// <summary>
        /// 取消当前的渐进式渲染
        /// </summary>
        public void CancelProgressiveRendering()
        {
            if (_progressiveRenderCts != null)
            {
                _progressiveRenderCts.Cancel();
                _progressiveRenderCts.Dispose();
                _progressiveRenderCts = null;
            }
            _isProgressiveRendering = false;
            _renderedLineCount = 0;
        }

        private void UpdateMarkdown()
        {
            if (String.IsNullOrEmpty(Markdown))
            {
                CancelProgressiveRendering();

                if (_wrapper.ContentControl is Control oldContentControl)
                {
                    oldContentControl.SizeChanged -= OnViewportSizeChanged;
                }

                _document = null;
                _wrapper.Document = null;
                _currentContentHash = null;
                _headerRects = null;
                return;
            }

            // 首先取消任何正在进行的渐进式渲染
            CancelProgressiveRendering();

            try
            {
                var markdownContent = Markdown ?? "";
                var contentHash = ComputeContentHash(markdownContent);

                // 如果内容哈希相同，无需更新
                if (_currentContentHash == contentHash && _wrapper.Document != null && !_isProgressiveRendering)
                {
                    System.Diagnostics.Debug.WriteLine("Markdown 内容未变化，跳过更新");
                    return;
                }

                var ofst = _viewer.Offset;

                // 注意：不再使用文档缓存，因为缓存 Avalonia 控件会导致内存泄漏
                // Avalonia 控件持有对样式系统的引用，缓存这些控件会阻止 GC 回收

                var totalLines = CountLines(markdownContent);
                bool shouldUseProgressiveRendering =
                    EnableProgressiveRendering &&
                    (totalLines > LargeFileThreshold ||
                     markdownContent.Length > LargeFileCharacterThreshold);
                _wrapper.UseVirtualization =
                    EnableVirtualization || shouldUseProgressiveRendering;

                if (shouldUseProgressiveRendering)
                {
                    StartProgressiveRendering(markdownContent, totalLines, contentHash, ofst);
                    return;
                }

                var newDocument = _engine.TransformElement(markdownContent);
                PrepareDocumentForDisplay(newDocument);

                // 全量更新
                _document = newDocument;
                _currentContentHash = contentHash;

                if (_wrapper.ContentControl is Control oldContentControl)
                {
                    oldContentControl.SizeChanged -= OnViewportSizeChanged;
                }

                _wrapper.Document = _document;

                if (_wrapper.ContentControl is Control newContentControl)
                {
                    newContentControl.SizeChanged += OnViewportSizeChanged;
                }

                _headerRects = null;

                if (SaveScrollValueWhenContentUpdated)
                    _viewer.Offset = ofst;

            }
            catch (StackOverflowException ex)
            {
                // 处理堆栈溢出异常（通常是由于 Markdown 内容深度嵌套导致的）
                System.Diagnostics.Debug.WriteLine($"Markdown 解析堆栈溢出: {ex.Message}");
                CreateErrorDocument("Markdown 内容解析失败：内容嵌套过深或格式有问题");
            }
            catch (RegexMatchTimeoutException ex)
            {
                // 处理正则表达式超时异常
                System.Diagnostics.Debug.WriteLine($"Markdown 正则表达式匹配超时: {ex.Message}");
                CreateErrorDocument("Markdown 内容解析失败：内容格式复杂，解析超时");
            }
            catch (OutOfMemoryException ex)
            {
                // 处理内存溢出异常（可能是由于大量递归或复杂正则导致的）
                System.Diagnostics.Debug.WriteLine($"Markdown 解析内存溢出: {ex.Message}");
                CreateErrorDocument("Markdown 内容解析失败：内容过大或格式过于复杂");
            }
            catch (Exception ex)
            {
                // 处理其他解析异常
                System.Diagnostics.Debug.WriteLine($"Markdown 解析失败: {ex.Message}\n{ex.StackTrace}");
                var errorMsg = ex.Message.Length > 100 ? ex.Message.Substring(0, 100) + "..." : ex.Message;
                CreateErrorDocument($"Markdown 内容解析失败: {errorMsg}");
            }
        }

        private void PrepareDocumentForDisplay(DocumentElement document)
        {
            // A virtualized root must stay purely semantic. Materializing its
            // StackPanel here would attach every child control to a hidden
            // parent before the virtualizing panel can realize it.
            if (_wrapper.UseVirtualization && document is DocumentRootElement)
                return;

            document.Control.Classes.Add("Markdown_Avalonia_MarkdownViewer");
        }
        /// <summary>
        /// Starts progressive realization from one authoritative full-document
        /// parse. Markdown is never split into independent parser inputs.
        /// </summary>
        private void StartProgressiveRendering(
            string fullContent,
            int totalLines,
            string contentHash,
            Vector savedOffset)
        {
            if (_disposed)
                return;

            CancelProgressiveRendering();

            _progressiveRenderCts = new CancellationTokenSource();
            _isProgressiveRendering = true;
            _renderedLineCount = 0;

            var ct = _progressiveRenderCts.Token;
            _renderStopwatch.Restart();

            if (_wrapper.UseVirtualization && _engine is Markdown markdownEngine)
            {
                Markdown.DeferredParseOptions options;
                try
                {
                    options = markdownEngine.PrepareDeferredParsing();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"大文档解析器初始化失败: {ex.Message}");
                    _isProgressiveRendering = false;
                    _renderStopwatch.Stop();
                    CreateErrorDocument($"Markdown 解析器初始化失败: {ex.Message}");
                    return;
                }

                _ = StartDeferredProgressiveRenderingAsync(
                    markdownEngine,
                    options,
                    fullContent,
                    contentHash,
                    totalLines,
                    savedOffset,
                    ct);
                return;
            }

            try
            {
                // Parse the complete source once so every element comes from
                // the same AST and retains document-wide context.
                var authoritativeDocument = _engine.TransformElement(fullContent);
                if (authoritativeDocument is not DocumentRootElement authoritativeRoot)
                {
                    PrepareDocumentForDisplay(authoritativeDocument);
                    ReplaceDocument(authoritativeDocument, savedOffset, SaveScrollValueWhenContentUpdated);
                    CompleteProgressiveRendering(contentHash, totalLines);
                    return;
                }

                var allElements = authoritativeRoot.Children.ToList();
                var ranges = CreateProgressiveElementRanges(
                    allElements.Count,
                    totalLines,
                    InitialRenderLines,
                    ProgressiveRenderBatchSize);

                if (ranges.Count == 0)
                {
                    PrepareDocumentForDisplay(authoritativeDocument);
                    ReplaceDocument(authoritativeDocument, savedOffset, SaveScrollValueWhenContentUpdated);
                    CompleteProgressiveRendering(contentHash, totalLines);
                    return;
                }

                var initialRange = ranges[0];
                var progressiveDocument = new DocumentRootElement(
                    allElements.Take(initialRange.Count));
                PrepareDocumentForDisplay(progressiveDocument);
                ReplaceDocument(progressiveDocument, savedOffset, SaveScrollValueWhenContentUpdated);

                _currentContentHash = null;
                _renderedLineCount = initialRange.RenderedLines;
                _headerRects = null;

                var elapsed = _renderStopwatch.ElapsedMilliseconds;
                System.Diagnostics.Debug.WriteLine(
                    $"初始挂载 {initialRange.Count}/{allElements.Count} 个完整块，耗时 {elapsed}ms");
                ProgressiveRenderingProgress?.Invoke(
                    this,
                    new ProgressiveRenderingProgressEventArgs(initialRange.RenderedLines, totalLines));

                if (ranges.Count > 1)
                {
                    _ = ContinueProgressiveRenderingOptimizedAsync(
                        progressiveDocument,
                        allElements,
                        ranges,
                        contentHash,
                        totalLines,
                        ct);
                }
                else
                {
                    CompleteProgressiveRendering(contentHash, totalLines);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"渐进式渲染初始化失败: {ex.Message}");
                _isProgressiveRendering = false;
                _renderStopwatch.Stop();

                try
                {
                    var fallbackDocument = _engine.TransformElement(fullContent);
                    PrepareDocumentForDisplay(fallbackDocument);
                    ReplaceDocument(fallbackDocument, savedOffset, SaveScrollValueWhenContentUpdated);
                    _currentContentHash = contentHash;
                    _headerRects = null;
                }
                catch (Exception fallbackEx)
                {
                    System.Diagnostics.Debug.WriteLine($"回退渲染也失败: {fallbackEx.Message}");
                    CreateErrorDocument($"Markdown 渲染失败: {fallbackEx.Message}");
                }
            }
        }

        internal static int CountLines(string text)
        {
            if (text.Length == 0)
                return 1;

            var count = 1;
            for (var index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n')
                {
                    count++;
                }
                else if (text[index] == '\r')
                {
                    count++;
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                        index++;
                }
            }
            return count;
        }

        private async Task StartDeferredProgressiveRenderingAsync(
            Markdown markdownEngine,
            Markdown.DeferredParseOptions options,
            string fullContent,
            string contentHash,
            int totalLines,
            Vector savedOffset,
            CancellationToken cancellationToken)
        {
            var gateEntered = false;
            try
            {
                await _largeDocumentParseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateEntered = true;
                cancellationToken.ThrowIfCancellationRequested();

                var plan = await Task.Run(
                    () => markdownEngine.BuildDeferredDocument(
                        fullContent,
                        options,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested ||
                        _disposed ||
                        !ReferenceEquals(_engine, markdownEngine))
                        return;

                    markdownEngine.ActivateDeferredDocument(plan);
                    PrepareDocumentForDisplay(plan.Document);
                    ReplaceDocument(
                        plan.Document,
                        savedOffset,
                        SaveScrollValueWhenContentUpdated);

                    _headerRects = null;
                    ProgressiveRenderingProgress?.Invoke(
                        this,
                        new ProgressiveRenderingProgressEventArgs(totalLines, totalLines));
                    CompleteProgressiveRendering(contentHash, totalLines);
                }, DispatcherPriority.Background, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("大文档解析被取消");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"大文档延迟解析失败: {ex.Message}\n{ex.StackTrace}");
                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            _isProgressiveRendering = false;
                            _renderStopwatch.Stop();
                            CreateErrorDocument($"Markdown 大文档解析失败: {ex.Message}");
                        }
                    }, DispatcherPriority.Background);
                }
            }
            finally
            {
                if (gateEntered)
                    _largeDocumentParseGate.Release();
            }
        }

        /// <summary>
        /// 异步挂载完整文档解析出的顶层元素。
        /// </summary>
        private async Task ContinueProgressiveRenderingOptimizedAsync(
            DocumentRootElement progressiveDocument,
            IReadOnlyList<DocumentElement> allElements,
            IReadOnlyList<ProgressiveElementRange> ranges,
            string contentHash,
            int totalLines,
            CancellationToken ct)
        {
            try
            {
                for (var rangeIndex = 1; rangeIndex < ranges.Count; rangeIndex++)
                {
                    if (ProgressiveRenderDelayMs > 0)
                        await Task.Delay(ProgressiveRenderDelayMs, ct);
                    else
                        await Task.Yield();

                    ct.ThrowIfCancellationRequested();
                    var range = ranges[rangeIndex];
                    var batch = allElements.Skip(range.Start).Take(range.Count).ToArray();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested ||
                            !ReferenceEquals(_document, progressiveDocument))
                            return;

                        // The root owns semantic order. The virtualizing panel is
                        // the sole visual owner when virtualization is enabled.
                        progressiveDocument.AppendElements(batch);
                        if (_wrapper.UseVirtualization && _wrapper.VirtualizingPanel is { } panel)
                            panel.AppendElements(batch);

                        _renderedLineCount = range.RenderedLines;
                        _headerRects = null;
                        ProgressiveRenderingProgress?.Invoke(
                            this,
                            new ProgressiveRenderingProgressEventArgs(range.RenderedLines, totalLines));
                    }, DispatcherPriority.Background, ct);
                }

                if (!ct.IsCancellationRequested && ReferenceEquals(_document, progressiveDocument))
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        () => CompleteProgressiveRendering(contentHash, totalLines),
                        DispatcherPriority.Background,
                        ct);
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("渐进式渲染被取消");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"渐进式挂载异常: {ex.Message}");

                // Keep the already displayed document stable and append every
                // missing authoritative block exactly once.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested ||
                        !ReferenceEquals(_document, progressiveDocument))
                        return;

                    var appended = progressiveDocument.Children.Count();
                    var remaining = allElements.Skip(appended).ToArray();
                    progressiveDocument.AppendElements(remaining);
                    if (_wrapper.UseVirtualization && _wrapper.VirtualizingPanel is { } panel)
                        panel.AppendElements(remaining);

                    CompleteProgressiveRendering(contentHash, totalLines);
                }, DispatcherPriority.Background);
            }
        }

        internal readonly record struct ProgressiveElementRange(
            int Start,
            int Count,
            int RenderedLines);

        internal static IReadOnlyList<ProgressiveElementRange> CreateProgressiveElementRanges(
            int totalElements,
            int totalLines,
            int initialLines,
            int batchLines)
        {
            if (totalElements <= 0)
                return Array.Empty<ProgressiveElementRange>();

            totalLines = Math.Max(1, totalLines);
            var initialCount = ScaleLineBudgetToElementCount(
                totalElements,
                totalLines,
                Math.Max(1, initialLines));
            var batchCount = ScaleLineBudgetToElementCount(
                totalElements,
                totalLines,
                Math.Max(1, batchLines));

            var ranges = new List<ProgressiveElementRange>();
            var start = 0;
            while (start < totalElements)
            {
                var count = Math.Min(start == 0 ? initialCount : batchCount, totalElements - start);
                var end = start + count;
                var renderedLines = end == totalElements
                    ? totalLines
                    : Math.Min(totalLines, Math.Max(1,
                        (int)Math.Ceiling(end * (double)totalLines / totalElements)));
                ranges.Add(new ProgressiveElementRange(start, count, renderedLines));
                start = end;
            }

            return ranges;
        }

        private static int ScaleLineBudgetToElementCount(
            int totalElements,
            int totalLines,
            int lineBudget)
        {
            return Math.Clamp(
                (int)Math.Ceiling(totalElements * (double)lineBudget / totalLines),
                1,
                totalElements);
        }

        private void CompleteProgressiveRendering(string contentHash, int totalLines)
        {
            _renderedLineCount = totalLines;
            _currentContentHash = contentHash;
            _isProgressiveRendering = false;
            _renderStopwatch.Stop();
            var totalTime = _renderStopwatch.ElapsedMilliseconds;
            System.Diagnostics.Debug.WriteLine(
                $"渐进式渲染完成，总计 {totalLines} 行，耗时 {totalTime}ms");
            TryNavigatePendingAnchor();
            ProgressiveRenderingCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void AttachAnchorProvider(IMarkdownEngine2 engine)
        {
            DetachAnchorProvider();
            if (engine is not IDocumentAnchorProvider provider)
                return;

            _anchorProvider = provider;
            provider.AnchorNavigationRequested = NavigateToDocumentAnchor;
        }

        private void DetachAnchorProvider()
        {
            if (_anchorProvider is not null &&
                _anchorProvider.AnchorNavigationRequested == NavigateToDocumentAnchor)
            {
                _anchorProvider.AnchorNavigationRequested = null;
            }
            _anchorProvider = null;
        }

        private void NavigateToDocumentAnchor(string anchor)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => NavigateToDocumentAnchor(anchor));
                return;
            }

            if (_anchorProvider?.TryGetDocumentAnchor(anchor, out var target) != true ||
                FindTopLevelTarget(_document, target) is not { } topLevelTarget)
            {
                _pendingAnchor = _isProgressiveRendering ? anchor : null;
                return;
            }

            _pendingAnchor = null;
            if (_wrapper.ScrollToElement(topLevelTarget))
            {
                // The top-level offset realizes virtualized content first; a
                // second pass then places a nested target such as a footnote item.
                Dispatcher.UIThread.Post(
                    () => target.Control.BringIntoView(),
                    DispatcherPriority.Loaded);
            }
            else
            {
                target.Control.BringIntoView();
            }
        }

        private void TryNavigatePendingAnchor()
        {
            if (_pendingAnchor is { } anchor)
                NavigateToDocumentAnchor(anchor);
        }

        private static DocumentElement? FindTopLevelTarget(DocumentElement? document, DocumentElement target)
        {
            if (document is null)
                return null;
            if (ReferenceEquals(document, target))
                return document;

            if (document is DocumentRootElement root)
            {
                foreach (var child in root.Children)
                {
                    if (ReferenceEquals(child, target) || ContainsDocumentElement(child, target))
                        return child;
                }
                return null;
            }

            return ContainsDocumentElement(document, target) ? document : null;

            static bool ContainsDocumentElement(DocumentElement parent, DocumentElement expected)
            {
                foreach (var child in parent.Children)
                {
                    if (ReferenceEquals(child, expected) || ContainsDocumentElement(child, expected))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 替换当前文档
        /// </summary>
        private void ReplaceDocument(
            DocumentElement newDocument,
            Vector? savedOffset = null,
            bool preserveOffset = false)
        {
            if (_wrapper.ContentControl is Control oldContentControl)
            {
                oldContentControl.SizeChanged -= OnViewportSizeChanged;
            }

            _document = newDocument;
            _wrapper.Document = _document;

            if (_wrapper.ContentControl is Control newContentControl)
            {
                newContentControl.SizeChanged += OnViewportSizeChanged;
            }

            if (preserveOffset && savedOffset is Vector offset)
            {
                Dispatcher.UIThread.Post(
                    () => _viewer.Offset = offset,
                    DispatcherPriority.Loaded);
            }
        }

        private void CreateErrorDocument(string errorMessage)
        {
            try
            {
                // 创建一个简单的文本块来显示错误消息
                var errorTextBlock = new TextBlock
                {
                    Text = errorMessage,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(10),
                    Foreground = Brushes.Red
                };

                var errorBorder = new Border
                {
                    Child = errorTextBlock,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(10)
                };

                // 使用 UnBlockElement 包装错误控件，然后创建 DocumentRootElement
                var errorElement = new UnBlockElement(errorBorder);
                var errorDocument = new DocumentRootElement(new[]
                {
                    errorElement
                });

                var ofst = _viewer.Offset;

                if (_wrapper.ContentControl is Control oldContentControl)
                {
                    oldContentControl.SizeChanged -= OnViewportSizeChanged;
                }

                _wrapper.Document = errorDocument;

                if (_wrapper.ContentControl is Control newContentControl)
                {
                    newContentControl.SizeChanged += OnViewportSizeChanged;
                }

                _headerRects = null;

                if (SaveScrollValueWhenContentUpdated)
                    _viewer.Offset = ofst;
            }
            catch
            {
                // 如果创建错误文档也失败，至少清空内容避免崩溃
                _wrapper.Document = null;
            }
        }

        private IMarkdownEngine2 _engine;

        public IMarkdownEngineBase Engine
        {
            set
            {
                if (value is null)
                    throw new ArgumentNullException(nameof(Engine));

                DetachAnchorProvider();

                if (value is IMarkdownEngine engine1)
                    _engine = engine1.Upgrade();
                else if (value is IMarkdownEngine2 engine)
                    _engine = engine;
                else
                    throw new ArgumentException();

                _engine.CascadeResources.SetParent(this);
                _engine.UseResource = _useResource;
                _engine.Plugins = _plugins;

                if (AssetPathRoot is not null)
                    _engine.AssetPathRoot = AssetPathRoot;

                AttachAnchorProvider(_engine);
            }
            get => _engine;
        }

        private string? _AssetPathRoot;

        public string? AssetPathRoot
        {
            set
            {
                if (value is not null)
                {
                    _engine.AssetPathRoot = _AssetPathRoot = value;
                    UpdateMarkdown();
                }
            }
            get => _AssetPathRoot;
        }

        public bool SaveScrollValueWhenContentUpdated
        {
            set => SetValue(SaveScrollValueWhenContentUpdatedProperty, value);
            get => GetValue(SaveScrollValueWhenContentUpdatedProperty);
        }

        public Vector ScrollValue
        {
            set => _viewer.Offset = value;
            get => _viewer.Offset;
        }

        private bool _selectionEnabled;

        public bool SelectionEnabled
        {
            set
            {
                Focusable = _selectionEnabled = value;
            }
            get => _selectionEnabled;
        }

        [Content]
        public string? HereMarkdown

        {
            get
            {
                return Markdown;
            }
            set
            {
                if (String.IsNullOrEmpty(value))
                {
                    Markdown = value;
                }
                else
                {
                    // like PHP's flexible_heredoc_nowdoc_syntaxes,
                    // The indentation of the closing tag dictates 
                    // the amount of whitespace to strip from each line 
                    var lines = s_newlineSplitter.Split(value);

                    // count last line indent
                    int lastIdtCnt = TextUtil.CountIndent(lines.Last());
                    // count full indent
                    int someIdtCnt = lines
                        .Where(line => !String.IsNullOrWhiteSpace(line))
                        .Select(line => TextUtil.CountIndent(line))
                        .Min();

                    var indentCount = Math.Max(lastIdtCnt, someIdtCnt);

                    Markdown = String.Join(
                        "\n",
                        lines
                            // skip first blank line
                            .Skip(String.IsNullOrWhiteSpace(lines[0]) ? 1 : 0)
                            // strip indent
                            .Select(line =>
                            {
                                var realIdx = 0;
                                var viewIdx = 0;

                                while (viewIdx < indentCount && realIdx < line.Length)
                                {
                                    var c = line[realIdx];
                                    if (c == ' ')
                                    {
                                        realIdx += 1;
                                        viewIdx += 1;
                                    }
                                    else if (c == '\t')
                                    {
                                        realIdx += 1;
                                        viewIdx = ((viewIdx >> 2) + 1) << 2;
                                    }
                                    else break;
                                }

                                return line.Substring(realIdx);
                            })
                    );
                }
            }
        }

        private string? _markdown;

        public string? Markdown
        {
            get
            {
                return _markdown;
            }
            set
            {
                if (SetAndRaise(MarkdownDirectProperty, ref _markdown, value))
                {
                    UpdateMarkdown();
                }
            }
        }

        private Uri? _source;

        public Uri? Source
        {
            get
            {
                return _source;
            }
            set
            {
                if (!SetAndRaise(SourceDirectProperty, ref _source, value))
                    return;

                if (value is null)
                {
                    _source = value;
                    Markdown = null;
                    return;
                }

                if (!value.IsAbsoluteUri)
                    throw new ArgumentException("it is not absolute.");

                _source = value;

                switch (_source.Scheme)
                {
                    case "http":
                    case "https":
                        using (var res = s_httpclient.GetAsync(_source).Result)
                        using (var strm = res.Content.ReadAsStreamAsync().Result)
                        using (var reader = new StreamReader(strm, true))
                            Markdown = reader.ReadToEnd();
                        break;

                    case "file":
                        using (var strm = File.OpenRead(_source.LocalPath))
                        using (var reader = new StreamReader(strm, true))
                            Markdown = reader.ReadToEnd();
                        break;

                    case "avares":
                        using (var strm = AssetLoader.Open(_source))
                        using (var reader = new StreamReader(strm, true))
                            Markdown = reader.ReadToEnd();
                        break;

                    default:
                        throw new ArgumentException($"unsupport schema {_source.Scheme}");
                }

                AssetPathRoot =
                    value.Scheme == "file" ? value.LocalPath : value.AbsoluteUri;
            }
        }

        private IStyle _markdownStyle;

        public IStyle MarkdownStyle
        {
            get
            {
                return _markdownStyle;
            }
            set
            {
                if (value is null)
                    throw new ArgumentNullException(nameof(MarkdownStyle));

                if (_markdownStyle != value)
                {
                    EditStyle(value);

                    if (_markdownStyle is not null)
                        Styles.Remove(_markdownStyle);

                    Styles.Insert(0, value);

                    TrySetupSelectionBrush(value);
                    //ResetContent();
                }

                _markdownStyle = value;
            }
        }

        private string? _markdownStyleName;

        public string? MarkdownStyleName
        {
            get
            {
                return _markdownStyleName;
            }
            set
            {
                _markdownStyleName = value;

                if (_markdownStyleName is null)
                {
                    MarkdownStyle =
                        nvl(ThemeDetector.IsFluentAvaloniaUsed) ? MdStyle.FluentAvalonia :
                        nvl(ThemeDetector.IsFluentUsed) ? MdStyle.FluentTheme :
                        nvl(ThemeDetector.IsSimpleUsed) ? MdStyle.SimpleTheme :
                        MdStyle.Standard;
                }
                else if (_markdownStyleName == "Empty")
                {
                    MarkdownStyle = new Styles();
                }
                else
                {
                    var prop = typeof(MarkdownStyle).GetProperty(_markdownStyleName);
                    if (prop is null) return;

                    var propVal = prop.GetValue(null) as IStyle;
                    if (propVal is null) return;

                    MarkdownStyle = propVal;
                }

                static bool nvl(bool? vl) => vl.HasValue && vl.Value;
            }
        }

        private MdAvPlugins _plugins;

        public MdAvPlugins Plugins
        {
            get => _plugins;
            set
            {
                _plugins = _engine.Plugins = value;
                _setup = Plugins.Info;

                EditStyle(MarkdownStyle);
                UpdateMarkdown();
            }
        }

        private bool _useResource;

        public bool UseResource
        {
            get => _useResource;
            set
            {
                _engine.UseResource = value;
                _useResource = value;
                UpdateMarkdown();
            }
        }

        public IBrush? SelectionBrush
        {
            get => GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        internal IBrush ComputedSelectionBrush => SelectionBrush ?? _selectionBrush ?? Brushes.Cyan;


        public ScrollViewer ScrollViewer => _viewer;

        class HeaderRect
        {
            public Rect BaseBound { get; }
            public int Level { get; }
            public string Text { get; }

            public HeaderRect(Rect bound, int level, string text)
            {
                BaseBound = bound;
                Level = level;
                Text = text;
            }
        }

        public ScrollBarVisibility HorizontalScrollBarVisibility
        {
            set => SetValue(HorizontalScrollBarVisibilityProperty, value);
            get => GetValue(HorizontalScrollBarVisibilityProperty);
        }

        public ScrollBarVisibility VerticalScrollBarVisibility
        {
            set => SetValue(VerticalScrollBarVisibilityProperty, value);
            get => GetValue(VerticalScrollBarVisibilityProperty);
        }

        class Wrapper : Control, ISelectionRenderHelper, ILogicalScrollable
        {
            private MarkdownScrollViewer? _viewer;
            private readonly Canvas _canvas;
            private readonly Dictionary<Control, Rectangle> _rects;
            private DocumentElement? _document;
            private VirtualizingMarkdownPanel? _virtualizingPanel;
            private bool _useVirtualization;

            public event EventHandler? ScrollInvalidated;

            /// <summary>
            /// 是否使用虚拟化模式
            /// </summary>
            public bool UseVirtualization
            {
                get => _useVirtualization;
                set
                {
                    if (_useVirtualization != value)
                    {
                        _useVirtualization = value;
                        // 如果已有文档，需要重新设置以应用新的虚拟化模式
                        if (_document != null)
                        {
                            var doc = _document;
                            Document = null;
                            Document = doc;
                        }
                    }
                }
            }

            public DocumentElement? Document
            {
                get => _document;
                set
                {
                    // 清理旧文档
                    if (_document is not null)
                    {
                        if (_useVirtualization && _virtualizingPanel != null)
                        {
                            // 虚拟化模式：清理虚拟化面板
                            _virtualizingPanel.ScrollInvalidated -= OnVirtualizingPanelScrollInvalidated;
                            _virtualizingPanel.Clear();
                            VisualChildren.Remove(_virtualizingPanel);
                            LogicalChildren.Remove(_virtualizingPanel);
                            _virtualizingPanel = null;
                        }
                        else
                        {
                            // 非虚拟化模式：直接移除文档控件
                            VisualChildren.Remove(_document.Control);
                            LogicalChildren.Remove(_document.Control);
                        }
                        _document.Helper = null;
                        Clear();
                    }

                    _document = value;

                    if (_document is not null)
                    {
                        if (_useVirtualization && _document is DocumentRootElement rootElement)
                        {
                            // 虚拟化模式：使用 VirtualizingMarkdownPanel
                            _virtualizingPanel = new VirtualizingMarkdownPanel
                            {
                                SelectionHelper = this
                            };
                            _virtualizingPanel.ScrollInvalidated += OnVirtualizingPanelScrollInvalidated;
                            _virtualizingPanel.SetElements(rootElement.Children);
                            _virtualizingPanel.Classes.Add("Markdown_Avalonia_MarkdownViewer");

                            VisualChildren.Insert(0, _virtualizingPanel);
                            LogicalChildren.Insert(0, _virtualizingPanel);

                            // 设置 Helper 到所有元素
                            foreach (var child in rootElement.Children)
                            {
                                child.Helper = this;
                            }
                        }
                        else
                        {
                            // 非虚拟化模式：直接使用文档控件
                            VisualChildren.Insert(0, _document.Control);
                            LogicalChildren.Insert(0, _document.Control);
                            _document.Helper = this;
                        }
                        InvalidateMeasure();
                    }
                }
            }

            /// <summary>
            /// 获取用于布局的主控件（虚拟化面板或文档控件）
            /// </summary>
            public Control? ContentControl => _useVirtualization ? _virtualizingPanel : _document?.Control;

            public Wrapper(MarkdownScrollViewer v)
            {
                _viewer = v;
                _canvas = new Canvas();
                _canvas.PointerPressed += OnCanvasPointerPressed;

                _rects = new Dictionary<Control, Rectangle>();

                VisualChildren.Add(_canvas);
            }

            private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
            {
                if (_useVirtualization && _virtualizingPanel != null)
                {
                    _virtualizingPanel.UnSelect();
                }
                else
                {
                    _document?.UnSelect();
                }
            }

            /// <summary>
            /// 清理所有资源，断开引用
            /// </summary>
            public void Cleanup()
            {
                // 取消事件订阅
                _canvas.PointerPressed -= OnCanvasPointerPressed;

                // 清理文档
                if (_document is not null)
                {
                    if (_useVirtualization && _virtualizingPanel != null)
                    {
                        _virtualizingPanel.ScrollInvalidated -= OnVirtualizingPanelScrollInvalidated;
                        _virtualizingPanel.Clear();
                        VisualChildren.Remove(_virtualizingPanel);
                        LogicalChildren.Remove(_virtualizingPanel);
                        _virtualizingPanel = null;
                    }
                    else
                    {
                        VisualChildren.Remove(_document.Control);
                        LogicalChildren.Remove(_document.Control);
                    }
                    _document.Helper = null;
                    _document = null;
                }

                // 清理选择矩形
                Clear();
                _rects.Clear();

                // 清理 Canvas 从视觉树中移除
                VisualChildren.Remove(_canvas);
                _canvas.Children.Clear();

                // 断开对 viewer 的引用
                _viewer = null;
            }

            private void OnVirtualizingPanelScrollInvalidated(object? sender, EventArgs e)
            {
                ScrollInvalidated?.Invoke(this, e);
            }

            public bool CanHorizontallyScroll
            {
                get => _virtualizingPanel?.CanHorizontallyScroll ?? false;
                set
                {
                    if (_virtualizingPanel != null)
                    {
                        _virtualizingPanel.CanHorizontallyScroll = value;
                    }
                }
            }

            public bool CanVerticallyScroll
            {
                get => _virtualizingPanel?.CanVerticallyScroll ?? false;
                set
                {
                    if (_virtualizingPanel != null)
                    {
                        _virtualizingPanel.CanVerticallyScroll = value;
                    }
                }
            }

            public bool IsLogicalScrollEnabled => _useVirtualization && _virtualizingPanel?.IsLogicalScrollEnabled == true;

            public Size ScrollSize => _virtualizingPanel?.ScrollSize ?? default;

            public Size PageScrollSize => _virtualizingPanel?.PageScrollSize ?? default;

            public Size Extent => _virtualizingPanel?.Extent ?? default;

            public Vector Offset
            {
                get => _virtualizingPanel?.Offset ?? default;
                set
                {
                    if (_virtualizingPanel != null)
                    {
                        _virtualizingPanel.Offset = value;
                    }
                }
            }

            public Size Viewport => _virtualizingPanel?.Viewport ?? default;

            public bool BringIntoView(Control target, Rect targetRect)
            {
                return _virtualizingPanel?.BringIntoView(target, targetRect) ?? false;
            }

            public bool ScrollToElement(DocumentElement target)
            {
                return _virtualizingPanel?.ScrollToElement(target) ?? false;
            }

            public Control? GetControlInDirection(NavigationDirection direction, Control? from)
            {
                return _virtualizingPanel?.GetControlInDirection(direction, from);
            }

            public void RaiseScrollInvalidated(EventArgs e)
            {
                _virtualizingPanel?.RaiseScrollInvalidated(e);
            }


            public void Register(Control control)
            {
                if (_viewer == null) return;

                if (!_rects.ContainsKey(control))
                {
                    var brush = _viewer.ComputedSelectionBrush;
                    var bounds = GetRectInDoc(control);
                    if (bounds == null) return;

                    var rect = new Rectangle()
                    {
                        Width = bounds.Value.Width,
                        Height = bounds.Value.Height,
                        Fill = brush,
                        Opacity = .5
                    };

                    Canvas.SetLeft(rect, bounds.Value.Left);
                    Canvas.SetTop(rect, bounds.Value.Top);

                    _rects[control] = rect;
                    _canvas.Children.Add(rect);
                }
            }

            public void Unregister(Control control)
            {
                if (_rects.TryGetValue(control, out var rct))
                {
                    _canvas.Children.Remove(rct);
                    _rects.Remove(control);
                }
            }

            public void Clear()
            {
                _canvas.Children.Clear();
                _rects.Clear();
            }

            public void Restructure()
            {
                foreach (var rct in _rects)
                {
                    var boundN = GetRectInDoc(rct.Key);
                    if (boundN.HasValue)
                    {
                        var bound = boundN.Value;
                        rct.Value.Width = bound.Width;
                        rct.Value.Height = bound.Height;
                        Canvas.SetLeft(rct.Value, bound.Left);
                        Canvas.SetTop(rct.Value, bound.Top);
                    }
                }
            }

            public Rect? GetRectInDoc(Control control)
            {
                if (!LayoutInformation.GetPreviousArrangeBounds(control).HasValue)
                    return null;

                // 获取主内容控件
                var contentControl = ContentControl;
                if (contentControl == null)
                    return null;

                double driftX = 0;
                double driftY = 0;

                StyledElement? c;
                for (c = control.Parent;
                     c is not null
                     && c is Layoutable layoutable
                     && !ReferenceEquals(contentControl, layoutable);
                     c = c.Parent)
                {
                    driftX += layoutable.Bounds.X;
                    driftY += layoutable.Bounds.Y;
                }

                return new Rect(
                    control.Bounds.X + driftX,
                    control.Bounds.Y + driftY,
                    control.Bounds.Width,
                    control.Bounds.Height);
            }

            /// <summary>
            /// 获取虚拟化面板（如果正在使用虚拟化模式）
            /// </summary>
            public VirtualizingMarkdownPanel? VirtualizingPanel => _virtualizingPanel;
        }
    }
}
