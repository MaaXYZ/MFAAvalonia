using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;
using ColorTextBlock.Avalonia.Utils;
using Markdown.Avalonia.Controls;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Parsers.Builtin;
using Markdown.Avalonia.Parsing;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.Tables;
using Markdown.Avalonia.Utils;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax.Inlines;
using Markdig.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;

namespace Markdown.Avalonia
{
    public class Markdown : AvaloniaObject, IMarkdownEngine, IMarkdownEngine2, IDocumentAnchorProvider
    {
        #region const

        /// <summary>
        /// maximum nested depth of [] and () supported by the transform; implementation detail
        /// </summary>
        private const int _nestDepth = 6;

        /// <summary>
        /// 最大递归深度限制，防止栈溢出
        /// </summary>
        private const int _maxRecursionDepth = 50;

        /// <summary>
        /// 线程本地的递归深度计数器
        /// </summary>
        [ThreadStatic]
        private static int _currentRecursionDepth;

        /// <summary>
        /// Tabs are automatically converted to spaces as part of the transform  
        /// this constant determines how "wide" those tabs become in spaces  
        /// </summary>
        private const int _tabWidth = 4;

        public const string Heading1Class = ClassNames.Heading1Class;
        public const string Heading2Class = ClassNames.Heading2Class;
        public const string Heading3Class = ClassNames.Heading3Class;
        public const string Heading4Class = ClassNames.Heading4Class;
        public const string Heading5Class = ClassNames.Heading5Class;
        public const string Heading6Class = ClassNames.Heading6Class;

        public const string CodeBlockClass = ClassNames.CodeBlockClass;
        public const string ContainerBlockClass = ClassNames.ContainerBlockClass;
        public const string NoContainerClass = ClassNames.NoContainerClass;
        public const string BlockquoteClass = ClassNames.BlockquoteClass;
        public const string NoteClass = ClassNames.NoteClass;

        public const string ParagraphClass = ClassNames.ParagraphClass;

        public const string TableClass = ClassNames.TableClass;
        public const string TableHeaderClass = ClassNames.TableHeaderClass;
        public const string TableFirstRowClass = ClassNames.TableFirstRowClass;
        public const string TableRowOddClass = ClassNames.TableRowOddClass;
        public const string TableRowEvenClass = ClassNames.TableRowEvenClass;
        public const string TableLastRowClass = ClassNames.TableLastRowClass;
        public const string TableFooterClass = ClassNames.TableFooterClass;

        public const string ListClass = ClassNames.ListClass;
        public const string ListMarkerClass = ClassNames.ListMarkerClass;

        #endregion

        #region static regex patterns

        /// <summary>
        /// 预编译的 HTML 注释匹配正则
        /// </summary>
        private static readonly Regex _htmlCommentPattern = new(@"<!--([\s\S]*?)-->", RegexOptions.Compiled);

        /// <summary>
        /// 预编译的中文字符检测正则
        /// </summary>
        private static readonly Regex _chineseCharPattern = new(@"[\u4e00-\u9fa5]", RegexOptions.Compiled);

        /// <summary>
        /// 预编译的 HTML 标签名提取正则
        /// </summary>
        private static readonly Regex _htmlTagNamePattern = new(@"^<\/?([a-zA-Z0-9]+)", RegexOptions.Compiled);

        /// <summary>
        /// 自闭合 HTML 标签集合
        /// </summary>
        private static readonly HashSet<string> _selfClosingTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "img",
            "br",
            "hr",
            "input",
            "meta",
            "link",
            "area",
            "base",
            "col",
            "embed",
            "param",
            "source",
            "track",
            "wbr"
        };

        #endregion

        /// <summary>
        /// when true, bold and italic require non-word characters on either side  
        /// WARNING: this is a significant deviation from the markdown spec
        /// </summary>
        public bool StrictBoldItalic { get; set; }

        private string _assetPathRoot;

        /// <inheritdoc/>
        public string AssetPathRoot
        {
            get => _assetPathRoot;
            set
            {
                _assetPathRoot = value;
#pragma warning disable CS0618
                if (BitmapLoader is not null)
                    BitmapLoader.AssetPathRoot = value;
#pragma warning restore CS0618
                if (_setupInfo is not null)
                    _setupInfo.PathResolver.AssetPathRoot = value;
            }
        }

        private string[] _assetAssemblyNames;
        public IEnumerable<string> AssetAssemblyNames => _assetAssemblyNames;

        private ICommand? _hyperlinkCommand;
        private RoutedHyperlinkCommand? _routedHyperlinkCommand;
        private readonly Dictionary<string, DocumentElement> _documentAnchors =
            new(StringComparer.Ordinal);

        Action<string>? IDocumentAnchorProvider.AnchorNavigationRequested { get; set; }

        /// <inheritdoc/>
        public ICommand? HyperlinkCommand
        {
            get => _routedHyperlinkCommand ??= new RoutedHyperlinkCommand(this);
            set
            {
                if (ReferenceEquals(value, _routedHyperlinkCommand))
                    return;
                _hyperlinkCommand = value;
            }
        }

        private ICommand? ExternalHyperlinkCommand => _hyperlinkCommand ?? _setupInfo?.HyperlinkCommand;

        bool IDocumentAnchorProvider.TryGetDocumentAnchor(string anchor, out DocumentElement element)
            => _documentAnchors.TryGetValue(NormalizeAnchor(anchor), out element!);

        public MdAvPlugins Plugins { get; set; }

        [Obsolete] private IBitmapLoader? _loader;

        /// <inheritdoc/>
        [Obsolete("Please use Plugins propety. see https://github.com/whistyun/Markdown.Avalonia/wiki/How-to-migrages-to-ver11")]
        public IBitmapLoader? BitmapLoader
        {
            get => _loader;
            set
            {
                _loader = value;
                if (_loader is not null)
                {
                    _loader.AssetPathRoot = _assetPathRoot;
                }
            }
        }

        private IContainerBlockHandler? _containerBlockHandler;

        public IContainerBlockHandler? ContainerBlockHandler
        {
            get => _containerBlockHandler ?? _setupInfo?.ContainerBlock;
            set
            {
                _containerBlockHandler = value;
            }
        }

        public CascadeDictionary CascadeResources { get; } = new CascadeDictionary();

        public IResourceDictionary Resources
        {
            get => CascadeResources.Owner;
            set => CascadeResources.Owner = value;
        }

        public bool UseResource { get; set; }

        #region dependencyobject property

        public static readonly DirectProperty<Markdown, ICommand?> HyperlinkCommandProperty =
            AvaloniaProperty.RegisterDirect<Markdown, ICommand?>(nameof(HyperlinkCommand),
                mdEng => mdEng.HyperlinkCommand,
                (mdEng, command) => mdEng.HyperlinkCommand = command);

        [Obsolete("Please use Plugins propety. see https://github.com/whistyun/Markdown.Avalonia/wiki/How-to-migrages-to-ver11")]
        public static readonly DirectProperty<Markdown, IBitmapLoader?> BitmapLoaderProperty =
            AvaloniaProperty.RegisterDirect<Markdown, IBitmapLoader?>(nameof(BitmapLoader),
                mdEng => mdEng.BitmapLoader,
                (mdEng, loader) => mdEng.BitmapLoader = loader);

        #endregion

        #region ParseInfo

        private SetupInfo _setupInfo;
        private BlockParser2[] _topBlockParsers;
        private BlockParser2[] _blockParsers;
        private InlineParser[] _inlines;
        private bool _supportTextAlignment;
        private bool _supportStrikethrough;
        private bool _supportTextileInline;

        #endregion

        public Markdown()
        {
            _assetPathRoot = Environment.CurrentDirectory;

            var stack = new StackTrace();
            _assetAssemblyNames = stack.GetFrames()
                .Select(frm => frm?.GetMethod()?.DeclaringType?.Assembly?.GetName()?.Name)
                .OfType<string>()
                .Where(name => !name.Equals("Markdown.Avalonia"))
                .Distinct()
                .ToArray();

            Plugins = new MdAvPlugins();

            _setupInfo = null!;
            _topBlockParsers = null!;
            _blockParsers = null!;
            _inlines = null!;
            SetupParser();
        }

        private void SetupParser()
        {
            var info = Plugins.Info;
            if (ReferenceEquals(info, _setupInfo))
                return;

            var topBlocks = new List<BlockParser2>();
            var subBlocks = new List<BlockParser2>();
            var inlines = new List<InlineParser>();


            // top-level block parser
            topBlocks.Add(
                info.EnableListMarkerExt ? new ExtListParser() : new CommonListParser());

            // Plugin block parsers must be able to specialize built-in syntax
            // (for example, rendering a ```math fence instead of a code block).
            topBlocks.AddRange(info.TopBlock.Select(bp => bp.Upgrade()));

            topBlocks.Add(new FencedCodeBlockParser(info.EnablePreRenderingCodeBlock));

            if (info.EnableContainerBlockExt)
            {
                topBlocks.Add(new ContainerBlockParser());
            }


            // sub-level block parser
            subBlocks.Add(new BlockquotesParser(info.EnableTextAlignment));
            subBlocks.Add(new SetextHeaderParser());
            subBlocks.Add(new AtxHeaderParser());

            subBlocks.Add(
                info.EnableRuleExt ? new ExtHorizontalParser() : new CommonHorizontalParser());

            if (info.EnableTableBlock)
            {
                subBlocks.Add(new TableParser());
            }

            if (info.EnableNoteBlock)
            {
                subBlocks.Add(new NoteParser());
            }

            subBlocks.Add(new IndentCodeBlockParser());


            // inline parser
            inlines.Add(InlineParser.New(_codeSpan, nameof(CodeSpanEvaluator), CodeSpanEvaluator));
            inlines.Add(InlineParser.New(_imageOrHrefInline, nameof(ImageOrHrefInlineEvaluator), ImageOrHrefInlineEvaluator));
            inlines.Add(InlineParser.New(_autoLink, nameof(AutoLinkEvaluator), AutoLinkEvaluator));

            if (StrictBoldItalic)
            {
                inlines.Add(InlineParser.New(_strictBold, nameof(BoldEvaluator), BoldEvaluator));
                inlines.Add(InlineParser.New(_strictItalic, nameof(ItalicEvaluator), ItalicEvaluator));

                if (info.EnableStrikethrough)
                    inlines.Add(InlineParser.New(_strikethrough, nameof(StrikethroughEvaluator), StrikethroughEvaluator));
            }

            // parser registered by plugin

            subBlocks.AddRange(info.Block.Select(bp => bp.Upgrade()));
            inlines.AddRange(info.Inline);


            // inform path info to resolver
            info.PathResolver.AssetPathRoot = AssetPathRoot;
            info.PathResolver.CallerAssemblyNames = AssetAssemblyNames;

            info.Overwrite(_hyperlinkCommand);
            info.Overwrite(_containerBlockHandler);
            info.Overwrite(_loader);


            _topBlockParsers = topBlocks.Select(p => info.Override(p).Upgrade()).ToArray();
            _blockParsers = subBlocks.Select(p => info.Override(p).Upgrade()).ToArray();
            _inlines = inlines.ToArray();
            _supportTextAlignment = info.EnableTextAlignment;
            _supportStrikethrough = info.EnableStrikethrough;
            _supportTextileInline = info.EnableTextileInline;
            _setupInfo = info;
        }

        private string? PreprocessText(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            // 使用预编译的静态正则表达式处理 HTML 注释
            var processed = _htmlCommentPattern.Replace(input, match =>
            {
                // 提取注释中间的内容（捕获组1）
                string commentContent = match.Groups[1].Value;

                // 检查内容中是否包含中文字符（使用预编译的静态正则）
                bool hasChineseChars = _chineseCharPattern.IsMatch(commentContent);

                // 若包含汉字，则保留中间内容；否则完全移除
                return hasChineseChars ? commentContent : string.Empty;
            });

            return ResolveReferenceLinks(processed);
        }

        private static readonly Regex _referenceDefinitionPattern = new(
            @"^[\t ]*\[(?!\^)(?<id>[^\]]+)\]:[\t ]*(?<url>\S+)(?:[\t ]+(?<title>[\""'].*?[\""']))?[\t ]*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex _referenceLinkPattern = new(
            @"(?<image>!?)\[(?<text>[^\]]+)\]\[(?<id>[^\]]+)\]",
            RegexOptions.Compiled);

        internal static string ResolveReferenceLinks(string text)
        {
            var definitions = new Dictionary<string, (string Url, string Title)>(StringComparer.OrdinalIgnoreCase);
            var withoutDefinitions = _referenceDefinitionPattern.Replace(text, match =>
            {
                definitions[match.Groups["id"].Value.Trim()] = (
                    match.Groups["url"].Value,
                    match.Groups["title"].Success ? match.Groups["title"].Value : string.Empty);
                return string.Empty;
            });

            if (definitions.Count == 0)
                return withoutDefinitions;

            return _referenceLinkPattern.Replace(withoutDefinitions, match =>
            {
                var id = match.Groups["id"].Value.Trim();
                return definitions.TryGetValue(id, out var definition)
                    ? $"{match.Groups["image"].Value}[{match.Groups["text"].Value}]" +
                      $"({definition.Url}{(definition.Title.Length > 0 ? " " + definition.Title : string.Empty)})"
                    : match.Value;
            });
        }

        public Control Transform(string? text)
        {
            return TransformElement(text).Control;
        }

        public DocumentElement TransformElement(string? text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            text = PreprocessText(text);
            SetupParser();

            text = TextUtil.Normalize(text, _tabWidth);

            var status = new ParseStatus(true & _supportTextAlignment);
            _documentAnchors.Clear();
            var elements = ParseGamutElement(text, status).ToArray();
            return new DocumentRootElement(elements);
        }

        internal DeferredParseOptions PrepareDeferredParsing()
        {
            SetupParser();
            return new DeferredParseOptions(_tabWidth, _supportTextAlignment);
        }

        internal DeferredDocumentPlan BuildDeferredDocument(
            string text,
            DeferredParseOptions options,
            CancellationToken cancellationToken)
        {
            var processed = PreprocessText(text) ?? string.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            processed = TextUtil.Normalize(processed, options.TabWidth);

            var index = StandardMarkdownParser.CreateIndex(processed, cancellationToken);
            var elements = new List<DocumentElement>(index.Blocks.Count);
            var anchors = new List<DeferredAnchor>();

            foreach (var block in index.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capturedBlock = block;
                var useBoundedRawFallback = ShouldUseBoundedRawFallback(index.Source, block);
                Func<IReadOnlyList<DocumentElement>> factory = useBoundedRawFallback
                    ? () =>
                    [
                        new LargeRawTextElement(index.Source.Substring(
                            capturedBlock.SourceStart,
                            capturedBlock.SourceLength))
                    ]
                    : () => RenderDeferredBlock(
                        index.Source,
                        capturedBlock,
                        options.SupportTextAlignment);
                var contentKey =
                    $"{block.Node.GetType().Name}:{block.SourceStart}:{block.SourceLength}";
                DocumentElement deferred = block.Node is HeadingBlock heading
                    ? new DeferredHeadingElement(
                        factory,
                        contentKey,
                        heading.Level,
                        ExtractHeadingText(heading))
                    : new DeferredBlockElement(factory, contentKey);

                elements.Add(deferred);
                foreach (var anchor in ExtractIndexedBlockAnchors(index.Source, block))
                    anchors.Add(new DeferredAnchor(anchor, deferred));

                if (block.Node is LeafBlock { Inline: { } inline })
                {
                    foreach (var link in EnumerateInlineTree(inline)
                                 .OfType<FootnoteLink>()
                                 .Where(link => !link.IsBackLink))
                    {
                        anchors.Add(new DeferredAnchor($"fnref:{link.Index}", deferred));
                    }
                }

                if (block.Node is FootnoteGroup footnoteGroup)
                {
                    foreach (var footnote in footnoteGroup.OfType<Footnote>().Where(f => f.Order > 0))
                        anchors.Add(new DeferredAnchor($"fn:{footnote.Order}", deferred));
                }
            }

            return new DeferredDocumentPlan(
                new DocumentRootElement(elements),
                anchors,
                index.Source);
        }

        internal void ActivateDeferredDocument(DeferredDocumentPlan plan)
        {
            _documentAnchors.Clear();
            foreach (var anchor in plan.Anchors)
                RegisterDocumentAnchor(anchor.Name, anchor.Target);
        }

        private IReadOnlyList<DocumentElement> RenderDeferredBlock(
            string documentSource,
            StandardMarkdownParser.IndexedBlock indexedBlock,
            bool supportTextAlignment)
        {
            var parsedBlock = indexedBlock.Materialize(documentSource);
            return RenderParsedBlock(parsedBlock, new ParseStatus(supportTextAlignment));
        }

        private IEnumerable<string> ExtractIndexedBlockAnchors(
            string documentSource,
            StandardMarkdownParser.IndexedBlock indexedBlock)
        {
            if (indexedBlock.IsRawHtmlContainer)
            {
                var html = documentSource.Substring(indexedBlock.SourceStart, indexedBlock.SourceLength);
                foreach (var anchor in ExtractHtmlAnchors(html))
                    yield return anchor;
                yield break;
            }

            var attributes = indexedBlock.Node.TryGetAttributes();
            if (!string.IsNullOrWhiteSpace(attributes?.Id))
                yield return attributes.Id!;

            if (indexedBlock.Node is CodeBlock)
                yield break;

            if (indexedBlock.Node is Markdig.Syntax.HtmlBlock htmlBlock)
            {
                foreach (var anchor in ExtractHtmlAnchors(SliceSource(documentSource, htmlBlock)))
                    yield return anchor;
            }

            if (indexedBlock.Node is LeafBlock { Inline: { } inline })
            {
                foreach (var htmlInline in EnumerateInlineTree(inline).OfType<HtmlInline>())
                {
                    foreach (var anchor in ExtractHtmlAnchors(SliceSource(documentSource, htmlInline)))
                        yield return anchor;
                }
            }
        }

        private static string ExtractHeadingText(HeadingBlock heading)
        {
            if (heading.Inline is null)
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var inline in EnumerateInlineTree(heading.Inline))
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        builder.Append(literal.Content.ToString());
                        break;
                    case HtmlEntityInline entity:
                        builder.Append(WebUtility.HtmlDecode(entity.Original.ToString()));
                        break;
                    case CodeInline code:
                        builder.Append(code.Content);
                        break;
                    case LineBreakInline:
                        builder.Append(' ');
                        break;
                    case AutolinkInline autolink:
                        builder.Append(autolink.Url);
                        break;
                    case FootnoteLink footnote when !footnote.IsBackLink:
                        builder.Append('[').Append(footnote.Footnote.Order).Append(']');
                        break;
                }
            }
            return builder.ToString();
        }

        private static bool ShouldUseBoundedRawFallback(
            string source,
            StandardMarkdownParser.IndexedBlock block)
        {
            if (block.Node is CodeBlock or FootnoteGroup or HeadingBlock)
                return false;

            const int maximumExpandedCharacters = 512 * 1024;
            const int maximumExpandedLines = 5000;
            if (block.SourceLength > maximumExpandedCharacters)
                return true;

            var end = Math.Min(source.Length, block.SourceStart + block.SourceLength);
            var lines = 1;
            for (var index = block.SourceStart; index < end; index++)
            {
                if (source[index] == '\n' && ++lines > maximumExpandedLines)
                    return true;
            }

            return false;
        }

        internal readonly record struct DeferredParseOptions(
            int TabWidth,
            bool SupportTextAlignment);

        internal readonly record struct DeferredAnchor(
            string Name,
            DocumentElement Target);

        internal sealed class DeferredDocumentPlan
        {
            public DeferredDocumentPlan(
                DocumentRootElement document,
                IReadOnlyList<DeferredAnchor> anchors,
                string source)
            {
                Document = document;
                Anchors = anchors;
                Source = source;
            }

            public DocumentRootElement Document { get; }
            public IReadOnlyList<DeferredAnchor> Anchors { get; }

            // Keeps the normalized source alive for deferred block factories.
            public string Source { get; }
        }

        public IEnumerable<DocumentElement> ParseGamutElement(string? text, ParseStatus status)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            SetupParser();
            return RunStandardBlockPipeline(text, status);
        }

        public IEnumerable<CInline> ParseGamutInline(string? text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            SetupParser();
            return PrivateRunSpanGamut(text);
        }

        public IEnumerable<Control> RunBlockGamut(string? text, ParseStatus status)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            SetupParser();

            text = TextUtil.Normalize(text, _tabWidth);

            var elements = RunStandardBlockPipeline(text, status);
            return elements.Select(e => e.Control);
        }

        public IEnumerable<CInline> RunSpanGamut(string? text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            SetupParser();

            text = TextUtil.Normalize(text, _tabWidth);

            return PrivateRunSpanGamut(text);
        }

        private IEnumerable<DocumentElement> RunStandardBlockPipeline(string text, ParseStatus status)
        {
            var pendingAnchors = new List<string>();

            foreach (var parsedBlock in StandardMarkdownParser.ParseBlocks(text))
            {
                var blockAnchors = ExtractBlockAnchors(parsedBlock).ToArray();
                var blockElements = RenderParsedBlock(parsedBlock, status);

                if (blockElements.Count == 0)
                {
                    pendingAnchors.AddRange(blockAnchors);
                    continue;
                }

                var anchorTarget = blockElements[0];
                foreach (var anchor in pendingAnchors)
                    RegisterDocumentAnchor(anchor, anchorTarget);
                pendingAnchors.Clear();

                foreach (var anchor in blockAnchors)
                    RegisterDocumentAnchor(anchor, anchorTarget);

                RegisterFootnoteReferenceAnchors(parsedBlock.Node, anchorTarget);

                foreach (var element in blockElements)
                    yield return element;
            }
        }

        private IReadOnlyList<DocumentElement> RenderParsedBlock(
            StandardMarkdownParser.ParsedBlock parsedBlock,
            ParseStatus status)
        {
            if (TryRenderStandardBlock(parsedBlock, status, out var standardElements))
                return standardElements!;

            var source = parsedBlock.Source.EndsWith('\n')
                ? parsedBlock.Source
                : parsedBlock.Source + "\n";
            return PrivateRunBlockGamut(source, status).ToArray();
        }

        private IEnumerable<string> ExtractBlockAnchors(StandardMarkdownParser.ParsedBlock parsedBlock)
        {
            if (parsedBlock.IsRawHtmlContainer)
            {
                foreach (var anchor in ExtractHtmlAnchors(parsedBlock.Source))
                    yield return anchor;
                yield break;
            }

            var attributes = parsedBlock.Node.TryGetAttributes();
            if (!string.IsNullOrWhiteSpace(attributes?.Id))
                yield return attributes.Id!;

            if (parsedBlock.Node is CodeBlock)
                yield break;

            if (parsedBlock.Node is Markdig.Syntax.HtmlBlock htmlBlock)
            {
                foreach (var anchor in ExtractHtmlAnchors(SliceSource(parsedBlock.DocumentSource, htmlBlock)))
                    yield return anchor;
            }

            if (parsedBlock.Node is LeafBlock { Inline: { } inline })
            {
                foreach (var htmlInline in EnumerateInlineTree(inline).OfType<HtmlInline>())
                {
                    foreach (var anchor in ExtractHtmlAnchors(SliceSource(parsedBlock.DocumentSource, htmlInline)))
                        yield return anchor;
                }
            }
        }

        private static readonly Regex _htmlIdAnchorPattern = new(
            "\\bid\\s*=\\s*(?:\"(?<double>[^\"]+)\"|'(?<single>[^']+)'|(?<bare>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex _htmlNameAnchorPattern = new(
            "<a\\b[^>]*\\bname\\s*=\\s*(?:\"(?<double>[^\"]+)\"|'(?<single>[^']+)'|(?<bare>[^\\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static IEnumerable<string> ExtractHtmlAnchors(string html)
        {
            foreach (Match match in _htmlIdAnchorPattern.Matches(html))
                yield return ReadAnchorAttribute(match);
            foreach (Match match in _htmlNameAnchorPattern.Matches(html))
                yield return ReadAnchorAttribute(match);

            static string ReadAnchorAttribute(Match match)
            {
                var value = match.Groups["double"].Success
                    ? match.Groups["double"].Value
                    : match.Groups["single"].Success
                        ? match.Groups["single"].Value
                        : match.Groups["bare"].Value;
                return WebUtility.HtmlDecode(value);
            }
        }

        private void RegisterFootnoteReferenceAnchors(Block node, DocumentElement target)
        {
            if (node is not LeafBlock { Inline: { } inline })
                return;

            foreach (var link in EnumerateInlineTree(inline).OfType<FootnoteLink>().Where(link => !link.IsBackLink))
                RegisterDocumentAnchor($"fnref:{link.Index}", target);
        }

        private void RegisterDocumentAnchor(string anchor, DocumentElement target)
        {
            var normalized = NormalizeAnchor(anchor);
            if (normalized.Length > 0)
                _documentAnchors.TryAdd(normalized, target);
        }

        private static string NormalizeAnchor(string anchor)
        {
            var normalized = anchor.Trim();
            if (normalized.StartsWith('#'))
                normalized = normalized[1..];

            try
            {
                return Uri.UnescapeDataString(normalized);
            }
            catch (UriFormatException)
            {
                return normalized;
            }
        }

        private bool TryRenderStandardBlock(
            StandardMarkdownParser.ParsedBlock parsedBlock,
            ParseStatus status,
            out IReadOnlyList<DocumentElement>? elements)
        {
            elements = null;

            // Markdig moves all referenced footnote definitions into a FootnoteGroup
            // at the end of the document.  Its group span can begin at position zero,
            // so sending Group.Source through the legacy parser would render a second
            // copy of the document.  Render the authoritative AST group directly.
            if (parsedBlock.Node is FootnoteGroup footnoteGroup)
            {
                elements = RenderFootnoteGroup(footnoteGroup, parsedBlock.DocumentSource, status);
                return true;
            }

            // Code blocks have already been delimited by Markdig.  Sending their
            // source through the legacy block/HTML parsers a second time makes
            // code such as "<?php" look like an HTML processing instruction and
            // can truncate everything after the first line.
            if (parsedBlock.Node is CodeBlock codeBlock and not Markdig.Syntax.FencedCodeBlock)
            {
                // The AST span is the authoritative boundary.  Rebuild the code
                // from its original source instead of Markdig's internal line
                // buffer, whose representation can vary with parser context.
                var code = TextUtil.DetentLinesBestEffort(parsedBlock.Source
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n'), 4)
                    .TrimEnd('\n');
                elements = [new UnBlockElement(FencedCodeBlockParser.Create(code), "IndentedCode:" + code)];
                return true;
            }

            if (status.SupportTextAlignment && _align.IsMatch(parsedBlock.Source))
                return false;

            ContainerInline? inline = parsedBlock.Node switch
            {
                ParagraphBlock paragraph => paragraph.Inline,
                HeadingBlock heading => heading.Inline,
                _ => null
            };

            if (inline is null)
                return false;

            CInline[] rendered;
            if (CanUseStandardInlinePipeline(parsedBlock.Source, inline))
            {
                rendered = RenderStandardInlines(inline).ToArray();
            }
            else if (ContainsForwardFootnoteLink(inline))
            {
                // Preserve the existing HTML/math/textile inline pipeline while
                // replacing only the footnote reference nodes recognized by Markdig.
                rendered = RenderMixedFootnoteInlines(
                    parsedBlock.Source,
                    parsedBlock.SourceStart,
                    inline).ToArray();
            }
            else
            {
                return false;
            }

            DecodeHtmlEntities(rendered);
            NormalizeInlineWhitespaceBoundaries(rendered);

            elements =
            [
                parsedBlock.Node is HeadingBlock headingBlock
                    ? new HeaderElement(rendered, headingBlock.Level)
                    : new CTextBlockElement(rendered, ParagraphClass)
            ];
            return true;
        }

        private IReadOnlyList<DocumentElement> RenderFootnoteGroup(
            FootnoteGroup footnoteGroup,
            string documentSource,
            ParseStatus status)
        {
            var footnotes = footnoteGroup
                .OfType<Footnote>()
                .Where(footnote => footnote.Order > 0)
                .OrderBy(footnote => footnote.Order)
                .ToArray();

            if (footnotes.Length == 0)
                return Array.Empty<DocumentElement>();

            var items = footnotes.Select(footnote =>
            {
                var contents = RenderFootnoteBlocks(footnote, documentSource, status).ToArray();
                if (contents.Length == 0)
                    contents = [new CTextBlockElement(Array.Empty<CInline>(), ParagraphClass)];
                var item = new ListItemElement(contents);
                RegisterDocumentAnchor($"fn:{footnote.Order}", item);
                return item;
            }).ToArray();

            var rule = new Rule(RuleType.Single);
            rule.Classes.Add(ClassNames.FootnoteRuleClass);

            return
            [
                new UnBlockElement(rule, "FootnoteRule"),
                new ListBlockElement(
                    ColorDocument.Avalonia.DocumentElements.TextMarkerStyle.Decimal,
                    items)
            ];
        }

        private IEnumerable<DocumentElement> RenderFootnoteBlocks(
            Footnote footnote,
            string documentSource,
            ParseStatus status)
        {
            foreach (var block in footnote)
            {
                var source = SliceSource(documentSource, block);

                if (block is ParagraphBlock paragraph && paragraph.Inline is { } paragraphInline)
                {
                    CInline[] inlines;
                    if (CanUseStandardInlinePipeline(source, paragraphInline))
                    {
                        // Generated backlink nodes are intentionally skipped by
                        // RenderStandardInlines until viewer-local anchor navigation
                        // is available. They must never be sent to the external URL command.
                        inlines = RenderStandardInlines(paragraphInline).ToArray();
                    }
                    else
                    {
                        inlines = PrivateRunSpanGamut(source).ToArray();
                    }

                    DecodeHtmlEntities(inlines);
                    NormalizeInlineWhitespaceBoundaries(inlines);
                    yield return new CTextBlockElement(inlines, ParagraphClass);
                    continue;
                }

                if (block is HeadingBlock heading && heading.Inline is { } headingInline)
                {
                    var inlines = CanUseStandardInlinePipeline(source, headingInline)
                        ? RenderStandardInlines(headingInline).ToArray()
                        : PrivateRunSpanGamut(source).ToArray();
                    DecodeHtmlEntities(inlines);
                    NormalizeInlineWhitespaceBoundaries(inlines);
                    yield return new HeaderElement(inlines, heading.Level);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(source))
                    continue;

                var nestedSource = source.EndsWith('\n') ? source : source + "\n";
                foreach (var nested in RunStandardBlockPipeline(nestedSource, status))
                    yield return nested;
            }
        }

        private static string SliceSource(string documentSource, MarkdownObject node)
        {
            var span = node.Span;
            if (span.Start < 0 || span.End < span.Start || span.Start >= documentSource.Length)
                return string.Empty;

            var end = Math.Min(span.End, documentSource.Length - 1);
            return documentSource.Substring(span.Start, end - span.Start + 1);
        }

        private IEnumerable<DocumentElement> PrivateRunBlockGamut(string text, ParseStatus status)
        {
            var index = 0;
            var length = text.Length;
            var rtn = new List<DocumentElement>();

            var candidates = new List<Candidate<BlockParser2>>();

            for (;;)
            {
                candidates.Clear();

                foreach (var parser in _topBlockParsers)
                {
                    var match = parser.Pattern.Match(text, index, length);
                    if (match.Success) candidates.Add(new Candidate<BlockParser2>(match, parser));
                }

                if (candidates.Count == 0) break;

                candidates.Sort();

                int bestBegin = 0;
                int bestEnd = 0;
                IEnumerable<DocumentElement>? result = null;

                foreach (var c in candidates)
                {
                    result = c.Parser.Convert2(text, c.Match, status, this, out bestBegin, out bestEnd);
                    if (result is not null) break;
                }

                if (result is null) break;

                if (bestBegin > index)
                {
                    RunBlockRest(text, index, bestBegin - index, status, 0, rtn);
                }

                rtn.AddRange(result);

                length -= bestEnd - index;
                index = bestEnd;
            }

            if (index < text.Length)
            {
                RunBlockRest(text, index, text.Length - index, status, 0, rtn);
            }

            return rtn;


            void RunBlockRest(
                string text,
                int index,
                int length,
                ParseStatus status,
                int parserStart,
                List<DocumentElement> outto)
            {
                for (; parserStart < _blockParsers.Length; ++parserStart)
                {
                    var parser = _blockParsers[parserStart];

                    for (;;)
                    {
                        var match = parser.Pattern.Match(text, index, length);
                        if (!match.Success) break;

                        var rslt = parser.Convert2(text, match, status, this, out int parseBegin, out int parserEnd);
                        if (rslt is null) break;

                        if (parseBegin > index)
                        {
                            RunBlockRest(text, index, parseBegin - index, status, parserStart + 1, outto);
                        }
                        outto.AddRange(rslt);

                        length -= parserEnd - index;
                        index = parserEnd;
                    }

                    if (length == 0) break;
                }

                if (length != 0)
                {
                    outto.AddRange(FormParagraphs(text.Substring(index, length), status));
                }
            }
        }


        private IEnumerable<CInline> PrivateRunSpanGamut(string text)
        {
            // Debug：初始解析入口
            var standardRoot = StandardMarkdownParser.ParseInline(text);
            var useStandardPipeline = CanUseStandardInlinePipeline(text, standardRoot);
            List<CInline> rtn;
            if (useStandardPipeline)
            {
                rtn = RenderStandardInlines(standardRoot).ToList();
            }
            else
            {
                var protectedText = ProtectCodeInlines(text, standardRoot, out var codeInlines);
                rtn = RestoreCodeInlinePlaceholders(
                    ParseAllNestedPairs(protectedText, 1),
                    codeInlines).ToList();
            }

            // 步骤1：先递归处理所有成对符号（分阶段核心：成对匹配优先）
            DecodeHtmlEntities(rtn);
            NormalizeInlineWhitespaceBoundaries(rtn);

            return rtn;
        }

        internal static string ProtectCodeInlines(
            string text,
            ContainerInline? root,
            out IReadOnlyDictionary<int, string> codeInlines)
        {
            var replacements = new Dictionary<int, string>();
            if (root is null)
            {
                codeInlines = replacements;
                return text;
            }

            var codeNodes = EnumerateInlineTree(root)
                .OfType<CodeInline>()
                .Where(code => code.Span.Start >= 0
                               && code.Span.End >= code.Span.Start
                               && code.Span.End < text.Length)
                .OrderBy(code => code.Span.Start)
                .ToArray();

            var protectedText = text;
            for (var index = codeNodes.Length - 1; index >= 0; index--)
            {
                var code = codeNodes[index];
                replacements[index] = code.Content;
                protectedText = protectedText.Remove(
                        code.Span.Start,
                        code.Span.End - code.Span.Start + 1)
                    .Insert(code.Span.Start, CreateCodeInlinePlaceholder(index));
            }

            codeInlines = replacements;
            return protectedText;
        }

        private static string CreateCodeInlinePlaceholder(int index) => $"\uE000CODE{index}\uE001";

        private static readonly Regex _codeInlinePlaceholderPattern = new(
            "\\uE000CODE(?<index>[0-9]+)\\uE001",
            RegexOptions.Compiled);

        private static IEnumerable<CInline> RestoreCodeInlinePlaceholders(
            IEnumerable<CInline> inlines,
            IReadOnlyDictionary<int, string> replacements)
        {
            foreach (var inline in inlines)
            {
                if (inline is CRun run && _codeInlinePlaceholderPattern.IsMatch(run.Text))
                {
                    var position = 0;
                    foreach (Match match in _codeInlinePlaceholderPattern.Matches(run.Text))
                    {
                        if (match.Index > position)
                            yield return new CRun { Text = run.Text.Substring(position, match.Index - position) };

                        if (int.TryParse(match.Groups["index"].Value, out var index)
                            && replacements.TryGetValue(index, out var code))
                        {
                            yield return new CCode([new CRun { Text = code }]);
                        }
                        else
                        {
                            yield return new CRun { Text = match.Value };
                        }

                        position = match.Index + match.Length;
                    }

                    if (position < run.Text.Length)
                        yield return new CRun { Text = run.Text.Substring(position) };
                    continue;
                }

                if (inline is CSpan span)
                    span.Content = RestoreCodeInlinePlaceholders(span.Content, replacements).ToArray();

                yield return inline;
            }
        }

        private static bool CanUseStandardInlinePipeline(string text, ContainerInline? root)
        {
            // HTML is detected from Markdig's actual node types below. A textual '<'
            // can also be a CommonMark autolink and must not force the HTML fallback.
            if (text.Contains("%{") || text.Contains('$') || text.Contains('@'))
                return false;

            return root is not null && EnumerateInlineTree(root).All(node => node is
                LiteralInline or HtmlEntityInline or CodeInline or EmphasisInline or LineBreakInline
                or AutolinkInline or LinkInline or FootnoteLink);
        }

        private static bool ContainsForwardFootnoteLink(ContainerInline root)
            => EnumerateInlineTree(root).OfType<FootnoteLink>().Any(link => !link.IsBackLink);

        private IEnumerable<CInline> RenderStandardInlines(ContainerInline? root)
        {
            if (root is null) yield break;

            for (var node = root.FirstChild; node is not null; node = node.NextSibling)
            {
                switch (node)
                {
                    case LiteralInline literal:
                        yield return new CRun { Text = ReplaceEmojiShortcodes(literal.Content.ToString()) };
                        break;
                    case HtmlEntityInline entity:
                        // Entity decoding is centralized after rendering so nested entities
                        // such as &amp;lt; are decoded exactly once.
                        yield return new CRun { Text = entity.Original.ToString() };
                        break;
                    case CodeInline code:
                        yield return new CCode([new CRun { Text = code.Content }]);
                        break;
                    case LineBreakInline lineBreak:
                        yield return lineBreak.IsHard ? new CLineBreak() : new CRun { Text = " " };
                        break;
                    case EmphasisInline emphasis:
                    {
                        var children = RenderStandardInlines(emphasis).ToArray();
                        yield return emphasis.DelimiterChar switch
                        {
                            '~' => new CStrikethrough(children),
                            '=' => CreateMarkedInline(children),
                            _ => emphasis.DelimiterCount >= 2
                                ? new CBold(children)
                                : new CItalic(children)
                        };
                        break;
                    }
                    case LinkInline link when !link.IsImage:
                    {
                        var target = link.GetDynamicUrl?.Invoke() ?? link.Url ?? string.Empty;
                        yield return new CHyperlink(RenderStandardInlines(link))
                        {
                            Command = url =>
                            {
                                if (HyperlinkCommand?.CanExecute(url) == true) HyperlinkCommand.Execute(url);
                            },
                            CommandParameter = target
                        };
                        break;
                    }
                    case LinkInline image when image.IsImage:
                    {
                        var target = image.GetDynamicUrl?.Invoke() ?? image.Url ?? string.Empty;
                        yield return LoadImage(target, image.Title ?? string.Empty);
                        break;
                    }
                    case AutolinkInline autolink:
                    {
                        var display = autolink.Url;
                        var target = autolink.IsEmail ? $"mailto:{display}" : display;
                        yield return new CHyperlink([new CRun { Text = display }])
                        {
                            Command = url =>
                            {
                                if (HyperlinkCommand?.CanExecute(url) == true) HyperlinkCommand.Execute(url);
                            },
                            CommandParameter = target
                        };
                        break;
                    }
                    case FootnoteLink footnoteLink when !footnoteLink.IsBackLink:
                        yield return CreateFootnoteReference(footnoteLink.Footnote.Order);
                        break;
                }
            }
        }

        private CHyperlink CreateFootnoteReference(int order)
        {
            var reference = new CHyperlink([new CRun { Text = $"[{order}]" }])
            {
                IsUnderline = false,
                TextVerticalAlignment = TextVerticalAlignment.Top,
                Command = url =>
                {
                    var command = HyperlinkCommand;
                    if (command?.CanExecute(url) == true)
                        command.Execute(url);
                },
                CommandParameter = $"#fn:{order}"
            };
            reference.Classes.Add(ClassNames.FootnoteReferenceClass);
            return reference;
        }

        private IEnumerable<CInline> RenderMixedFootnoteInlines(
            string blockSource,
            int blockSourceStart,
            ContainerInline root)
        {
            var links = EnumerateInlineTree(root)
                .OfType<FootnoteLink>()
                .Where(link => !link.IsBackLink)
                .OrderBy(link => link.Span.Start)
                .ToArray();

            if (links.Length == 0)
                return PrivateRunSpanGamut(blockSource);

            var replacements = new Dictionary<int, int>();
            var source = blockSource;
            for (var index = links.Length - 1; index >= 0; index--)
            {
                var link = links[index];
                var localStart = link.Span.Start - blockSourceStart;
                var localEnd = link.Span.End - blockSourceStart;
                if (localStart < 0 || localEnd < localStart || localEnd >= source.Length)
                    continue;

                replacements[index] = link.Footnote.Order;
                source = source.Remove(localStart, localEnd - localStart + 1)
                    .Insert(localStart, CreateFootnotePlaceholder(index));
            }

            return ReplaceFootnotePlaceholders(PrivateRunSpanGamut(source), replacements).ToArray();
        }

        private static string CreateFootnotePlaceholder(int index) => $"\uE000FN{index}\uE001";

        private static readonly Regex _footnotePlaceholderPattern = new(
            "\\uE000FN(?<index>[0-9]+)\\uE001",
            RegexOptions.Compiled);

        private IEnumerable<CInline> ReplaceFootnotePlaceholders(
            IEnumerable<CInline> inlines,
            IReadOnlyDictionary<int, int> replacements)
        {
            foreach (var inline in inlines)
            {
                if (inline is CRun run && _footnotePlaceholderPattern.IsMatch(run.Text))
                {
                    var position = 0;
                    foreach (Match match in _footnotePlaceholderPattern.Matches(run.Text))
                    {
                        if (match.Index > position)
                            yield return new CRun { Text = run.Text.Substring(position, match.Index - position) };

                        if (int.TryParse(match.Groups["index"].Value, out var index) &&
                            replacements.TryGetValue(index, out var order))
                        {
                            yield return CreateFootnoteReference(order);
                        }
                        else
                        {
                            yield return new CRun { Text = match.Value };
                        }

                        position = match.Index + match.Length;
                    }

                    if (position < run.Text.Length)
                        yield return new CRun { Text = run.Text.Substring(position) };
                    continue;
                }

                if (inline is CSpan span)
                    span.Content = ReplaceFootnotePlaceholders(span.Content, replacements).ToArray();

                yield return inline;
            }
        }

        private static IEnumerable<Inline> EnumerateInlineTree(ContainerInline root)
        {
            for (var node = root.FirstChild; node is not null; node = node.NextSibling)
            {
                yield return node;
                if (node is ContainerInline container)
                    foreach (var child in EnumerateInlineTree(container)) yield return child;
            }
        }

        private sealed class RoutedHyperlinkCommand(Markdown owner) : ICommand
        {
            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter)
            {
                if (parameter is string target && target.TrimStart().StartsWith('#'))
                {
                    var provider = (IDocumentAnchorProvider)owner;
                    if (provider.AnchorNavigationRequested is not null)
                        return true;
                }

                var external = owner.ExternalHyperlinkCommand;
                return external?.CanExecute(parameter) == true;
            }

            public void Execute(object? parameter)
            {
                if (parameter is string target && target.TrimStart().StartsWith('#'))
                {
                    var provider = (IDocumentAnchorProvider)owner;
                    if (provider.AnchorNavigationRequested is { } navigate)
                    {
                        navigate(NormalizeAnchor(target));
                        return;
                    }
                }

                var external = owner.ExternalHyperlinkCommand;
                if (external?.CanExecute(parameter) == true)
                    external.Execute(parameter);
            }
        }

        private static readonly Regex _emojiShortcode = new(
            @":(?<name>[a-z0-9_+\-]+):",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string ReplaceEmojiShortcodes(string text)
            => _emojiShortcode.Replace(text, match =>
                EmojiTable.TryGet(match.Groups["name"].Value, out var emoji)
                    ? emoji ?? match.Value
                    : match.Value);

        private static CSpan CreateMarkedInline(IEnumerable<CInline> inlines)
        {
            var marked = new CSpan(inlines);
            marked.Classes.Add("Mark");
            return marked;
        }

        private static void DecodeHtmlEntities(IEnumerable<CInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case CRun run:
                        run.Text = WebUtility.HtmlDecode(run.Text);
                        break;
                    case CSpan span:
                        DecodeHtmlEntities(span.Content);
                        break;
                }
            }
        }

        private static void NormalizeInlineWhitespaceBoundaries(IEnumerable<CInline> inlines)
        {
            CRun? previous = null;
            foreach (var run in EnumerateRuns(inlines))
            {
                if (previous is not null
                    && previous.Text.EndsWith(' ')
                    && run.Text.StartsWith(' '))
                {
                    run.Text = run.Text.TrimStart(' ');
                }

                if (run.Text.Length > 0)
                    previous = run;
            }

            static IEnumerable<CRun> EnumerateRuns(IEnumerable<CInline> source)
            {
                foreach (var inline in source)
                {
                    if (inline is CRun run)
                    {
                        yield return run;
                    }
                    else if (inline is CSpan span)
                    {
                        foreach (var child in EnumerateRuns(span.Content))
                            yield return child;
                    }
                }
            }
        }
        #region html辅助

        /// <summary>
        /// 识别文本中所有HTML标签块（包括嵌套标签、自闭合标签），返回标签块的范围和内容
        /// </summary>
        /// <param name="text">待解析文本</param>
        /// <returns>HTML标签块的列表（起始索引、结束索引、标签内容）</returns>
        private List<(int StartIdx, int EndIdx, string Content)> ExtractHtmlBlocks(string text)
        {
            var htmlBlocks = new List<(int, int, string)>();
            int index = 0;
            var tagStack = new Stack<string>(); // 处理嵌套HTML标签的栈

            while (index < text.Length)
            {
                // 查找HTML标签起始符`<`
                int tagStart = text.IndexOf('<', index);
                if (tagStart == -1) break;

                // 查找HTML标签结束符`>`
                int tagEnd = text.IndexOf('>', tagStart);
                if (tagEnd == -1) break; // 不完整标签，忽略

                // 提取标签名（如div、span、img）
                string tagContent = text.Substring(tagStart, tagEnd - tagStart + 1);
                string tagName = GetHtmlTagName(tagContent);

                if (string.IsNullOrEmpty(tagName))
                {
                    // 非标签（如<!--注释-->、<!DOCTYPE>），直接作为HTML块
                    htmlBlocks.Add((tagStart, tagEnd, tagContent));
                    index = tagEnd + 1;
                    continue;
                }

                // 处理自闭合标签（如<img/>、<br/>）
                if (tagContent.EndsWith("/>") || IsSelfClosingTag(tagName))
                {
                    htmlBlocks.Add((tagStart, tagEnd, tagContent));
                    index = tagEnd + 1;
                    continue;
                }

                // 处理开始标签（如<div>），入栈并查找对应的结束标签
                if (!tagContent.StartsWith("</"))
                {
                    tagStack.Push(tagName);
                    // 递归查找匹配的结束标签（处理嵌套）
                    int closeTagEnd = FindMatchingCloseTag(text, tagEnd + 1, tagName);
                    if (closeTagEnd == -1)
                    {
                        // 无匹配结束标签，视为不完整标签，作为HTML块
                        htmlBlocks.Add((tagStart, tagEnd, tagContent));
                        index = tagEnd + 1;
                        tagStack.Pop();
                    }
                    else
                    {
                        // 完整的嵌套HTML块
                        string fullHtml = text.Substring(tagStart, closeTagEnd - tagStart + 1);
                        htmlBlocks.Add((tagStart, closeTagEnd, fullHtml));
                        index = closeTagEnd + 1;
                        tagStack.Pop();
                    }
                }
                else
                {
                    // 孤立的结束标签，直接作为HTML块
                    htmlBlocks.Add((tagStart, tagEnd, tagContent));
                    index = tagEnd + 1;
                    if (tagStack.Count > 0 && tagStack.Peek() == tagName)
                        tagStack.Pop();
                }
            }

            return htmlBlocks;
        }

        /// <summary>
        /// 提取HTML标签的标签名（如&lt;div class="a"&gt; → div）
        /// </summary>
        private string GetHtmlTagName(string tag)
        {
            var match = _htmlTagNamePattern.Match(tag);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
        }

        /// <summary>
        /// 判断是否为HTML自闭合标签
        /// </summary>
        private static bool IsSelfClosingTag(string tagName)
        {
            return _selfClosingTags.Contains(tagName);
        }

        /// <summary>
        /// 查找匹配的HTML结束标签（处理嵌套）
        /// </summary>
        private int FindMatchingCloseTag(string text, int startIndex, string tagName)
        {
            int index = startIndex;
            var stack = new Stack<string>();
            stack.Push(tagName);

            while (index < text.Length && stack.Count > 0)
            {
                int tagStart = text.IndexOf('<', index);
                if (tagStart == -1) break;

                int tagEnd = text.IndexOf('>', tagStart);
                if (tagEnd == -1) break;

                string tagContent = text.Substring(tagStart, tagEnd - tagStart + 1);
                string currentTag = GetHtmlTagName(tagContent);

                if (currentTag == tagName)
                {
                    if (tagContent.StartsWith("</"))
                    {
                        stack.Pop();
                        if (stack.Count == 0)
                            return tagEnd;
                    }
                    else
                    {
                        stack.Push(tagName); // 嵌套的同标签，入栈
                    }
                }

                index = tagEnd + 1;
            }

            return -1; // 未找到匹配的结束标签
        }

        /// <summary>
        /// 检查指定位置是否在HTML标签块内
        /// </summary>
        private bool IsInHtmlBlock(int position, List<(int StartIdx, int EndIdx, string Content)> htmlBlocks)
        {
            return htmlBlocks.Any(block => position >= block.StartIdx && position <= block.EndIdx);
        }

        #endregion

        #region 核心：通用成对符号解析（可扩展、支持嵌套）

        private List<(int Start, int End)> FindAllHtmlTags(string text)
        {
            var htmlRanges = new List<(int Start, int End)>();
            int index = 0;
            while (index < text.Length)
            {
                // 查找HTML标签起始符`<`
                int tagStart = text.IndexOf('<', index);
                if (tagStart == -1) break;

                // 查找HTML标签结束符`>`（处理自闭合标签和普通标签）
                int tagEnd = text.IndexOf('>', tagStart);
                if (tagEnd == -1) break; // 不完整标签，忽略

                // 添加标签范围（包含`<`和`>`）
                htmlRanges.Add((tagStart, tagEnd));

                // 继续查找下一个标签
                index = tagEnd + 1;
            }
            return htmlRanges;
        }

        /// <summary>
        /// 可扩展的成对符号配置（支持任意成对符号，按优先级排序：长符号优先，避免短符号截断长符号）
        /// 格式：(开始符号, 结束符号, 生成对应Inline元素的工厂方法)
        /// </summary>
// 显式指定 ImmutableList 的泛型参数为完整元组类型，解决类型推断失败问题
        private readonly ImmutableList<(
            string Start,
            string End,
            Func<IEnumerable<CInline>, CInline> ElementFactory
            )> _pairSymbols = ImmutableList.Create<(
            string Start,
            string End,
            Func<IEnumerable<CInline>, CInline> ElementFactory
            )>(
            // 优先级：长符号 > 短符号（避免 "**" 被 "*" 截断）
            ("***", "***", inlines => new CBold([new CItalic(inlines)])),
            ("___", "___", inlines => new CBold([new CItalic(inlines)])),
            ("**", "**", inlines => new CBold(inlines)), // 加粗
            ("__", "__", inlines => new CBold(inlines)), // 下划线
            ("~~", "~~", inlines => new CStrikethrough(inlines)), // 删除线
            ("==", "==", CreateMarkedInline),
            ("*", "*", inlines => new CItalic(inlines)), // 斜体
            ("_", "_", inlines => new CItalic(inlines)) // 斜体（下划线版）
            // 可扩展添加其他成对符号，例如：
            // ("===", "===", inlines => new CHighlight(inlines)), // 高亮
            // ("::", "::", inlines => new CCustom(inlines))       // 自定义符号
        );
        /// <summary>
        /// 判断字符是否为单词字符（字母、数字、下划线）
        /// </summary>
        private bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        /// <summary>
        /// 校验_/__符号的边界是否合法（仅前后为非单词字符/行首行尾/空白符时有效）
        /// </summary>
        /// <param name="text">原文本</param>
        /// <param name="startIdx">起始符号的索引</param>
        /// <param name="startSym">起始符号（_/__）</param>
        /// <param name="endIdx">结束符号的起始索引</param>
        /// <param name="endSym">结束符号（_/__）</param>
        /// <returns>边界是否合法</returns>
        private bool IsValidUnderscoreBoundary(string text, int startIdx, string startSym, int endIdx, string endSym)
        {
            // 仅对_/__生效，其他符号（如*//**）不校验
            if (startSym != "_" && startSym != "__") return true;

            // 1. 校验起始符号的前边界
            bool validStart = true;
            if (startIdx > 0)
            {
                char prevChar = text[startIdx - 1];
                // 前边界必须是：非单词字符（排除字母、数字、_） OR 空白符（空格、换行、制表符等）
                validStart = !char.IsLetterOrDigit(prevChar) && prevChar != '_' || char.IsWhiteSpace(prevChar);
            }
            // 行首则直接合法

            // 2. 校验结束符号的后边界
            bool validEnd = true;
            int endSymTotalIdx = endIdx + endSym.Length; // 结束符号的最后一个字符索引
            if (endSymTotalIdx < text.Length)
            {
                char nextChar = text[endSymTotalIdx];
                // 后边界必须是：非单词字符（排除字母、数字、_） OR 空白符 OR 换行
                validEnd = !char.IsLetterOrDigit(nextChar) && nextChar != '_' || char.IsWhiteSpace(nextChar);
            }
            // 行尾则直接合法

            // 3. 额外校验：_/__的内容不能为空（避免空标记如__、_）
            int contentStart = startIdx + startSym.Length;
            int contentLength = endIdx - contentStart;
            if (contentLength <= 0)
            {
                return false;
            }

            return validStart && validEnd;
        }

        private bool IsRangeOverlapped(int targetStart, int targetEnd, List<(int Start, int End)> usedRanges)
        {
            foreach (var (usedStart, usedEnd) in usedRanges)
            {
                // 目标范围与已占用范围有重叠 → 返回true
                if (targetStart < usedEnd && targetEnd > usedStart)
                    return true;
            }
            return false;
        }
        /// <summary>
        /// 递归解析所有嵌套的成对符号（分阶段第一步：先处理完所有成对符号）
        /// </summary>
        /// <param name="text">当前要解析的文本</param>
        /// <param name="level">解析层级（用于Debug缩进）</param>
        /// <returns>成对符号解析后的Inline元素列表</returns>
        private List<CInline> ParseAllNestedPairs(string text, int level)
        {
            var result = new List<CInline>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }

            // 第一步：提取所有HTML标签块，按位置排序
            var htmlBlocks = ExtractHtmlBlocks(text)
                .OrderBy(b => b.StartIdx)
                .ToList();

            if (htmlBlocks.Count == 0)
            {
                // 无HTML块，按原逻辑处理成对符号
                return ParsePureMarkdownPairs(text, level);
            }

            // 第二步：将文本拆分为「HTML块」和「Markdown文本块」，分段处理
            int lastPosition = 0;
            foreach (var htmlBlock in htmlBlocks)
            {
                int htmlStart = htmlBlock.StartIdx;
                int htmlEnd = htmlBlock.EndIdx;

                // 1. 处理HTML块之前的Markdown文本
                if (htmlStart > lastPosition)
                {
                    string markdownText = text.Substring(lastPosition, htmlStart - lastPosition);
                    if (string.IsNullOrWhiteSpace(markdownText))
                    {
                        // HTML collapses inter-element whitespace to one visible
                        // space. A standalone regular-space run can lose its width
                        // in the custom formatter, so preserve it as NBSP.
                        result.Add(new CRun { Text = "\u00A0" });
                    }
                    else
                    {
                        var markdownInlines = ParsePureMarkdownPairs(markdownText, level + 1);
                        result.AddRange(markdownInlines);
                    }
                }

                // 2. 处理HTML块（交给 HTML 插件处理）
                string htmlContent = htmlBlock.Content;
                var htmlInlines = ProcessWithHTMLParsers(htmlContent, level + 1, isBlockContext: false);
                result.AddRange(htmlInlines);

                // 更新最后处理位置
                lastPosition = htmlEnd + 1;
            }

                        // 3. 处理最后一个HTML块之后的Markdown文本
                        if (lastPosition < text.Length)
                        {
                            string remainingMarkdown = text.Substring(lastPosition);
                            // 去除前导换行符，避免HTML内联标签后产生不必要的间隔
                            remainingMarkdown = remainingMarkdown.TrimStart('\n', '\r');
                            if (!string.IsNullOrEmpty(remainingMarkdown))
                            {
                                if (string.IsNullOrWhiteSpace(remainingMarkdown))
                                    result.Add(new CRun { Text = "\u00A0" });
                                else
                                    result.AddRange(ParsePureMarkdownPairs(remainingMarkdown, level + 1));
                            }
                        }

            return result;
        }

        /// <summary>
        /// 处理纯Markdown文本（无HTML块）的成对符号，复用原有的成对解析逻辑
        /// </summary>
        /// <summary>
        /// 处理纯Markdown文本（无HTML块）的成对符号，复用原有的成对解析逻辑
        /// </summary>
        private List<CInline> ParsePureMarkdownPairs(string text, int level)
        {
            var result = new List<CInline>();
            var (found, startSym, endSym, elementFactory, startIdx, endIdx) = FindInnermostPair(text, level);

            if (!found)
            {
                // 无成对符号，执行HTML解析（传递isBlockContext=true，避免循环）
                var parserResult = ProcessWithHTMLParsers(text, level, isBlockContext: true);
                result.AddRange(parserResult);
                return result;
            }

            // 拆分前/中/后文本，递归解析
            string preText = text.Substring(0, startIdx);
            string middleText = text.Substring(startIdx + startSym.Length, endIdx - (startIdx + startSym.Length));
            string postText = text.Substring(endIdx + endSym.Length);

            // 处理前缀
            if (!string.IsNullOrEmpty(preText))
            {
                var preResult = ParseAllNestedPairs(preText, level + 1); // 递归：前缀可能包含HTML
                result.AddRange(preResult);
            }

            // 处理中间内容（递归：中间可能包含HTML）
            var middleResult = ParseAllNestedPairs(middleText, level + 1);
            var pairElement = elementFactory(middleResult);
            result.Add(pairElement);

            // 处理后缀
            if (!string.IsNullOrEmpty(postText))
            {
                var postResult = ParseAllNestedPairs(postText, level + 1); // 递归：后缀可能包含HTML
                result.AddRange(postResult);
            }

            return result;
        }
        private const int _maxNestDepth = 20;
        /// <summary>
        /// 查找文本中最内层的成对符号（核心：避免外层符号截断内层）
        /// </summary>
        /// <returns>是否找到、开始符号、结束符号、元素工厂、开始索引、结束索引</returns>
        private (bool Found, string StartSym, string EndSym, Func<IEnumerable<CInline>, CInline> ElementFactory, int StartIdx, int EndIdx) FindInnermostPair(string text, int level)
        {
            if (level > _maxNestDepth)
            {
                return (false, "", "", null, -1, -1);
            }
            var longPairs = _pairSymbols.Where(p => p.Start.Length >= 2).ToList();
            var shortPairs = _pairSymbols.Where(p => p.Start.Length == 1).ToList();

            var allValidPairs = new List<(string Start, string End, Func<IEnumerable<CInline>, CInline> Factory, int StartIdx, int EndIdx, int NestLevel)>();
            var longSymbolPositions = new List<(int Start, int End)>(); // 记录所有__的位置（无论是否有效），用于隔离_

            // 步骤1：处理长符号__（优先收集，强制隔离_）
            foreach (var (start, end, factory) in longPairs)
            {
                int currentStartIdx = 0;
                while (true)
                {
                    currentStartIdx = FindNonEscaped(text, start, currentStartIdx);
                    if (currentStartIdx == -1) break;

                    int currentEndIdx = FindNonEscaped(text, end, currentStartIdx + start.Length);
                    if (currentEndIdx == -1)
                    {
                        currentStartIdx += start.Length;
                        continue;
                    }

                    // 关键1：记录所有__的位置（即使边界无效），不让_拆分匹配
                    int longSymEnd = currentEndIdx + end.Length;
                    longSymbolPositions.Add((currentStartIdx, longSymEnd));

                    // 关键2：仅当边界有效时，才视为粗体匹配
                    if (IsValidUnderscoreBoundary(text, currentStartIdx, start, currentEndIdx, end))
                    {
                        int nestLevel = CalculateNestLevel(text, currentStartIdx + start.Length, currentEndIdx, level);
                        allValidPairs.Add((start, end, factory, currentStartIdx, currentEndIdx, nestLevel));
                    }

                    currentStartIdx = longSymEnd;
                }
            }

            // 步骤2：处理短符号_（严格跳过长符号__的所有位置）
            foreach (var (start, end, factory) in shortPairs)
            {
                int currentStartIdx = 0;
                while (true)
                {
                    currentStartIdx = FindNonEscaped(text, start, currentStartIdx);
                    if (currentStartIdx == -1) break;

                    int shortSymStart = currentStartIdx;
                    int shortSymEnd = currentStartIdx + start.Length;

                    // 关键3：如果_的位置与任何__的位置重叠，直接跳过（不拆分__）
                    bool isOverlapWithLongSym = longSymbolPositions.Any(l =>
                        shortSymStart < l.End && shortSymEnd > l.Start);
                    if (isOverlapWithLongSym)
                    {
                        currentStartIdx = shortSymEnd;
                        continue;
                    }

                    int currentEndIdx = FindNonEscaped(text, end, shortSymEnd);
                    if (currentEndIdx == -1) break;

                    int shortEndTotal = currentEndIdx + end.Length;
                    // 结束符号也不能与__重叠
                    bool isEndOverlap = longSymbolPositions.Any(l =>
                        currentEndIdx < l.End && shortEndTotal > l.Start);
                    if (isEndOverlap)
                    {
                        currentStartIdx = shortEndTotal;
                        continue;
                    }

                    // 边界校验：_必须符合规则才视为斜体
                    if (IsValidUnderscoreBoundary(text, shortSymStart, start, currentEndIdx, end))
                    {
                        int nestLevel = CalculateNestLevel(text, shortSymEnd, currentEndIdx, level);
                        allValidPairs.Add((start, end, factory, shortSymStart, currentEndIdx, nestLevel));
                    }

                    currentStartIdx = shortEndTotal;
                }
            }

            if (allValidPairs.Count == 0)
                return (false, "", "", null, -1, -1);

            // 排序规则：长符号优先 → 最内层优先 → 结束位置靠前
            var bestPair = allValidPairs
                .OrderByDescending(p => p.Start.Length) // 长符号（__）优先级 > 短符号（_）
                .ThenByDescending(p => p.NestLevel)
                .ThenBy(p => p.EndIdx)
                .First();

            return (true, bestPair.Start, bestPair.End, bestPair.Factory, bestPair.StartIdx, bestPair.EndIdx);
        }

        /// <summary>
        /// 查找非转义的目标字符串（跳过 \ 开头的符号）
        /// </summary>
        private static int FindNonEscaped(string text, string target, int startFrom)
        {
            int index = text.IndexOf(target, startFrom, StringComparison.Ordinal);
            while (index != -1)
            {
                // 检查是否被转义（前面不是 \，或前面是 \\ 双重转义）
                bool isEscaped = index > 0 && text[index - 1] == '\\';
                if (isEscaped)
                {
                    index = text.IndexOf(target, index + target.Length, StringComparison.Ordinal);
                    continue;
                }

                // 优化：如果目标是长符号（≥2字符），检查是否被短符号"截断"
                if (target.Length >= 2)
                {
                    bool isTruncated = false;
                    // 检查长符号是否由相同字符组成（如 ** 应该全是 *）
                    char shortChar = target[0];
                    var span = text.AsSpan(index, target.Length);
                    foreach (char c in span)
                    {
                        if (c != shortChar)
                        {
                            isTruncated = true;
                            break;
                        }
                    }

                    if (isTruncated)
                    {
                        index = text.IndexOf(target, index + target.Length, StringComparison.Ordinal);
                        continue;
                    }
                }

                return index;
            }
            return -1;
        }

        /// <summary>
        /// 计算文本片段的嵌套层级（用于判断最内层成对符号）
        /// </summary>
        private int CalculateNestLevel(string text, int startIdx, int endIdx, int level)
        {
            int nestLevel = 0;
            int currentIdx = startIdx;

            while (currentIdx < endIdx)
            {
                bool matched = false;
                // 遍历所有成对符号，统计嵌套次数
                foreach (var (start, end, _) in _pairSymbols)
                {
                    if (currentIdx + start.Length <= endIdx && text.AsSpan(currentIdx, start.Length).SequenceEqual(start.AsSpan()))
                    {
                        nestLevel++;
                        currentIdx += start.Length;
                        matched = true;
                        break;
                    }
                    if (currentIdx + end.Length <= endIdx && text.AsSpan(currentIdx, end.Length).SequenceEqual(end.AsSpan()))
                    {
                        nestLevel--;
                        currentIdx += end.Length;
                        matched = true;
                        break;
                    }
                }
                // 只有在没有匹配到任何符号时才递增索引
                if (!matched)
                {
                    currentIdx++;
                }
            }

            return nestLevel;
        }

        #endregion

        #region 原Parser处理逻辑（分阶段第二步：处理非成对符号文本）

        /// <summary>
        /// 块级 HTML 标签集合（用于判断是否需要包装成 CInlineUIContainer）
        /// </summary>
        private static readonly HashSet<string> _blockHtmlTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "address",
            "article",
            "aside",
            "blockquote",
            "center",
            "dd",
            "details",
            "dialog",
            "dir",
            "div",
            "dl",
            "dt",
            "fieldset",
            "figcaption",
            "figure",
            "footer",
            "form",
            "h1",
            "h2",
            "h3",
            "h4",
            "h5",
            "h6",
            "header",
            "hr",
            "li",
            "main",
            "menu",
            "nav",
            "ol",
            "p",
            "pre",
            "section",
            "summary",
            "table",
            "tbody",
            "td",
            "tfoot",
            "th",
            "thead",
            "tr",
            "ul"
        };

        /// <summary>
        /// 判断 HTML 标签是否为块级标签
        /// </summary>
        private static bool IsBlockHtmlTag(string tagName)
        {
            return _blockHtmlTags.Contains(tagName);
        }

        /// <summary>
        /// 提取HTML标签的内部内容（去除开始和结束标签）
        /// 例如：&lt;details&gt;内容&lt;/details&gt; → 内容
        /// </summary>
        private string ExtractHtmlInnerContent(string html, string tagName)
        {
            if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(tagName))
                return string.Empty;

            // 查找开始标签的结束位置
            int openTagEnd = html.IndexOf('>');
            if (openTagEnd == -1)
                return string.Empty;

            // 查找结束标签的开始位置
            string closeTag = $"</{tagName}>";
            int closeTagStart = html.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (closeTagStart == -1)
                return string.Empty;

            // 提取内部内容
            int contentStart = openTagEnd + 1;
            int contentLength = closeTagStart - contentStart;
            if (contentLength <= 0)
                return string.Empty;

            return html.Substring(contentStart, contentLength);
        }

        /// <summary>
        /// 处理HTML解析，区分行级和块级标签
        /// - 行级标签（如 font、span）：直接返回 CInline 元素
        /// - 块级标签（如 div）：包装成 CInlineUIContainer
        /// </summary>
        /// <param name="text">待解析文本</param>
        /// <param name="level">递归层级</param>
        /// <param name="isBlockContext">是否为块级解析上下文（若是则跳过块级解析）</param>
        private List<CInline> ProcessWithHTMLParsers(string text, int level, bool isBlockContext = false)
        {
            var rtn = new List<CInline>();

            // 递归深度检查，防止栈溢出
            if (_currentRecursionDepth >= _maxRecursionDepth)
            {
                // 超过最大递归深度，直接返回纯文本
                rtn.Add(new CRun { Text = text });
                return rtn;
            }

            // 检查是否为 HTML 标签
            string tagName = GetHtmlTagName(text);

            if (!string.IsNullOrEmpty(tagName) && !isBlockContext)
            {
                // 判断是行级还是块级标签
                if (IsBlockHtmlTag(tagName))
                {
                    // 对于 details/summary 等特殊标签，提取内部内容进行解析
                    string innerContent = ExtractHtmlInnerContent(text, tagName);
                    
                    if (!string.IsNullOrEmpty(innerContent))
                    {
                        // 块级标签：解析内部内容，然后包装成 CInlineUIContainer
                        var parseStatus = new ParseStatus(_supportTextAlignment);
                        var blockElements = new List<DocumentElement>();
                        int blockParseIndex = 0;
                        int contentLength = innerContent.Length;

                        _currentRecursionDepth++;
                        try
                        {
                            ProcessBlockGamut(innerContent, ref blockParseIndex, contentLength, parseStatus, blockElements);
                        }
                        finally
                        {
                            _currentRecursionDepth--;
                        }

                        foreach (var blockElement in blockElements)
                        {
                            var innerControl = blockElement.Control;
                            innerControl.VerticalAlignment = VerticalAlignment.Top;
                            innerControl.Margin = new Thickness(0, 8, 0, 0);

                            if (innerControl is Panel panel)
                            {
                                foreach (var child in panel.Children)
                                {
                                    child.VerticalAlignment = VerticalAlignment.Top;
                                    child.Margin = new Thickness(0);
                                }
                            }

                            var inlineUIContainer = new CInlineUIContainer(innerControl)
                            {
                                TextVerticalAlignment = TextVerticalAlignment.Center,
                            };
                            rtn.Add(inlineUIContainer);
                        }
                    }
                    else
                    {
                        // 无法提取内部内容，直接作为文本处理
                        OriginalRunSpanRest(text, 0, text.Length, 0, rtn);
                    }
                }
                else
                {
                    // 行级标签（如 font、span）：直接使用行级解析器处理
                    OriginalRunSpanRest(text, 0, text.Length, 0, rtn);
                }
            }
            else
            {
                // 非 HTML 标签或已在块级上下文中：直接使用行级解析器
                OriginalRunSpanRest(text, 0, text.Length, 0, rtn);
            }

            return rtn;
        }

        /// <summary>
        /// 复用原 PrivateRunBlockGamut 的块级解析逻辑，提取为独立方法
        /// 处理 _topBlockParsers + _blockParsers，将结果存入 blockElements
        /// </summary>
        private void ProcessBlockGamut(string text, ref int index, int length, ParseStatus status, List<DocumentElement> blockElements)
        {
            var candidates = new List<Candidate<BlockParser2>>();

            // 第一步：处理顶级块解析器 _topBlockParsers
            for (;;)
            {
                candidates.Clear();
                foreach (var parser in _topBlockParsers)
                {
                    var match = parser.Pattern.Match(text, index, length);
                    if (match.Success)
                        candidates.Add(new Candidate<BlockParser2>(match, parser));
                }

                if (candidates.Count == 0)
                    break;

                candidates.Sort(); // 按匹配位置排序

                IEnumerable<DocumentElement>? result = null;
                int bestBegin = 0;
                int bestEnd = 0;

                foreach (var c in candidates)
                {
                    result = c.Parser.Convert2(text, c.Match, status, this, out bestBegin, out bestEnd);
                    if (result is not null)
                        break;
                }

                if (result is null)
                    break;

                // 处理顶级块之前的文本（交给次级块解析器）
                if (bestBegin > index)
                {
                    ProcessSubBlockGamut(text, index, bestBegin - index, status, blockElements);
                }

                blockElements.AddRange(result);

                // 更新索引和剩余长度
                length -= bestEnd - index;
                index = bestEnd;
            }

            // 第二步：处理剩余文本的次级块解析器 _blockParsers
            if (index < text.Length)
            {
                ProcessSubBlockGamut(text, index, text.Length - index, status, blockElements);
            }
        }

        /// <summary>
        /// 处理次级块解析器 _blockParsers（复用原 RunBlockRest 逻辑）
        /// </summary>
        private void ProcessSubBlockGamut(string text, int index, int length, ParseStatus status, List<DocumentElement> blockElements)
        {
            for (int parserStart = 0; parserStart < _blockParsers.Length; ++parserStart)
            {
                var parser = _blockParsers[parserStart];

                for (;;)
                {
                    var match = parser.Pattern.Match(text, index, length);
                    if (!match.Success)
                        break;

                    var rslt = parser.Convert2(text, match, status, this, out int parseBegin, out int parserEnd);
                    if (rslt is null)
                        break;

                    // 处理当前块之前的文本（递归交给下一个次级解析器）
                    if (parseBegin > index)
                    {
                        ProcessSubBlockGamut(text, index, parseBegin - index, status, blockElements);
                    }

                    blockElements.AddRange(rslt);

                    // 更新索引和剩余长度
                    length -= parserEnd - index;
                    index = parserEnd;

                    if (length == 0)
                        break;
                }

                if (length == 0)
                    break;
            }

            // 最后：未被块解析器处理的文本，转为段落（复用原 FormParagraphs 逻辑）
            if (length != 0)
            {
                string remainingText = text.Substring(index, length);
                var paragraphs = FormParagraphs(remainingText, status);
                blockElements.AddRange(paragraphs);
            }
        }

        /// <summary>
        /// 原有行级解析逻辑（_inlines），提取为独立方法，直接操作结果列表
        /// </summary>
        private void OriginalRunSpanRest(
            string txt,
            int index,
            int length,
            int parserStart,
            List<CInline> result)
        {
            for (; parserStart < _inlines.Length; ++parserStart)
            {
                var parser = _inlines[parserStart];

                for (;;)
                {
                    var match = parser.Pattern.Match(txt, index, length);
                    if (!match.Success)
                    {
                        break;
                    }

                    var rslt = parser.Convert(txt, match, this, out int parseBegin, out int parserEnd);
                    if (rslt is null)
                    {
                        break;
                    }

                    if (parseBegin > index)
                    {
                        OriginalRunSpanRest(txt, index, parseBegin - index, parserStart + 1, result);
                    }

                    result.AddRange(rslt);

                    length -= parserEnd - index;
                    index = parserEnd;

                    if (length == 0)
                    {
                        break;
                    }
                }

                if (length == 0)
                    break;
            }

            // 未被行级解析器处理的文本，转为普通文本
            if (length != 0)
            {
                var subtext = txt.Substring(index, length);
                var textInlines = StrictBoldItalic ? DoText(subtext) : DoTextDecorations(subtext, DoText);
                result.AddRange(textInlines);
            }
        }

        #endregion

        #region grammer - paragraph

        private static readonly Regex _align = new(@"^p([<=>])\.", RegexOptions.Compiled);
        private static readonly Regex _newlinesLeadingTrailing = new(@"^\n+|\n+\z", RegexOptions.Compiled);
        private static readonly Regex _newlinesMultiple = new(@"\n{2,}", RegexOptions.Compiled);

        /// <summary>
        /// splits on two or more newlines, to form "paragraphs";
        /// </summary>
        private IEnumerable<DocumentElement> FormParagraphs(string text, ParseStatus status)
        {
            // 递归深度检查，防止栈溢出
            if (_currentRecursionDepth >= _maxRecursionDepth)
            {
                // 超过最大递归深度，直接返回纯文本，不再递归解析
                yield return new CTextBlockElement(new[] { new CRun { Text = text } }, ParagraphClass);
                yield break;
            }

            var trimemdText = _newlinesLeadingTrailing.Replace(text, "");

            string[] grafs = trimemdText == "" ? new string[0] : _newlinesMultiple.Split(trimemdText);

            foreach (var g in grafs)
            {
                var chip = g;

                TextAlignment? indiAlignment = null;

                if (status.SupportTextAlignment)
                {
                    var alignMatch = _align.Match(chip);
                    if (alignMatch.Success)
                    {
                        chip = chip.Substring(alignMatch.Length);
                        switch (alignMatch.Groups[1].Value)
                        {
                            case "<":
                                indiAlignment = TextAlignment.Left;
                                break;
                            case ">":
                                indiAlignment = TextAlignment.Right;
                                break;
                            case "=":
                                indiAlignment = TextAlignment.Center;
                                break;
                        }
                    }
                }

                _currentRecursionDepth++;
                try
                {
                    var inlines = PrivateRunSpanGamut(chip);
                    var ctbox = indiAlignment.HasValue ? new CTextBlockElement(inlines, ParagraphClass, indiAlignment.Value) : new CTextBlockElement(inlines, ParagraphClass);
                    yield return ctbox;
                }
                finally
                {
                    _currentRecursionDepth--;
                }
            }
        }
        #endregion

        #region grammer - image or href

        private static readonly Regex _autoLink = new(@"
            (?<![\w@])
            (?:
                <(?<angle>(?:https?://|mailto:)[^>\s]+|[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})>
                |
                (?<url>https?://[^\s<>]+?[A-Z0-9/#])(?=$|[\s\p{P}])
                |
                (?<email>[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,})
            )",
            RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        private CInline AutoLinkEvaluator(Match match)
        {
            var value = match.Groups["angle"].Success
                ? match.Groups["angle"].Value
                : match.Groups["url"].Success
                    ? match.Groups["url"].Value
                    : match.Groups["email"].Value;

            var isEmail = !value.Contains("://", StringComparison.Ordinal)
                          && !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
            var target = isEmail ? $"mailto:{value}" : value;
            var display = value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                ? value[7..]
                : value;

            return new CHyperlink([new CRun { Text = display }])
            {
                Command = url =>
                {
                    if (HyperlinkCommand?.CanExecute(url) == true)
                        HyperlinkCommand.Execute(url);
                },
                CommandParameter = target
            };
        }

        private static readonly Regex _imageOrHrefInline = new(string.Format(@"
                (                           # wrap whole match in $1
                    (!)?                    # image maker = $2
                    \[
                        ({0})               # link text = $3
                    \]
                    \(                      # literal paren
                        [ ]*
                        ({1})               # href = $4
                        [ ]*
                        (                   # $5
                        (['""])             # quote char = $6
                        (.*?)               # title = $7
                        \6                  # matching quote
                        [ ]*                # ignore any spaces between closing quote and )
                        )?                  # title is optional
                    \)
                )", GetNestedBracketsPattern(), GetNestedParensPattern()),
            RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);


        private CInline ImageOrHrefInlineEvaluator(Match match)
        {
            if (String.IsNullOrEmpty(match.Groups[2].Value))
            {
                return TreatsAsHref(match);
            }
            else
            {
                return TreatsAsImage(match);
            }
        }


        private CInline TreatsAsHref(Match match)
        {
            string linkText = match.Groups[3].Value;
            string url = match.Groups[4].Value;
            string title = match.Groups[7].Value;

            var link = new CHyperlink(PrivateRunSpanGamut(linkText))
            {
                Command = (urlTxt) =>
                {
                    if (HyperlinkCommand != null && HyperlinkCommand.CanExecute(urlTxt))
                    {
                        HyperlinkCommand.Execute(urlTxt);
                    }
                },

                CommandParameter = url
            };

            if (!String.IsNullOrEmpty(title)
                && !title.Any(ch => !Char.IsLetterOrDigit(ch)))
            {
                link.Classes.Add(title);
            }

            return link;
        }

        private CInline TreatsAsImage(Match match)
        {
            string altText = match.Groups[3].Value;
            string urlTxt = match.Groups[4].Value;
            string title = match.Groups[7].Value;

            return LoadImage(urlTxt, title);
        }

        private CInline LoadImage(string urlTxt, string title)
        {
            if (UseResource && CascadeResources.TryGet(urlTxt, out var resourceVal))
            {
                if (resourceVal is Control control)
                {
                    return new CInlineUIContainer(control);
                }

                CImage? cimg = null;
                if (resourceVal is Bitmap renderedImage)
                {
                    cimg = new CImage(renderedImage);
                }
                if (resourceVal is IEnumerable<Byte> byteEnum)
                {
                    try
                    {
                        using (var memstream = new MemoryStream(byteEnum.ToArray()))
                        {
                            var bitmap = new Bitmap(memstream);
                            cimg = new CImage(bitmap);
                        }
                    }
                    catch { }
                }

                if (cimg is not null)
                {
                    cimg.ClickCommand = new ImageOpenCommand();
                    // 命令参数为图片的原始路径/URL（urlTxt）
                    cimg.ClickCommandParameter = cimg;

                    if (!String.IsNullOrEmpty(title)
                        && title.All(Char.IsLetterOrDigit))
                    {
                        cimg.Classes.Add(title);
                    }
                    return cimg;
                }
            }

            CImage image = _setupInfo.LoadImage(urlTxt);
            image.ClickCommand = new ImageOpenCommand();
            image.ClickCommandParameter = image; // 传递图片路径/URL

            if (!String.IsNullOrEmpty(title)
                && title.All(char.IsLetterOrDigit))
            {
                image.Classes.Add(title);
            }

            return image;
        }

        #endregion

        #region grammer - code

        //    * You can use multiple backticks as the delimiters if you want to
        //        include literal backticks in the code span. So, this input:
        //
        //        Just type ``foo `bar` baz`` at the prompt.
        //
        //        Will translate to:
        //
        //          <p>Just type <code>foo `bar` baz</code> at the prompt.</p>
        //
        //        There's no arbitrary limit to the number of backticks you
        //        can use as delimters. If you need three consecutive backticks
        //        in your code, use four for delimiters, etc.
        //
        //    * You can use spaces to get literal backticks at the edges:
        //
        //          ... type `` `bar` `` ...
        //
        //        Turns to:
        //
        //          ... type <code>`bar`</code> ...         
        //
        private static readonly Regex _codeSpan = new(@"
                    (?<!\\)   # Character before opening ` can't be a backslash
                    (`+)      # $1 = Opening run of `
                    (.+?)     # $2 = The code block
                    (?<!`)
                    \1
                    (?!`)", RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);

        private CCode CodeSpanEvaluator(Match match)
        {
            string span = match.Groups[2].Value;
            span = Regex.Replace(span, @"^[ ]*", ""); // leading whitespace
            span = Regex.Replace(span, @"[ ]*$", ""); // trailing whitespace

            var result = new CCode(new[]
            {
                new CRun()
                {
                    Text = span
                }
            });

            return result;
        }

        #endregion

        #region grammer - textdecorations

        private static readonly Regex _strictBold = new(@"([\W_]|^) (\*\*|__) (?=\S) ([^\r]*?\S[\*_]*) \2 ([\W_]|$)",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _strictItalic = new(@"([\W_]|^) (\*|_) (?=\S) ([^\r\*_]*?\S) \2 ([\W_]|$)",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _strikethrough = new(@"(~~) (?=\S) (.+?) (?<=\S) \1",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex _underline = new(@"(__) (?=\S) (.+?) (?<=\S) \1",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Turn Markdown *italics* and **bold** into HTML strong and em tags
        /// </summary>
        private IEnumerable<CInline> DoTextDecorations(string text, Func<string, IEnumerable<CInline>> defaultHandler)
        {
            var rtn = new List<CInline>();

            var buff = new StringBuilder();

            void HandleBefore()
            {
                if (buff.Length > 0)
                {
                    rtn.AddRange(defaultHandler(buff.ToString()));
                    buff.Clear();
                }
            }

            for (var i = 0; i < text.Length; ++i)
            {
                var ch = text[i];
                switch (ch)
                {
                    default:
                        buff.Append(ch);
                        break;

                    case '\\': // escape
                        if (++i < text.Length)
                        {
                            switch (text[i])
                            {
                                default:
                                    buff.Append('\\').Append(text[i]);
                                    break;

                                case '\\': // escape
                                case ':': // bold? or italic
                                case '*': // bold? or italic
                                case '~': // strikethrough?
                                case '_': // underline?
                                case '%': // color?
                                    buff.Append(text[i]);
                                    break;
                            }
                        }
                        else
                            buff.Append('\\');

                        break;

                    case ':': // emoji?
                    {
                        var nxtI = text.IndexOf(':', i + 1);
                        if (nxtI != -1 && EmojiTable.TryGet(text.Substring(i + 1, nxtI - i - 1), out var emoji))
                        {
                            buff.Append(emoji);
                            i = nxtI;
                        }
                        else buff.Append(':');
                        break;
                    }

                    case '*': // bold? or italic
                    {
                        var oldI = i;
                        var inline = ParseAsBoldOrItalic(text, ref i);
                        if (inline == null)
                        {
                            buff.Append(text, oldI, i - oldI + 1);
                        }
                        else
                        {
                            HandleBefore();
                            rtn.Add(inline);
                        }
                        break;
                    }

                    case '~': // strikethrough?
                    {
                        var oldI = i;
                        var inline = ParseAsStrikethrough(text, ref i);
                        if (inline == null)
                        {
                            buff.Append(text, oldI, i - oldI + 1);
                        }
                        else
                        {
                            HandleBefore();
                            rtn.Add(inline);
                        }
                        break;
                    }

                    // case '_': // underline?
                    // {
                    //     var oldI = i;
                    //     var inline = ParseAsUnderline(text, ref i);
                    //     if (inline == null)
                    //     {
                    //         buff.Append(text, oldI, i - oldI + 1);
                    //     }
                    //     else
                    //     {
                    //         HandleBefore();
                    //         rtn.Add(inline);
                    //     }
                    //     break;
                    // }

                    case '%': // color?
                    {
                        var oldI = i;
                        var inline = ParseAsColor(text, ref i);
                        if (inline == null)
                        {
                            buff.Append(text, oldI, i - oldI + 1);
                        }
                        else
                        {
                            HandleBefore();
                            rtn.Add(inline);
                        }
                        break;
                    }
                }
            }

            if (buff.Length > 0)
            {
                rtn.AddRange(defaultHandler(buff.ToString()));
            }

            return rtn;
        }

        /// <summary>
        /// 通用的成对标记解析方法（用于下划线、删除线等）
        /// </summary>
        /// <typeparam name="T">返回的 CInline 类型</typeparam>
        /// <param name="text">待解析文本</param>
        /// <param name="start">起始位置（会被修改）</param>
        /// <param name="marker">标记字符（如 '_' 或 '~'）</param>
        /// <param name="minCount">最少需要的标记数量</param>
        /// <param name="factory">创建结果元素的工厂方法</param>
        /// <returns>解析结果，失败返回 null</returns>
        private T? ParseAsDecoration<T>(string text,
            ref int start,
            char marker,
            int minCount,
            Func<IEnumerable<CInline>, T> factory) where T : CInline
        {
            var bgnCnt = CountRepeat(text, start, marker);
            int last = EscapedIndexOf(text, start + bgnCnt, marker);
            int endCnt = last >= 0 ? CountRepeat(text, last, marker) : -1;

            if (endCnt >= minCount && bgnCnt >= minCount)
            {
                int cnt = minCount;
                int bgn = start + cnt;
                int end = last;

                // 递归解析内部内容
                var innerText = text.Substring(bgn, end - bgn);
                var innerInlines = PrivateRunSpanGamut(innerText);

                start = end + cnt - 1;
                return factory(innerInlines);
            }
            else
            {
                start += bgnCnt - 1;
                return null;
            }
        }

        private CUnderline? ParseAsUnderline(string text, ref int start)
            => ParseAsDecoration(text, ref start, '_', 2, inlines => new CUnderline(inlines));

        private CStrikethrough? ParseAsStrikethrough(string text, ref int start)
            => ParseAsDecoration(text, ref start, '~', 2, inlines => new CStrikethrough(inlines));

        private CInline? ParseAsBoldOrItalic(string text, ref int start)
        {
            // count asterisk (bgn)
            var bgnCnt = CountRepeat(text, start, '*');

            int last = EscapedIndexOf(text, start + bgnCnt, '*');

            int endCnt = last >= 0 ? CountRepeat(text, last, '*') : -1;

            if (endCnt >= 1)
            {
                int cnt = Math.Min(bgnCnt, endCnt);
                int bgn = start + cnt;
                int end = last;

                // 核心修改：递归解析 **/* 内部的内容（比如链接）
                var innerText = text.Substring(bgn, end - bgn);
                var innerInlines = PrivateRunSpanGamut(innerText); // 递归！解析内部的链接/其他格式

                switch (cnt)
                {
                    case 1: //  italic
                        start = end + cnt - 1;
                        return new CItalic(innerInlines); // 用递归解析后的内容创建斜体
                    case 2: // bold
                        start = end + cnt - 1;
                        return new CBold(innerInlines); // 用递归解析后的内容创建加粗
                    default: // >3; bold-italic
                        bgn = start + 3;
                        start = end + 3 - 1;
                        var inline = new CItalic(innerInlines);
                        return new CBold(new[]
                        {
                            inline
                        });
                }
            }
            else
            {
                start += bgnCnt - 1;
                return null;
            }
        }

        private CInline? ParseAsColor(string text, ref int start)
        {
            if (start + 1 >= text.Length)
                return null;

            if (text[start + 1] != '{')
                return null;

            int end = text.IndexOf('}', start + 1);

            if (end == -1)
                return null;

            var styleTxts = text.Substring(start + 2, end - (start + 2));

            int bgnIdx = end + 1;
            int endIdx = EscapedIndexOf(text, bgnIdx, '%');

            CSpan span;
            if (endIdx == -1)
            {
                endIdx = text.Length - 1;
                span = new CSpan(PrivateRunSpanGamut(text.Substring(bgnIdx)));
            }
            else
            {
                span = new CSpan(PrivateRunSpanGamut(text.Substring(bgnIdx, endIdx - bgnIdx)));
            }

            foreach (var styleTxt in styleTxts.Split(';'))
            {
                var nameAndVal = styleTxt.Split(':');

                if (nameAndVal.Length != 2)
                    return null;

                var name = nameAndVal[0].Trim();
                var colorLbl = nameAndVal[1].Trim();

                switch (name)
                {
                    case "color":
                        try
                        {
                            var color = colorLbl.StartsWith("#") ? (IBrush?)new BrushConverter().ConvertFrom(colorLbl) : (IBrush?)new BrushConverter().ConvertFromString(colorLbl);

                            span.Foreground = color;
                        }
                        catch { }
                        break;

                    case "background":
                        try
                        {
                            var color = colorLbl.StartsWith("#") ? (IBrush?)new BrushConverter().ConvertFrom(colorLbl) : (IBrush?)new BrushConverter().ConvertFromString(colorLbl);

                            span.Background = color;
                        }
                        catch { }
                        break;

                    default:
                        return null;
                }
            }

            start = endIdx;
            return span;
        }


        private int EscapedIndexOf(string text, int start, char target)
        {
            for (var i = start; i < text.Length; ++i)
            {
                var ch = text[i];
                if (ch == '\\') ++i;
                else if (ch == target) return i;
            }
            return -1;
        }
        private int CountRepeat(string text, int start, char target)
        {
            var count = 0;

            for (var i = start; i < text.Length; ++i)
            {
                if (text[i] == target) ++count;
                else break;
            }

            return count;
        }

        private CItalic ItalicEvaluator(Match match)
        {
            var content = match.Groups[3].Value;

            return new CItalic(PrivateRunSpanGamut(content));
        }

        private CBold BoldEvaluator(Match match)
        {
            var content = match.Groups[3].Value;

            return new CBold(PrivateRunSpanGamut(content));
        }

        private CStrikethrough StrikethroughEvaluator(Match match)
        {
            var content = match.Groups[2].Value;

            return new CStrikethrough(PrivateRunSpanGamut(content));
        }

        private CUnderline UnderlineEvaluator(Match match)
        {
            var content = match.Groups[2].Value;

            return new CUnderline(PrivateRunSpanGamut(content));
        }

        #endregion

        #region grammer - text

        private static readonly Regex _eoln = new("\\s+");
        private static readonly Regex _lbrk = new(@"\ {2,}\n");

        private IEnumerable<CRun> DoText(string text)
        {
            var lines = _lbrk.Split(text);
            bool first = true;
            foreach (var line in lines)
            {
                if (first)
                    first = false;
                else
                    yield return new CLineBreak();
                var t = _eoln.Replace(line, " ");
                yield return new CRun()
                {
                    Text = t
                };
            }
        }

        #endregion

        #region helper - make regex

        /// <summary>
        /// Reusable pattern to match balanced [brackets]. See Friedl's 
        /// "Mastering Regular Expressions", 2nd Ed., pp. 328-331.
        /// </summary>
        private static string GetNestedBracketsPattern()
        {
            // in other words [this] and [this[also]] and [this[also[too]]]
            // up to _nestDepth
            return RepeatString(@"
                   (?>              # Atomic matching
                      [^\[\]]+      # Anything other than brackets
                    |
                      \[
                          ", _nestDepth)
                + RepeatString(
                    @" \]
                   )*"
                    , _nestDepth);
        }

        /// <summary>
        /// Reusable pattern to match balanced (parens). See Friedl's 
        /// "Mastering Regular Expressions", 2nd Ed., pp. 328-331.
        /// </summary>
        private static string GetNestedParensPattern()
        {
            // in other words (this) and (this(also)) and (this(also(too)))
            // up to _nestDepth
            return RepeatString(@"
                   (?>              # Atomic matching
                      [^()\n\t]+? # Anything other than parens or whitespace
                    |
                      \(
                          ", _nestDepth)
                + RepeatString(
                    @" \)
                   )*?"
                    , _nestDepth);
        }

        /// <summary>
        /// this is to emulate what's evailable in PHP
        /// </summary>
        private static string RepeatString(string text, int count)
        {
            var sb = new StringBuilder(text.Length * count);
            for (int i = 0; i < count; i++)
                sb.Append(text);
            return sb.ToString();
        }

        #endregion


        #region helper - parse

        private TResult Create<TResult, TContent>(IEnumerable<TContent> content)
            where TResult : Panel, new()
            where TContent : Control
        {
            var result = new TResult();
            foreach (var c in content)
            {
                result.Children.Add(c);
            }

            return result;
        }


        //private IEnumerable<T> Evaluates<T>(
        //        string text, ParseStatus status,
        //        BlockParser<T>[] primary,
        //        BlockParser<T>[] secondly,
        //        Func<string, ParseStatus, IEnumerable<T>> rest
        //    )
        //{
        //    var index = 0;
        //    var length = text.Length;
        //    var rtn = new List<T>();
        //
        //    while (true)
        //    {
        //        int bestIndex = Int32.MaxValue;
        //        Match? bestMatch = null;
        //        BlockParser<T>? bestParser = null;
        //
        //        foreach (var parser in primary)
        //        {
        //            var match = parser.Pattern.Match(text, index, length);
        //            if (match.Success && match.Index < bestIndex)
        //            {
        //                bestIndex = match.Index;
        //                bestMatch = match;
        //                bestParser = parser;
        //            }
        //        }
        //
        //        if (bestParser is null || bestMatch is null) break;
        //
        //        var result = bestParser.Convert(text, bestMatch, status, this, out bestIndex, out int newIndex);
        //
        //        if (bestIndex > index)
        //        {
        //            EvaluateRest(rtn, text, index, bestIndex - index, status, secondly, 0, rest);
        //        }
        //
        //        rtn.AddRange(result);
        //
        //        length -= newIndex - index;
        //        index = newIndex;
        //    }
        //
        //    if (index < text.Length)
        //    {
        //        EvaluateRest(rtn, text, index, text.Length - index, status, secondly, 0, rest);
        //    }
        //
        //    return rtn;
        //
        //}
        //
        //private void EvaluateRest<T>(
        //    List<T> resultIn,
        //    string text, int index, int length,
        //    ParseStatus status,
        //    BlockParser<T>[] parsers, int parserStart,
        //    Func<string, ParseStatus, IEnumerable<T>> rest)
        //{
        //    for (; parserStart < parsers.Length; ++parserStart)
        //    {
        //        var parser = parsers[parserStart];
        //
        //        for (; ; )
        //        {
        //            var match = parser.Pattern.Match(text, index, length);
        //            if (!match.Success) break;
        //
        //            var result = parser.Convert(text, match, status, this, out var matchStartIndex, out int newIndex);
        //
        //            if (matchStartIndex > index)
        //            {
        //                EvaluateRest(resultIn, text, index, match.Index - index, status, parsers, parserStart + 1, rest);
        //            }
        //
        //            resultIn.AddRange(result);
        //
        //            length -= newIndex - index;
        //            index = newIndex;
        //        }
        //
        //        if (length == 0) break;
        //    }
        //
        //    if (length != 0)
        //    {
        //        var suffix = text.Substring(index, length);
        //        resultIn.AddRange(rest(suffix, status));
        //    }
        //}

        #endregion
    }

    internal struct Candidate<T> : IComparable<Candidate<T>>
    {
        public Match Match { get; }
        public T Parser { get; }

        public Candidate(Match result, T parser)
        {
            Match = result;
            Parser = parser;
        }

        public int CompareTo(Candidate<T> other)
        {
            var indexComparison = Match.Index.CompareTo(other.Match.Index);
            return indexComparison != 0
                ? indexComparison
                : other.Match.Length.CompareTo(Match.Length);
        }
    }

    internal class UnclosableStream : Stream
    {
        private Stream _stream;

        public UnclosableStream(Stream stream)
        {
            _stream = stream;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;

        public override long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        public override void Flush() { }
        public override void Close() { }

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);

        public override void SetLength(long value) => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
