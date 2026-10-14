using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Markdown.Avalonia.Parsing;

/// <summary>
/// CommonMark/GFM front-end responsible for tokenization and complete block boundaries.
/// Rendering remains in the Avalonia document layer.
/// </summary>
internal static class StandardMarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static MarkdownDocument Parse(string markdown)
        => Markdig.Markdown.Parse(markdown, Pipeline);

    public static IEnumerable<ParsedBlock> ParseBlocks(string markdown)
    {
        var index = CreateIndex(markdown, CancellationToken.None);
        foreach (var block in index.Blocks)
            yield return block.Materialize(index.Source);
    }

    public static ParsedDocumentIndex CreateIndex(
        string markdown,
        CancellationToken cancellationToken)
    {
        var document = Parse(markdown);
        var containerRanges = FindDetailsContainerRanges(markdown, document);
        var blocks = new List<IndexedBlock>(document.Count);
        var rangeIndex = 0;
        var emittedRange = -1;

        foreach (var block in document)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (block is LinkReferenceDefinitionGroup)
                continue;

            var span = block.Span;
            if (span.Start < 0 || span.End < span.Start || span.Start >= markdown.Length)
                continue;

            while (rangeIndex < containerRanges.Count && containerRanges[rangeIndex].End < span.Start)
                rangeIndex++;

            if (rangeIndex < containerRanges.Count &&
                span.Start >= containerRanges[rangeIndex].Start &&
                span.Start <= containerRanges[rangeIndex].End)
            {
                if (emittedRange != rangeIndex)
                {
                    var range = containerRanges[rangeIndex];
                    blocks.Add(new IndexedBlock(
                        block,
                        range.Start,
                        range.End - range.Start + 1,
                        true));
                    emittedRange = rangeIndex;
                }
                continue;
            }

            var end = Math.Min(span.End, markdown.Length - 1);
            blocks.Add(new IndexedBlock(
                block,
                span.Start,
                end - span.Start + 1,
                false));
        }

        return new ParsedDocumentIndex(markdown, document, blocks);
    }

    private static readonly Regex DetailsTagPattern = new(
        @"<\s*(?<close>/)?\s*details\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<SourceSpan> FindDetailsContainerRanges(
        string markdown,
        MarkdownDocument document)
    {
        var excluded = document.Descendants<CodeBlock>()
            .Select(block => block.Span)
            .Concat(document.Descendants<CodeInline>().Select(inline => inline.Span))
            .Where(span => span.Start >= 0 && span.End >= span.Start)
            .OrderBy(span => span.Start)
            .ToArray();

        var ranges = new List<SourceSpan>();
        var starts = new Stack<int>();
        var excludedIndex = 0;
        foreach (Match match in DetailsTagPattern.Matches(markdown))
        {
            while (excludedIndex < excluded.Length && excluded[excludedIndex].End < match.Index)
                excludedIndex++;

            if (excludedIndex < excluded.Length &&
                match.Index >= excluded[excludedIndex].Start &&
                match.Index <= excluded[excludedIndex].End)
                continue;

            if (!match.Groups["close"].Success)
            {
                if (!match.Value.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                    starts.Push(match.Index);
                continue;
            }

            if (starts.Count == 0)
                continue;

            var start = starts.Pop();
            if (starts.Count == 0)
                ranges.Add(new SourceSpan(start, match.Index + match.Length - 1));
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        return ranges;
    }

    public static ContainerInline? ParseInline(string markdown)
    {
        var document = Parse(markdown);
        return document.Descendants<LeafBlock>()
            .Select(block => block.Inline)
            .FirstOrDefault(inline => inline is not null);
    }

    internal readonly record struct ParsedBlock(
        Block Node,
        string Source,
        string DocumentSource,
        int SourceStart,
        bool IsRawHtmlContainer);

    internal readonly record struct IndexedBlock(
        Block Node,
        int SourceStart,
        int SourceLength,
        bool IsRawHtmlContainer)
    {
        public ParsedBlock Materialize(string documentSource)
            => new(
                Node,
                documentSource.Substring(SourceStart, SourceLength),
                documentSource,
                SourceStart,
                IsRawHtmlContainer);
    }

    internal sealed class ParsedDocumentIndex
    {
        public ParsedDocumentIndex(
            string source,
            MarkdownDocument document,
            IReadOnlyList<IndexedBlock> blocks)
        {
            Source = source;
            Document = document;
            Blocks = blocks;
        }

        public string Source { get; }
        public MarkdownDocument Document { get; }
        public IReadOnlyList<IndexedBlock> Blocks { get; }
    }
}
