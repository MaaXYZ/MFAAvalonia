using ColorTextBlock.Avalonia;
using Markdown.Avalonia.Html;
using Markdown.Avalonia.Html.Core;
using Markdown.Avalonia.Html.Core.Parsers;
using Markdown.Avalonia.Html.Core.Utils;
using Markdown.Avalonia.Plugins;
using Markdown.Avalonia.SyntaxHigh;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Parsing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Markdown.Avalonia.Html.Tests;

public class HtmlCompatibilityTests
{
    [Fact]
    public void MarkdownScrollViewer_ForwardsScrollbarVisibilityProperties()
    {
        var viewer = new global::Markdown.Avalonia.MarkdownScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden
        };

        Assert.Equal(
            global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            viewer.ScrollViewer.HorizontalScrollBarVisibility);
        Assert.Equal(
            global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            viewer.ScrollViewer.VerticalScrollBarVisibility);
    }

    [Fact]
    public void DecodeEntities_PreservesNonBreakingSpacesAndEscapedSyntax()
    {
        var run = new CRun
        {
            Text = "A&nbsp;B&#160;C&#xA0;D&lt;x&gt;&#42;&#x2A;"
        };

        HtmlTextNormalizer.DecodeEntities([run]);

        Assert.Equal("A\u00A0B\u00A0C\u00A0D<x>**", run.Text);
    }

    [Fact]
    public void NormalizeSourceWhitespace_DoesNotDecodeEntitiesBeforeMarkdownParsing()
    {
        var result = HtmlTextNormalizer.NormalizeSourceWhitespace(
            "\r\nA&nbsp;B\r\n&lt;x&gt;\n&ast;&ast;");

        Assert.Equal("A&nbsp;B &lt;x&gt; &ast;&ast;", result);
    }

    [Theory]
    [InlineData("<small>content</small>")]
    [InlineData("<custom-element>content</custom-element>")]
    [InlineData("<section class=\"note\">content</section>")]
    public void InlineEntryPattern_RecognizesUnknownHtmlTags(string html)
    {
        Assert.Matches(SimpleHtmlUtils.CreateAnyTagStartPattern(), html);
    }

    [Fact]
    public void ReplaceManager_BypassesUnknownTagsByDefault()
    {
        var manager = new ReplaceManager(new SyntaxHighlight(), new SetupInfo());

        Assert.Equal(UnknownTagsOption.Bypass, manager.UnknownTags);
    }

    [Fact]
    public void MarkdownInline_DecodesEntitiesAfterParsing()
    {
        var engine = new global::Markdown.Avalonia.Markdown();

        var text = string.Concat(engine.ParseGamutInline(
            "&copy; &lt;x&gt; &nbsp; &#188;").Select(x => x.AsString()));

        Assert.Equal("© <x> \u00A0 ¼", text);
    }

    [Fact]
    public void MarkdownInline_DecodesNestedEntityExactlyOnce()
    {
        var engine = new global::Markdown.Avalonia.Markdown();

        var text = string.Concat(engine.ParseGamutInline("&amp;lt;").Select(x => x.AsString()));

        Assert.Equal("&lt;", text);
    }

    [Theory]
    [InlineData("Heading 1 link [Heading link](https://example.com)", "Heading 1 link Heading link")]
    [InlineData("The <abbr>HTML</abbr> specification", "The HTML specification")]
    public void FullMarkdown_PreservesSpacesAroundInlineElements(string markdown, string expected)
    {
        var engine = CreateFullEngine();

        var text = string.Concat(engine.ParseGamutInline(markdown).Select(x => x.AsString()));

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("abbr")]
    [InlineData("acronym")]
    public void HtmlAbbreviation_UsesDottedUnderlineAndDecodedTitle(string tag)
    {
        var engine = CreateFullEngine();
        var markdown = $"The <{tag} title=\"World Wide Web &amp; Consortium\">W3C</{tag}>.";

        var span = Assert.Single(
            DescendantSpans(engine.ParseGamutInline(markdown))
                .OfType<CSpan>()
                .Where(candidate => candidate.AsString() == "W3C"));

        Assert.True(span.IsUnderline);
        Assert.Equal(CTextUnderlineStyle.Dotted, span.UnderlineStyle);
        Assert.Equal("World Wide Web & Consortium", span.ToolTipText);
    }

    [Fact]
    public void HtmlAbbreviation_WithoutTitleDoesNotCreateEmptyToolTip()
    {
        var engine = CreateFullEngine();

        var span = Assert.Single(
            DescendantSpans(engine.ParseGamutInline("<abbr>HTML</abbr>"))
                .OfType<CSpan>()
                .Where(candidate => candidate.AsString() == "HTML"));

        Assert.True(span.IsUnderline);
        Assert.Equal(CTextUnderlineStyle.Dotted, span.UnderlineStyle);
        Assert.Null(span.ToolTipText);
    }

    [Fact]
    public void ViewerPipeline_RendersMixedMarkdownAndHtmlDecorationsIndependently()
    {
        var engine = CreateFullEngine();
        const string markdown = "~~deleted~~ <s>html deleted</s> *italic* **bold** X<sub>2</sub> O<sup>2</sup>";

        var inlines = engine.ParseGamutInline(markdown).ToArray();

        Assert.Equal(2, inlines.OfType<CStrikethrough>().Count()
            + inlines.OfType<CSpan>().Count(span => span.IsStrikethrough && span is not CStrikethrough));
        Assert.Single(inlines.OfType<CItalic>());
        Assert.Single(inlines.OfType<CBold>());
        Assert.Contains(inlines.OfType<CSpan>(), span => span.TextVerticalAlignment == TextVerticalAlignment.Bottom);
        Assert.Contains(inlines.OfType<CSpan>(), span => span.TextVerticalAlignment == TextVerticalAlignment.Top);
    }

    [Fact]
    public void ViewerPipeline_RendersBothBoldItalicDelimiterForms()
    {
        var engine = CreateFullEngine();

        var inlines = engine.ParseGamutInline("***bold italic*** ___bold italic___").ToArray();

        var spans = DescendantSpans(inlines).ToArray();

        Assert.Equal("bold italic bold italic", string.Concat(inlines.Select(inline => inline.AsString())));
        Assert.Equal(2, spans.OfType<CBold>().Count());
        Assert.Equal(2, spans.OfType<CItalic>().Count());
    }

    [Theory]
    [InlineData("Blockquotes :star:", "Blockquotes ⭐")]
    [InlineData("Emoji :smiley: and :fa-star:", "Emoji 😃 and :fa-star:")]
    public void StandardInlinePipeline_ReplacesKnownEmojiShortcodesOnly(string markdown, string expected)
    {
        var engine = CreateFullEngine();

        var inlines = engine.ParseGamutInline(markdown).ToArray();

        Assert.Equal(expected, string.Concat(inlines.Select(inline => inline.AsString())));
    }

    [Fact]
    public void ViewerPipeline_RendersBoldItalicInsideParagraphContainingHtml()
    {
        var engine = CreateFullEngine();
        const string markdown = "~~deleted~~ <s>html deleted</s>\n*italic* _italic_\n**bold** __bold__\n***bold italic*** ___bold italic___";

        var root = engine.TransformElement(markdown);
        var textBlock = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(Assert.Single(root.Children).Control);
        var spans = DescendantSpans(textBlock.Content).ToArray();

        Assert.Equal(2, spans.OfType<CBold>().Count(bold => DescendantSpans(bold.Content).OfType<CItalic>().Any()));
        Assert.DoesNotContain("***", textBlock.Text);
        Assert.DoesNotContain("___", textBlock.Text);
    }

    [Fact]
    public void ViewerPipeline_ConsumesGfmTaskMarkersIncludingNestedLists()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "- [x] GFM task list 1\n" +
            "- [x] GFM task list 2\n" +
            "- [ ] GFM task list 3\n" +
            "    - [ ] GFM task list 3-1\n" +
            "    - [ ] GFM task list 3-2\n" +
            "    - [ ] GFM task list 3-3\n" +
            "- [ ] GFM task list 4\n" +
            "    - [ ] GFM task list 4-1\n" +
            "    - [ ] GFM task list 4-2\n";

        var root = engine.TransformElement(markdown);
        var list = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(Assert.Single(root.Children));
        var items = list.Children.Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>().ToArray();

        Assert.Equal(new bool?[] { true, true, false, false }, items.Select(item => item.TaskChecked));
        var markers = ReadTaskListMarkers(list);
        Assert.Equal(new bool?[] { true, true, false, false }, markers.Select(marker => marker.IsChecked));
        Assert.All(markers, marker =>
        {
            Assert.Equal(14, marker.Width);
            Assert.Equal(14, marker.Height);
            Assert.False(marker.IsEnabled);
            Assert.NotEqual(global::Avalonia.Layout.VerticalAlignment.Center, marker.VerticalAlignment);
            Assert.False(marker.IsHitTestVisible);
            Assert.Null(marker.RenderTransform);
        });

        var nested3 = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(
            Assert.Single(items[2].Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()));
        var nested3Items = nested3.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>()
            .ToArray();
        Assert.Equal(3, nested3Items.Length);
        Assert.All(nested3Items, item =>
            Assert.Empty(item.Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()));

        var nested4 = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(
            Assert.Single(items[3].Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()));
        var nested4Items = nested4.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>()
            .ToArray();
        Assert.Equal(2, nested4Items.Length);
        Assert.All(nested4Items, item =>
            Assert.Empty(item.Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()));
    }

    [Fact]
    public void ViewerPipeline_ParsesRightParenthesisOrderedMarkerAndPreservesStartNumber()
    {
        var engine = CreateFullEngine();

        var root = engine.TransformElement("2) ordered with right parenthesis\n");
        var list = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(
            Assert.Single(root.Children));

        Assert.Equal("2.", Assert.Single(ReadListMarkerTexts(list)));
    }

    [Fact]
    public void ViewerPipeline_UsesNestingDepthInsteadOfSourceBulletForListMarkers()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "- outer\n" +
            "    1. first\n" +
            "    2. second\n" +
            "- outer two\n" +
            "    - dash child\n" +
            "    * star child\n";

        var root = engine.TransformElement(markdown);
        var outer = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(
            Assert.Single(root.Children));

        Assert.Equal(new[] { "•", "•" }, ReadListMarkerTexts(outer));

        var items = outer.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>()
            .ToArray();
        var ordered = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(
            Assert.Single(items[0].Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()));
        var unordered = items[1].Children
            .OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>()
            .ToArray();

        Assert.Equal(new[] { "i.", "ii." }, ReadListMarkerTexts(ordered));
        Assert.Equal(new[] { "○", "○" }, unordered.SelectMany(ReadListMarkerTexts));
    }

    [Fact]
    public void ViewerPipeline_ResolvesReferenceStyleImages()
    {
        var engine = CreateFullEngine();
        engine.UseResource = true;
        engine.CascadeResources.Owner["image.png"] = new global::Avalonia.Controls.Border();
        const string markdown =
            "![reference image][image-id]\n\n" +
            "[image-id]: image.png \"image title\"\n";

        var root = engine.TransformElement(markdown);
        var textBlock = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(
            Assert.Single(root.Children).Control);

        Assert.Single(textBlock.Content.OfType<CInlineUIContainer>());
        Assert.DoesNotContain("![reference image]", textBlock.Text);
        Assert.DoesNotContain("[image-id]:", textBlock.Text);
    }

    [Fact]
    public void HtmlImage_PercentageWidthUsesInlineRelativeLayoutWithoutAncestorBinding()
    {
        var image = new CImage(new global::Avalonia.Media.DrawingImage());

        global::Markdown.Avalonia.Html.Core.Parsers.ImageParser.ApplyDimensions(
            image,
            widthText: "30%",
            heightText: null);

        Assert.Equal(0.3d, image.RelativeWidth);
        Assert.Null(image.LayoutWidth);
        Assert.Null(image.LayoutHeight);
    }

    [Fact]
    public void ViewerPipeline_PreservesPlainFencedCodeContent()
    {
        var engine = CreateFullEngine();

        var root = engine.TransformElement("```\n# fenced code content\n```\n");
        var text = string.Concat(root.Children.Select(element => ReadControlText(element.Control)));

        Assert.Contains("# fenced code content", text);
    }

    [Fact]
    public void ViewerPipeline_PreservesHtmlLookingTextInsidePlainFence()
    {
        var engine = CreateFullEngine();
        const string video = "<video src=\"xxx.mp4\" />  # literal HTML example";

        var root = engine.TransformElement($"```\n{video}\n```\n");
        var block = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.PlainCodeBlockElement>(
            Assert.Single(root.Children));
        var text = DescendantControls([block.Control])
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Single();

        Assert.Equal(video, text.Text);
        var scroll = Assert.IsType<global::Avalonia.Controls.ScrollViewer>(
            Assert.IsType<global::Avalonia.Controls.Border>(block.Control).Child);
        Assert.Equal(new global::Avalonia.Thickness(10, 8), scroll.Padding);
        Assert.Equal(36d, scroll.MinHeight);
        Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            scroll.VerticalScrollBarVisibility);
        Assert.Equal(20d, text.MinHeight);
    }

    [Fact]
    public void ProgressiveAppend_DoesNotMaterializeAHiddenRootPanelOrDuplicateElements()
    {
        var engine = CreateFullEngine();
        var allElements = engine.TransformElement("one\n\ntwo\n\nthree\n\nfour\n").Children.ToArray();
        var root = new global::ColorDocument.Avalonia.DocumentElements.DocumentRootElement(allElements.Take(2));
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(root.Children);

        var remaining = allElements.Skip(2).ToArray();
        root.AppendElements(remaining);
        panel.AppendElements(remaining);

        var lazyField = typeof(global::ColorDocument.Avalonia.DocumentElements.DocumentRootElement)
            .GetField("_block", BindingFlags.Instance | BindingFlags.NonPublic);
        var lazyPanel = Assert.IsType<Lazy<global::Avalonia.Controls.StackPanel>>(lazyField!.GetValue(root));

        Assert.False(lazyPanel.IsValueCreated);
        Assert.Equal(allElements.Length, root.Children.Count());
        Assert.Equal(allElements.Length, panel.ElementCount);
        Assert.Equal(allElements, panel.AllElements);
    }

    [Fact]
    public void VirtualizingPanel_ScrollsToUnrealizedDocumentElementByIndex()
    {
        var engine = CreateFullEngine();
        var elements = engine.TransformElement("one\n\ntwo\n\nthree\n").Children.ToArray();
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(elements);

        Assert.True(panel.ScrollToElement(elements[2]));
        Assert.Equal(100d, panel.Offset.Y);
    }

    [Fact]
    public void ProgressiveRanges_CoverEveryElementExactlyOnce()
    {
        var ranges = global::Markdown.Avalonia.MarkdownScrollViewer.CreateProgressiveElementRanges(
            totalElements: 137,
            totalLines: 1000,
            initialLines: 300,
            batchLines: 200);

        var nextStart = 0;
        var previousRenderedLines = 0;
        foreach (var range in ranges)
        {
            Assert.Equal(nextStart, range.Start);
            Assert.True(range.Count > 0);
            Assert.True(range.RenderedLines >= previousRenderedLines);
            nextStart += range.Count;
            previousRenderedLines = range.RenderedLines;
        }

        Assert.Equal(137, nextStart);
        Assert.Equal(1000, ranges[^1].RenderedLines);
    }

    [Fact]
    public void LargeDocumentPipeline_IndexesOneHundredThousandLinesWithoutRealizingBlocks()
    {
        var source = new System.Text.StringBuilder(1_500_000);
        for (var line = 0; line < 100_000; line++)
        {
            source.Append("line ").Append(line).Append('\n');
            if (line % 100 == 99)
                source.Append('\n');
        }

        var markdown = source.ToString();
        var engine = CreateFullEngine();
        var options = engine.PrepareDeferredParsing();
        var plan = engine.BuildDeferredDocument(
            markdown,
            options,
            System.Threading.CancellationToken.None);
        engine.ActivateDeferredDocument(plan);

        var blocks = plan.Document.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.DeferredBlockElement>()
            .ToArray();

        Assert.Equal(101_001, global::Markdown.Avalonia.MarkdownScrollViewer.CountLines(markdown));
        Assert.Equal(1000, blocks.Length);
        Assert.All(blocks, block => Assert.False(block.IsRealized));

        var firstControl = blocks[0].Control;
        Assert.True(blocks[0].IsRealized);
        Assert.All(blocks.Skip(1), block => Assert.False(block.IsRealized));

        blocks[0].ReleaseControl();
        Assert.False(blocks[0].IsRealized);
        Assert.NotSame(firstControl, blocks[0].Control);
    }

    [Fact]
    public void DeferredLargeDocument_PreservesHeadingMetadataWithoutRealizingHeadingControl()
    {
        var engine = CreateFullEngine();
        var options = engine.PrepareDeferredParsing();
        var plan = engine.BuildDeferredDocument(
            "# **Deferred** heading\n\nbody\n",
            options,
            System.Threading.CancellationToken.None);

        var heading = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.DeferredHeadingElement>(
            plan.Document.Children.First());
        var metadata = Assert.IsAssignableFrom<global::ColorDocument.Avalonia.IDocumentHeading>(heading);

        Assert.Equal(1, metadata.Level);
        Assert.Equal("Deferred heading", metadata.Text);
        Assert.False(heading.IsRealized);
    }

    [Fact]
    public void VeryLargeHighlightedCodeBlock_DisablesFullDocumentHighlighting()
    {
        var code = string.Join('\n', Enumerable.Repeat("const value = 1;", 2001));
        var engine = CreateFullEngine();

        var block = Assert.Single(engine.TransformElement($"```js\n{code}\n```\n").Children);
        Assert.Equal("CodeBlockElement", block.GetType().Name);
        Assert.True(Assert.IsType<bool>(block.GetType()
            .GetField("_isLargeCodeBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(block)));
    }

    [Fact]
    public void VeryLargeSingleParagraph_UsesBoundedRawFallback()
    {
        var markdown = string.Join('\n', Enumerable.Repeat("plain text", 5001));
        var engine = CreateFullEngine();
        var options = engine.PrepareDeferredParsing();
        var plan = engine.BuildDeferredDocument(
            markdown,
            options,
            System.Threading.CancellationToken.None);

        var deferred = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.DeferredBlockElement>(
            Assert.Single(plan.Document.Children));
        _ = deferred.Control;

        Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.LargeRawTextElement>(
            Assert.Single(deferred.Children));
    }

    [Fact]
    public void VirtualizingPanel_ReleasesDeferredBlocksAndDoesNotProbeEveryControl()
    {
        var realizationCount = 0;
        var blocks = Enumerable.Range(0, 100)
            .Select(index => new global::ColorDocument.Avalonia.DocumentElements.DeferredBlockElement(
                () =>
                {
                    realizationCount++;
                    return
                    [
                        new global::ColorDocument.Avalonia.DocumentElements.UnBlockElement(
                            new global::Avalonia.Controls.Border(),
                            index.ToString())
                    ];
                },
                index.ToString()))
            .ToArray();
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(blocks);

        var panelType = typeof(global::Markdown.Avalonia.VirtualizingMarkdownPanel);
        var realized = Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(panelType
            .GetMethod("RealizeElement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [50]));

        Assert.True(panel.BringIntoView(realized, default));
        Assert.Equal(1, realizationCount);

        panelType.GetMethod("VirtualizeElement", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [50]);
        Assert.False(blocks[50].IsRealized);
        Assert.Equal(1, realizationCount);
    }

    [Fact]
    public void VirtualizationHeightIndex_UsesUpdatedPrefixSumsWithoutFullOffsetRecalculation()
    {
        var elements = Enumerable.Range(0, 4)
            .Select(index => new global::ColorDocument.Avalonia.DocumentElements.UnBlockElement(
                new global::Avalonia.Controls.Border(),
                index.ToString()))
            .ToArray();
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(elements);

        var panelType = typeof(global::Markdown.Avalonia.VirtualizingMarkdownPanel);
        panelType.GetMethod("SetElementHeight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [0, 125d]);

        Assert.True(panel.ScrollToElement(elements[2]));
        Assert.Equal(175d, panel.Offset.Y);
    }

    [Fact]
    public void VirtualizationHeightCorrection_PreservesTheVisibleElementAnchor()
    {
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(Enumerable.Range(0, 5)
            .Select(_ => new global::ColorDocument.Avalonia.DocumentElements.UnBlockElement(
                new global::Avalonia.Controls.Border())));

        var panelType = typeof(global::Markdown.Avalonia.VirtualizingMarkdownPanel);
        var heights = Assert.IsType<List<double>>(panelType
            .GetField("_elementHeights", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel));
        heights.Clear();
        heights.AddRange([100d, 100d, 100d, 100d, 100d]);

        panelType.GetMethod("RecalculateOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        panelType.GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, new global::Avalonia.Size(200, 100));
        panelType.GetMethod("UpdateExtent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        panel.Offset = new global::Avalonia.Vector(0, 250);

        var anchor = panelType.GetMethod("CaptureScrollAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        heights[0] = 200;
        panelType.GetMethod("RecalculateOffsets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        panelType.GetMethod("UpdateExtent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        panelType.GetMethod("RestoreScrollAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [anchor]);

        Assert.Equal(350, panel.Offset.Y);
    }

    [Fact]
    public void VirtualizationEstimator_UpdatesEveryUnmeasuredElement()
    {
        var panel = new global::Markdown.Avalonia.VirtualizingMarkdownPanel();
        panel.SetElements(Enumerable.Range(0, 4)
            .Select(_ => new global::ColorDocument.Avalonia.DocumentElements.UnBlockElement(
                new global::Avalonia.Controls.Border())));

        var panelType = typeof(global::Markdown.Avalonia.VirtualizingMarkdownPanel);
        var measured = Assert.IsType<HashSet<int>>(panelType
            .GetField("_measuredElements", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel));
        measured.Add(0);
        panelType.GetMethod("UpdateEstimatedHeight", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [100d]);

        var heights = Assert.IsType<List<double>>(panelType
            .GetField("_elementHeights", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel));
        Assert.Equal(50d, heights[0]);
        Assert.Equal([100d, 100d, 100d], heights.Skip(1));
    }

    [Fact]
    public void ViewerPipeline_PreservesAllLinesOfIndentedPhpCode()
    {
        var engine = CreateFullEngine();
        const string markdown = "Paragraph before code.\n\n    <?php\n        echo \"Hello world!\";\n    ?>\n";

        var root = engine.TransformElement(markdown);
        var codeElement = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.UnBlockElement>(root.Children.Last());
        var border = Assert.IsType<global::Avalonia.Controls.Border>(codeElement.Control);
        var scroll = Assert.IsType<global::Avalonia.Controls.ScrollViewer>(border.Child);
        var text = Assert.IsType<global::Avalonia.Controls.TextBlock>(scroll.Content);

        Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, scroll.VerticalScrollBarVisibility);
        Assert.Equal(new global::Avalonia.Thickness(10, 8), scroll.Padding);
        Assert.Equal(76d, scroll.MinHeight);
        Assert.Equal(60d, text.MinHeight);
        Assert.Equal("<?php\n    echo \"Hello world!\";\n?>", text.Text);
    }

    [Fact]
    public void ViewerPipeline_PreservesAllLinesOfIndentedPreformattedTable()
    {
        var engine = CreateFullEngine();
        const string markdown = "    | First Header  | Second Header |\n    | ------------- | ------------- |\n    | Content Cell  | Content Cell  |\n    | Content Cell  | Content Cell  |\n";

        var root = engine.TransformElement(markdown);
        var border = Assert.IsType<global::Avalonia.Controls.Border>(Assert.Single(root.Children).Control);
        var scroll = Assert.IsType<global::Avalonia.Controls.ScrollViewer>(border.Child);
        var text = Assert.IsType<global::Avalonia.Controls.TextBlock>(scroll.Content);

        Assert.Equal(
            "| First Header  | Second Header |\n| ------------- | ------------- |\n| Content Cell  | Content Cell  |\n| Content Cell  | Content Cell  |",
            text.Text);
    }

    [Fact]
    public void PipeTableTokenizer_TreatsEscapedPipeAsCellContent()
    {
        var cells = global::Markdown.Avalonia.Parsers.Builtin.TableParser.SplitRowCells(
            "| git diff | ` | git diff \\| |");

        Assert.Equal(3, cells.Count);
        Assert.Equal("git diff |", cells[2].Trim());
    }

    [Fact]
    public void ViewerPipeline_DoesNotCreateExtraColumnForEscapedPipe()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "| Left-aligned | Center-aligned | Right-aligned |\n" +
            "| :--- | :---: | ---: |\n" +
            "| git status | git status | git status |\n" +
            "| git diff | ` | git diff \\| |\n";

        var table = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.TableBlockElement>(
            Assert.Single(engine.TransformElement(markdown).Children));
        var cells = table.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.TableCellElement>()
            .ToArray();

        Assert.Equal(9, cells.Length);
        Assert.Equal(
            new[] { "git diff", "`", "git diff |" },
            cells.Skip(6).Select(cell => ReadControlText(cell.Control).Trim()));

        var border = Assert.IsType<global::Avalonia.Controls.Border>(table.Control);
        var grid = Assert.IsType<global::Avalonia.Controls.Grid>(border.Child);
        Assert.Equal(3, grid.ColumnDefinitions.Count);
    }

    [Fact]
    public void LegacyBlockPipeline_PreservesAllLinesOfIndentedAnnouncementBlocks()
    {
        var engine = CreateFullEngine();
        const string markdown = "    <?php\n        echo \"Hello world!\";\n    ?>\n\n    | First Header  | Second Header |\n    | ------------- | ------------- |\n    | Content Cell  | Content Cell  |\n    | Content Cell  | Content Cell  |\n";

        var controls = engine.RunBlockGamut(markdown, ParseStatus.Init).ToArray();
        var texts = controls
            .SelectMany(control => DescendantControls([control]))
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Select(text => text.Text)
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains(texts, text => text!.Contains("echo \"Hello world!\";"));
        Assert.Contains(texts, text => text!.Contains("| Content Cell  | Content Cell  |"));
    }

    [Fact]
    public void ViewerPipeline_PreservesAdjacentIndentedBlocksFromAnnouncementExcerpt()
    {
        var engine = CreateFullEngine();
        const string markdown = "缩进风格。\n\n    <?php\n        echo \"Hello world!\";\n    ?>\n    \n预格式化文本：\n\n    | First Header  | Second Header |\n    | ------------- | ------------- |\n    | Content Cell  | Content Cell  |\n    | Content Cell  | Content Cell  |\n";

        var root = engine.TransformElement(markdown);
        var codeTexts = root.Children
            .OfType<global::ColorDocument.Avalonia.DocumentElements.UnBlockElement>()
            .Select(element => DescendantControls([element.Control])
                .OfType<global::Avalonia.Controls.TextBlock>()
                .Single().Text)
            .ToArray();

        Assert.Equal(2, codeTexts.Length);
        Assert.Equal("<?php\n    echo \"Hello world!\";\n?>", codeTexts[0]);
        Assert.Equal("| First Header  | Second Header |\n| ------------- | ------------- |\n| Content Cell  | Content Cell  |\n| Content Cell  | Content Cell  |", codeTexts[1]);
    }

    [Fact]
    public void ViewerPipeline_PreservesIndentedBlocksDeepInLongDocument()
    {
        var engine = CreateFullEngine();
        var prefix = string.Join("\n\n", Enumerable.Range(1, 60).Select(i => $"paragraph {i}"));
        var markdown = prefix + "\n\n缩进风格。\n\n    <?php\n        echo \"Hello world!\";\n    ?>\n    \n预格式化文本：\n\n    | First Header  | Second Header |\n    | ------------- | ------------- |\n    | Content Cell  | Content Cell  |\n    | Content Cell  | Content Cell  |\n";

        var codeTexts = engine.TransformElement(markdown).Children
            .OfType<global::ColorDocument.Avalonia.DocumentElements.UnBlockElement>()
            .Select(element => DescendantControls([element.Control])
                .OfType<global::Avalonia.Controls.TextBlock>()
                .Single().Text)
            .ToArray();

        Assert.Equal(2, codeTexts.Length);
        Assert.Contains("echo \"Hello world!\";", codeTexts[0]);
        Assert.Contains("| Content Cell  | Content Cell  |", codeTexts[1]);
    }

    [Fact]
    public void IncrementalRendering_DoesNotReuseDifferentCodeBlocks()
    {
        var engine = CreateFullEngine();
        var php = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.UnBlockElement>(
            Assert.Single(engine.TransformElement("    <?php\n    ?>\n").Children));
        var table = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.UnBlockElement>(
            Assert.Single(engine.TransformElement("    | header |\n    | body   |\n").Children));

        Assert.NotEqual(php.ContentHash, table.ContentHash);
        Assert.False(php.CanReuseWith(table));
    }

    [Theory]
    [InlineData("<https://github.com>")]
    [InlineData("https://baidu.com")]
    [InlineData("test.test@gmail.com")]
    public void FullMarkdown_CreatesAutomaticLinks(string markdown)
    {
        var engine = CreateFullEngine();

        var result = engine.ParseGamutInline(markdown).ToArray();

        Assert.Contains(result, inline => inline is CHyperlink);
    }

    [Fact]
    public void ReferenceLinks_AreResolvedAndDefinitionsRemoved()
    {
        const string markdown = "[anchor][anchor-id]\n\n[anchor-id]: https://example.com/";

        var result = global::Markdown.Avalonia.Markdown.ResolveReferenceLinks(markdown);

        Assert.Contains("[anchor](https://example.com/)", result);
        Assert.DoesNotContain("[anchor-id]:", result);
    }

    [Fact]
    public void ReferenceLinkPreprocessor_DoesNotConsumeFootnoteDefinitions()
    {
        const string markdown =
            "text[^note]\n\n" +
            "[^note]: single-word\n" +
            "[anchor-id]: https://example.com/\n";

        var result = global::Markdown.Avalonia.Markdown.ResolveReferenceLinks(markdown);

        Assert.Contains("[^note]: single-word", result);
        Assert.DoesNotContain("[anchor-id]:", result);
    }

    [Fact]
    public void ViewerPipeline_RendersFootnoteReferencesAndDefinitionsFromAst()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "first[^alpha], second[^beta], first again[^alpha].\n\n" +
            "[^alpha]: Alpha **note**.\n" +
            "[^beta]: Beta note.\n" +
            "  continuation.\n";

        var root = engine.TransformElement(markdown);
        var topLevel = root.Children.ToArray();

        // One body paragraph followed by the standard footnote rule and list.
        // In particular, the FootnoteGroup must not fall back to its source span,
        // because Markdig's group span may cover the whole document.
        Assert.Equal(3, topLevel.Length);
        Assert.IsType<global::Markdown.Avalonia.Controls.Rule>(topLevel[1].Control);
        var list = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(topLevel[2]);

        var body = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(topLevel[0].Control);
        var references = DescendantSpans(body.Content)
            .OfType<CHyperlink>()
            .Where(link => link.Classes.Contains(global::ColorDocument.Avalonia.ClassNames.FootnoteReferenceClass))
            .ToArray();

        Assert.Equal(new[] { "[1]", "[2]", "[1]" }, references.Select(link => link.AsString()));
        Assert.All(references, reference =>
        {
            Assert.False(reference.IsUnderline);
            Assert.Equal(TextVerticalAlignment.Top, reference.TextVerticalAlignment);
        });

        var items = list.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>()
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(new[] { "1.", "2." }, ReadListMarkerTexts(list));

        var alphaText = string.Concat(items[0].Children.Select(element => ReadControlText(element.Control)));
        var betaText = string.Concat(items[1].Children.Select(element => ReadControlText(element.Control)));
        Assert.Equal("Alpha note.", alphaText);
        Assert.Equal("Beta note. continuation.", betaText);
        Assert.Contains(
            DescendantControls(items[0].Children.Select(element => element.Control))
                .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
                .SelectMany(text => DescendantSpans(text.Content)),
            inline => inline is CBold);

        var allText = string.Concat(topLevel.Select(element => ReadControlText(element.Control)));
        Assert.DoesNotContain("[^alpha]", allText);
        Assert.DoesNotContain("[^beta]", allText);
    }

    [Fact]
    public void ViewerPipeline_UsesGlobalFootnoteOrderInsteadOfNumericLabels()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "earlier[^satisfy]\n\n" +
            "later[^11] and [^12]\n\n" +
            "[^satisfy]: first\n" +
            "[^11]: second\n" +
            "[^12]: third\n" +
            "  continuation\n";

        var root = engine.TransformElement(markdown);
        var references = root.Children
            .Select(element => element.Control)
            .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
            .SelectMany(text => DescendantSpans(text.Content))
            .OfType<CHyperlink>()
            .Where(link => link.Classes.Contains(global::ColorDocument.Avalonia.ClassNames.FootnoteReferenceClass))
            .ToArray();

        Assert.Equal(new[] { "[1]", "[2]", "[3]" }, references.Select(link => link.AsString()));

        var list = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>(root.Children.Last());
        var definitions = list.Children
            .Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>()
            .Select(item => string.Concat(item.Children.Select(element => ReadControlText(element.Control))))
            .ToArray();

        Assert.Equal(new[] { "first", "second", "third continuation" }, definitions);
    }

    [Fact]
    public void ViewerPipeline_PreservesHtmlAndMathAroundFootnoteReferences()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "math $x^2$ and <s>html</s>[^note].\n\n" +
            "[^note]: definition\n";

        var root = engine.TransformElement(markdown);
        var body = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(root.Children.First().Control);
        var spans = DescendantSpans(body.Content).ToArray();

        Assert.Contains(body.Content, inline => inline is CInlineUIContainer);
        Assert.Contains(spans, span => span.IsStrikethrough);
        Assert.Contains(spans.OfType<CHyperlink>(), link =>
            link.Classes.Contains(global::ColorDocument.Avalonia.ClassNames.FootnoteReferenceClass) &&
            link.AsString() == "[1]");
        Assert.DoesNotContain("[^note]", body.Text);
    }

    [Fact]
    public void ViewerPipeline_RoutesMarkdownHeadingAnchorsInternally()
    {
        var engine = CreateFullEngine();
        var provider = Assert.IsAssignableFrom<global::Markdown.Avalonia.IDocumentAnchorProvider>(engine);
        string? navigatedAnchor = null;
        provider.AnchorNavigationRequested = anchor => navigatedAnchor = anchor;

        var root = engine.TransformElement(
            "# Automatic Heading\n\n" +
            "[automatic](#automatic-heading)\n\n" +
            "## Explicit Heading {#custom-heading}\n\n" +
            "[explicit](#custom-heading)\n");

        Assert.True(provider.TryGetDocumentAnchor("automatic-heading", out var automaticTarget));
        Assert.True(provider.TryGetDocumentAnchor("#custom-heading", out var explicitTarget));
        Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.HeaderElement>(automaticTarget);
        Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.HeaderElement>(explicitTarget);

        var links = root.Children
            .Select(element => element.Control)
            .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
            .SelectMany(text => DescendantSpans(text.Content))
            .OfType<CHyperlink>()
            .ToArray();

        Assert.Equal(2, links.Length);
        links[0].Command!(links[0].CommandParameter!);
        Assert.Equal("automatic-heading", navigatedAnchor);
        links[1].Command!(links[1].CommandParameter!);
        Assert.Equal("custom-heading", navigatedAnchor);
    }

    [Fact]
    public void ViewerPipeline_RoutesHtmlNameAndIdAnchorsInternally()
    {
        var engine = CreateFullEngine();
        var provider = Assert.IsAssignableFrom<global::Markdown.Avalonia.IDocumentAnchorProvider>(engine);
        var navigatedAnchors = new List<string>();
        provider.AnchorNavigationRequested = navigatedAnchors.Add;
        const string markdown =
            "<a href='#jump'>html jump</a>\n\n" +
            "<a name='jump'>html target</a>\n\n" +
            "[markdown jump](#jump_two)\n\n" +
            "<span id = \"jump_two\">span target</span>\n";

        var root = engine.TransformElement(markdown);

        Assert.True(provider.TryGetDocumentAnchor("jump", out var namedTarget));
        Assert.True(provider.TryGetDocumentAnchor("jump_two", out var idTarget));
        Assert.Contains("html target", ReadControlText(namedTarget.Control));
        Assert.Contains("span target", ReadControlText(idTarget.Control));

        var links = DescendantControls(root.Children.Select(element => element.Control))
            .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
            .SelectMany(text => DescendantSpans(text.Content))
            .OfType<CHyperlink>()
            .Where(link => link.CommandParameter?.StartsWith('#') == true)
            .ToArray();

        Assert.Equal(2, links.Length);
        foreach (var link in links)
            link.Command!(link.CommandParameter!);
        Assert.Equal(new[] { "jump", "jump_two" }, navigatedAnchors);
    }

    [Fact]
    public void ViewerPipeline_RoutesFootnoteReferenceToItsDefinitionAnchor()
    {
        var engine = CreateFullEngine();
        var provider = Assert.IsAssignableFrom<global::Markdown.Avalonia.IDocumentAnchorProvider>(engine);
        string? navigatedAnchor = null;
        provider.AnchorNavigationRequested = anchor => navigatedAnchor = anchor;

        var root = engine.TransformElement("body[^note]\n\n[^note]: definition\n");
        Assert.True(provider.TryGetDocumentAnchor("fn:1", out var target));
        Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>(target);

        var body = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(root.Children.First().Control);
        var reference = Assert.Single(
            DescendantSpans(body.Content).OfType<CHyperlink>(),
            link => link.Classes.Contains(global::ColorDocument.Avalonia.ClassNames.FootnoteReferenceClass));

        Assert.Equal("#fn:1", reference.CommandParameter);
        reference.Command!(reference.CommandParameter!);
        Assert.Equal("fn:1", navigatedAnchor);
    }

    [Fact]
    public void ViewerPipeline_StillRoutesExternalLinksToConfiguredCommand()
    {
        var command = new RecordingCommand();
        var engine = CreateFullEngine();
        engine.HyperlinkCommand = command;

        var root = engine.TransformElement("[external](https://example.com/path)\n");
        var body = Assert.IsType<global::ColorTextBlock.Avalonia.CTextBlock>(Assert.Single(root.Children).Control);
        var link = Assert.Single(DescendantSpans(body.Content).OfType<CHyperlink>());

        link.Command!(link.CommandParameter!);

        Assert.Equal("https://example.com/path", command.LastParameter);
    }

    [Fact]
    public void ViewerPipeline_ResolvesReferenceLinksAndRemovesDefinitions()
    {
        var engine = CreateFullEngine();
        const string markdown = "[anchor text][anchor-id]\n\n[anchor-id]: http://www.this-anchor-link.com/";

        var document = engine.TransformElement(markdown);
        var textBlocks = document.Children
            .Select(element => element.Control)
            .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
            .ToArray();

        var textBlock = Assert.Single(textBlocks);
        var link = Assert.Single(textBlock.Content.OfType<CHyperlink>());
        Assert.Equal("anchor text", link.AsString());
        Assert.Equal("http://www.this-anchor-link.com/", link.CommandParameter);
        Assert.DoesNotContain("anchor-id]:", textBlock.Text);
    }

    [Fact]
    public void ViewerPipeline_RendersAngleAutolinkAsHyperlink()
    {
        var engine = CreateFullEngine();

        var document = engine.TransformElement("Direct link: <https://github.com>");
        var textBlock = Assert.Single(document.Children)
            .Control as global::ColorTextBlock.Avalonia.CTextBlock;

        Assert.NotNull(textBlock);
        var link = Assert.Single(textBlock.Content.OfType<CHyperlink>());
        Assert.Equal("https://github.com", link.AsString());
        Assert.Equal("https://github.com", link.CommandParameter);
    }

    [Fact]
    public void FullMarkdown_RendersInlineMathAsControl()
    {
        var engine = CreateFullEngine();

        var result = engine.ParseGamutInline("before $x^2$ after").ToArray();

        Assert.Contains(result, inline => inline is CInlineUIContainer);
    }

    [Fact]
    public void FullMarkdown_RendersDisplayMathAsBlock()
    {
        var engine = CreateFullEngine();

        var result = engine.RunBlockGamut("$$E=mc^2$$\n", ParseStatus.Init).ToArray();

        Assert.Single(result);
        Assert.IsType<global::Avalonia.Controls.Border>(result[0]);
    }

    [Theory]
    [InlineData(@"\(\sqrt{x}\)", @"\sqrt{x}")]
    [InlineData(@"\[x > y\]", "x > y")]
    public void Math_NormalizesNestedDelimiters(string latex, string expected)
    {
        Assert.Equal(expected, global::Markdown.Avalonia.Math.MathPlugin.NormalizeLatex(latex));
    }

    [Fact]
    public void FullMarkdown_RendersMathFenceInsteadOfCodeBlock()
    {
        var engine = CreateFullEngine();

        var result = engine.RunBlockGamut("```math\n\\displaystyle x^2\n```\n", ParseStatus.Init).ToArray();

        var border = Assert.IsType<global::Avalonia.Controls.Border>(Assert.Single(result));
        Assert.IsType<global::Avalonia.Controls.Viewbox>(border.Child);
    }

    [Fact]
    public void FullMarkdown_LeavesOrdinaryFenceAsCodeBlock()
    {
        var engine = CreateFullEngine();

        var result = engine.ParseGamutElement(
            "```csharp\nvar x = 1;\n```\n", ParseStatus.Init).ToArray();

        Assert.Equal("CodeBlockElement", Assert.Single(result).ElementType);
    }

    [Fact]
    public void FullMarkdown_PreservesIndentedPhpCode()
    {
        var engine = CreateFullEngine();
        const string markdown = "\n    <?php\n        echo \"Hello world!\";\n    ?>\n";

        var result = engine.RunBlockGamut(markdown, ParseStatus.Init).ToArray();

        var border = Assert.IsType<global::Avalonia.Controls.Border>(Assert.Single(result));
        var scroll = Assert.IsType<global::Avalonia.Controls.ScrollViewer>(border.Child);
        var text = Assert.IsType<global::Avalonia.Controls.TextBlock>(scroll.Content);
        Assert.Equal("<?php\n    echo \"Hello world!\";\n?>", text.Text);
    }

    [Fact]
    public void FullMarkdown_PreservesBarePhpProcessingInstruction()
    {
        var engine = CreateFullEngine();
        const string markdown = "<?php\n    echo \"Hello world!\";\n?>";

        var result = engine.RunBlockGamut(markdown, ParseStatus.Init).ToArray();

        Assert.Contains("echo \"Hello world!\";", string.Concat(result.Select(ReadControlText)));
        Assert.Contains("?>", string.Concat(result.Select(ReadControlText)));
    }

    [Fact]
    public void StandardParser_KeepsDetailsWithMarkdownAsOneBlock()
    {
        const string markdown = """
                                <details><summary>CLICK ME</summary>
                                <p>

                                #### We can hide anything, even code!

                                ```ruby
                                   puts "Hello World"
                                ```

                                </p>
                                </details>

                                Text after details.
                                """;

        var blocks = StandardMarkdownParser.ParseBlocks(markdown).ToArray();

        Assert.Equal(2, blocks.Length);
        Assert.True(blocks[0].IsRawHtmlContainer);
        Assert.StartsWith("<details>", blocks[0].Source);
        Assert.EndsWith("</details>", blocks[0].Source);
        Assert.Contains("#### We can hide anything, even code!", blocks[0].Source);
        Assert.Contains("puts \"Hello World\"", blocks[0].Source);
        Assert.Equal("Text after details.", blocks[1].Source);
    }

    [Fact]
    public void ViewerPipeline_RendersDetailsMarkdownInsideSingleExpander()
    {
        var engine = CreateFullEngine();
        const string markdown = """
                                <details><summary>CLICK ME</summary>
                                <p>

                                #### We can hide anything, even code!

                                ```
                                   puts "Hello World"
                                ```

                                </p>
                                </details>

                                Text after details.
                                """;

        var root = engine.TransformElement(markdown);
        var topLevel = root.Children.ToArray();
        var expander = Assert.IsType<global::Avalonia.Controls.Expander>(topLevel[0].Control);
        var allText = string.Concat(topLevel.Select(element => ReadControlText(element.Control)));

        Assert.False(expander.IsExpanded);
        Assert.Contains("CLICK ME", ReadControlText(Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(expander.Header)));
        Assert.Contains("We can hide anything, even code!", ReadControlText(Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(expander.Content)));
        Assert.Contains("puts \"Hello World\"", ReadControlText(Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(expander.Content)));
        Assert.Contains("Text after details.", ReadControlText(topLevel[1].Control));
        Assert.DoesNotContain("</p>", allText);
        Assert.DoesNotContain("</details>", allText);

        var opened = engine.TransformElement(markdown.Replace("<details>", "<details open>"));
        var openedExpander = Assert.IsType<global::Avalonia.Controls.Expander>(opened.Children.First().Control);
        Assert.True(openedExpander.IsExpanded);
    }

    [Fact]
    public void InlineCode_ProtectsHtmlMarkupFromMixedHtmlFallback()
    {
        const string markdown = "支持下划线：`<u>下划线</u>` becomes <u>下划线</u>.";
        var root = StandardMarkdownParser.ParseInline(markdown);

        var protectedText = global::Markdown.Avalonia.Markdown.ProtectCodeInlines(
            markdown, root, out var codeInlines);

        Assert.Equal("<u>下划线</u>", Assert.Single(codeInlines).Value);
        Assert.DoesNotContain("`<u>下划线</u>`", protectedText);
        Assert.EndsWith("becomes <u>下划线</u>.", protectedText);
    }

    [Fact]
    public void InlineCode_ProtectsAngleEmailFromAutolinking()
    {
        const string markdown = "`<i@typora.io>` becomes <i@typora.io>.";
        var root = StandardMarkdownParser.ParseInline(markdown);

        var protectedText = global::Markdown.Avalonia.Markdown.ProtectCodeInlines(
            markdown, root, out var codeInlines);

        Assert.Equal("<i@typora.io>", Assert.Single(codeInlines).Value);
        Assert.False(protectedText.StartsWith("`<i@typora.io>`", StringComparison.Ordinal));
        Assert.EndsWith("becomes <i@typora.io>.", protectedText);
    }

    [Theory]
    [InlineData("支持高亮：==highlight==")]
    [InlineData("支持高亮：==highlight== and <u>underline</u>")]
    public void HighlightSyntax_RendersAsMarkInsteadOfBold(string markdown)
    {
        var engine = CreateFullEngine();

        var inlines = engine.ParseGamutInline(markdown).ToArray();
        var spans = DescendantSpans(inlines).ToArray();

        var marked = Assert.Single(spans, span => span.Classes.Contains("Mark"));
        Assert.Equal("highlight", marked.AsString());
        Assert.DoesNotContain(spans.OfType<CBold>(), bold => bold.AsString() == "highlight");
    }

    [Fact]
    public void KbdTag_UsesDedicatedKeyboardClass()
    {
        var parseInfo = new TypicalParseInfo(
            ["kbd", "ColorTextBlock.Avalonia.CCode", "TagKbd", ""]);

        Assert.Equal(Tags.TagKbd, parseInfo.TagName);
        Assert.Equal("Kbd", parseInfo.TagName.GetClass());
        Assert.NotEqual(Tags.TagCodeSpan.GetClass(), parseInfo.TagName.GetClass());
    }

    [Theory]
    [InlineData("var", "TagVar")]
    [InlineData("cite", "TagCite")]
    public void SemanticItalicTags_UseItalicInline(string tag, string tagName)
    {
        var parseInfo = new TypicalParseInfo(
            [tag, "ColorTextBlock.Avalonia.CItalic", tagName, ""]);

        Assert.Equal(typeof(CItalic), parseInfo.FlowDocumentTag);
    }

    [Fact]
    public void FontTag_AppliesFaceSizeAndInterElementSpacing()
    {
        var engine = CreateFullEngine();
        const string markdown =
            "<font face=\"STCAIYUN\">first</font>  " +
            "<font color=#0099ff size=5 face=\"黑体\">second</font>";

        var inlines = engine.ParseGamutInline(markdown).ToArray();
        var fontSpans = DescendantSpans(inlines)
            .Where(span => span.Classes.Contains("Font"))
            .ToArray();

        Assert.Equal(2, fontSpans.Length);
        Assert.Contains("STCAIYUN", fontSpans[0].FontFamily.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(18, fontSpans[1].FontSize);
        Assert.Contains("黑体", fontSpans[1].FontFamily.ToString(), StringComparison.Ordinal);
        Assert.All(fontSpans[1].Content, child => Assert.Equal(18, child.FontSize));
        Assert.Equal("\u00A0", Assert.Single(inlines.OfType<CRun>()).Text);
    }

    [Fact]
    public void HtmlTable_AppliesBgcolorWithCellOverRowPrecedence()
    {
        var engine = CreateFullEngine();
        const string markdown = """
                                <table><tbody>
                                <tr bgcolor="CornflowerBlue">
                                  <td>row color</td>
                                  <td bgcolor="Tomato">cell color</td>
                                </tr>
                                </tbody></table>
                                """;

        var root = engine.TransformElement(markdown);
        var tableBorder = Assert.IsType<global::Avalonia.Controls.Border>(Assert.Single(root.Children).Control);
        var grid = Assert.IsType<global::Avalonia.Controls.Grid>(tableBorder.Child);
        var cells = grid.Children.OfType<global::Avalonia.Controls.Border>().ToArray();

        Assert.Equal(2, cells.Length);
        Assert.Equal(
            global::Avalonia.Media.Colors.CornflowerBlue,
            Assert.IsType<global::Avalonia.Media.SolidColorBrush>(cells[0].Background).Color);
        Assert.Equal(
            global::Avalonia.Media.Colors.Tomato,
            Assert.IsType<global::Avalonia.Media.SolidColorBrush>(cells[1].Background).Color);
    }

    [Theory]
    [InlineData("note", global::ColorDocument.Avalonia.DocumentElements.AlertType.Note)]
    [InlineData("abstract", global::ColorDocument.Avalonia.DocumentElements.AlertType.Abstract)]
    [InlineData("summary", global::ColorDocument.Avalonia.DocumentElements.AlertType.Abstract)]
    [InlineData("tldr", global::ColorDocument.Avalonia.DocumentElements.AlertType.Abstract)]
    [InlineData("info", global::ColorDocument.Avalonia.DocumentElements.AlertType.Info)]
    [InlineData("todo", global::ColorDocument.Avalonia.DocumentElements.AlertType.Todo)]
    [InlineData("tip", global::ColorDocument.Avalonia.DocumentElements.AlertType.Tip)]
    [InlineData("hint", global::ColorDocument.Avalonia.DocumentElements.AlertType.Tip)]
    [InlineData("important", global::ColorDocument.Avalonia.DocumentElements.AlertType.Important)]
    [InlineData("success", global::ColorDocument.Avalonia.DocumentElements.AlertType.Success)]
    [InlineData("check", global::ColorDocument.Avalonia.DocumentElements.AlertType.Success)]
    [InlineData("done", global::ColorDocument.Avalonia.DocumentElements.AlertType.Success)]
    [InlineData("question", global::ColorDocument.Avalonia.DocumentElements.AlertType.Question)]
    [InlineData("help", global::ColorDocument.Avalonia.DocumentElements.AlertType.Question)]
    [InlineData("faq", global::ColorDocument.Avalonia.DocumentElements.AlertType.Question)]
    [InlineData("warning", global::ColorDocument.Avalonia.DocumentElements.AlertType.Warning)]
    [InlineData("attention", global::ColorDocument.Avalonia.DocumentElements.AlertType.Warning)]
    [InlineData("caution", global::ColorDocument.Avalonia.DocumentElements.AlertType.Caution)]
    [InlineData("failure", global::ColorDocument.Avalonia.DocumentElements.AlertType.Failure)]
    [InlineData("fail", global::ColorDocument.Avalonia.DocumentElements.AlertType.Failure)]
    [InlineData("missing", global::ColorDocument.Avalonia.DocumentElements.AlertType.Failure)]
    [InlineData("danger", global::ColorDocument.Avalonia.DocumentElements.AlertType.Danger)]
    [InlineData("error", global::ColorDocument.Avalonia.DocumentElements.AlertType.Danger)]
    [InlineData("bug", global::ColorDocument.Avalonia.DocumentElements.AlertType.Bug)]
    [InlineData("example", global::ColorDocument.Avalonia.DocumentElements.AlertType.Example)]
    [InlineData("quote", global::ColorDocument.Avalonia.DocumentElements.AlertType.Quote)]
    [InlineData("cite", global::ColorDocument.Avalonia.DocumentElements.AlertType.Quote)]
    [InlineData("Any", global::ColorDocument.Avalonia.DocumentElements.AlertType.Note)]
    public void ObsidianCalloutAliases_MapToSupportedVisualType(
        string marker,
        global::ColorDocument.Avalonia.DocumentElements.AlertType expectedType)
    {
        var engine = CreateFullEngine();

        var element = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.AlertBlockElement>(
            Assert.Single(engine.ParseGamutElement($"> [!{marker}]\n> body\n", ParseStatus.Init)));

        Assert.Equal(expectedType, element.AlertType);
        Assert.Equal(
            char.ToUpperInvariant(marker[0]) + marker[1..].ToLowerInvariant(),
            element.Title);
    }

    [Fact]
    public void ObsidianCallout_AllowsEmptyBodyAndCustomTitle()
    {
        var engine = CreateFullEngine();

        var empty = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.AlertBlockElement>(
            Assert.Single(engine.ParseGamutElement("> [!summary]\n", ParseStatus.Init)));
        var titled = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.AlertBlockElement>(
            Assert.Single(engine.ParseGamutElement("> [!note] Custom title\n> body\n", ParseStatus.Init)));

        Assert.Empty(empty.Children);
        Assert.Equal("Summary", empty.Title);
        Assert.Equal("Custom title", titled.Title);
    }

    private static string ReadControlText(global::Avalonia.Controls.Control control)
    {
        return control switch
        {
            global::ColorTextBlock.Avalonia.CTextBlock text => text.Text,
            global::Avalonia.Controls.TextBlock text => text.Text ?? string.Empty,
            global::Avalonia.Controls.ContentControl content when content.Content is global::Avalonia.Controls.Control child
                => ReadControlText(child),
            global::Avalonia.Controls.Panel panel => string.Concat(panel.Children.Select(ReadControlText)),
            global::Avalonia.Controls.Decorator decorator when decorator.Child is not null => ReadControlText(decorator.Child),
            _ => string.Empty
        };
    }

    private static IEnumerable<CSpan> DescendantSpans(IEnumerable<CInline> inlines)
    {
        foreach (var span in inlines.OfType<CSpan>())
        {
            yield return span;
            foreach (var child in DescendantSpans(span.Content))
                yield return child;
        }
    }

    private static IEnumerable<global::Avalonia.Controls.Control> DescendantControls(
        IEnumerable<global::Avalonia.Controls.Control> controls)
    {
        foreach (var control in controls)
        {
            yield return control;
            switch (control)
            {
                case global::Avalonia.Controls.Panel panel:
                    foreach (var child in DescendantControls(panel.Children)) yield return child;
                    break;
                case global::Avalonia.Controls.Decorator decorator when decorator.Child is not null:
                    foreach (var child in DescendantControls([decorator.Child])) yield return child;
                    break;
                case global::Avalonia.Controls.ContentControl content when content.Content is global::Avalonia.Controls.Control child:
                    foreach (var descendant in DescendantControls([child])) yield return descendant;
                    break;
            }
        }
    }

    private static global::Avalonia.Controls.CheckBox[] ReadTaskListMarkers(
        global::ColorDocument.Avalonia.DocumentElements.ListBlockElement list)
    {
        var grid = Assert.IsType<global::Avalonia.Controls.Grid>(list.Control);
        return grid.Children.OfType<global::Avalonia.Controls.CheckBox>()
            .Where(marker => global::Avalonia.Controls.Grid.GetColumn(marker) == 0)
            .ToArray();
    }

    private static string[] ReadListMarkerTexts(
        global::ColorDocument.Avalonia.DocumentElements.ListBlockElement list)
    {
        var grid = Assert.IsType<global::Avalonia.Controls.Grid>(list.Control);
        return grid.Children
            .OfType<global::ColorTextBlock.Avalonia.CTextBlock>()
            .Where(marker => global::Avalonia.Controls.Grid.GetColumn(marker) == 0)
            .OrderBy(global::Avalonia.Controls.Grid.GetRow)
            .Select(marker => marker.Text)
            .ToArray();
    }


    private static IEnumerable<global::ColorDocument.Avalonia.DocumentElements.ListItemElement> DescendantListItems(
        global::ColorDocument.Avalonia.DocumentElements.ListBlockElement list)
    {
        foreach (var item in list.Children.Cast<global::ColorDocument.Avalonia.DocumentElements.ListItemElement>())
        {
            yield return item;
            foreach (var nested in item.Children.OfType<global::ColorDocument.Avalonia.DocumentElements.ListBlockElement>())
                foreach (var descendant in DescendantListItems(nested))
                    yield return descendant;
        }
    }

    [Fact]
    public void StandardParser_KeepsFencedPhpAsOneAstBlock()
    {
        const string markdown = "before\n\n```php\n<?php\n    echo \"Hello world!\";\n?>\n```\n\nafter";

        var blocks = StandardMarkdownParser.ParseBlocks(markdown).ToArray();

        Assert.Equal(3, blocks.Length);
        Assert.Contains("echo \"Hello world!\";", blocks[1].Source);
        Assert.StartsWith("```php", blocks[1].Source);
        Assert.EndsWith("```", blocks[1].Source);
    }

    [Fact]
    public void StandardParser_KeepsIndentedPhpAsOneAstBlock()
    {
        const string markdown = "    <?php\n        echo \"Hello world!\";\n    ?>\n";

        var block = Assert.Single(StandardMarkdownParser.ParseBlocks(markdown));

        Assert.Contains("<?php", block.Source);
        Assert.Contains("?>", block.Source);
    }

    [Fact]
    public void StandardInlinePipeline_RendersNestedCommonMarkNodes()
    {
        var engine = CreateFullEngine();

        var result = engine.RunSpanGamut("**bold and *italic*** plus text").ToArray();

        var bold = Assert.IsType<global::ColorTextBlock.Avalonia.CBold>(result[0]);
        Assert.Contains(bold.Content, item => item is global::ColorTextBlock.Avalonia.CItalic);
        Assert.Equal("bold and italic plus text", string.Concat(result.Select(item => item.AsString())));
    }

    [Fact]
    public void StandardInlinePipeline_UsesCommonMarkUnderscoreBoundaries()
    {
        var engine = CreateFullEngine();

        var result = engine.RunSpanGamut("foo_bar_baz").ToArray();

        Assert.Single(result);
        Assert.IsType<global::ColorTextBlock.Avalonia.CRun>(result[0]);
        Assert.Equal("foo_bar_baz", result[0].AsString());
    }

    [Fact]
    public void StandardBlockPipeline_RendersHeadingDirectlyFromAst()
    {
        var engine = CreateFullEngine();

        var result = engine.ParseGamutElement("## **AST** heading\n", ParseStatus.Init).ToArray();

        var heading = Assert.IsType<global::ColorDocument.Avalonia.DocumentElements.HeaderElement>(Assert.Single(result));
        Assert.Equal(2, heading.Level);
        Assert.Equal("AST heading", heading.Text);
    }

    private static global::Markdown.Avalonia.Markdown CreateFullEngine()
    {
        return new global::Markdown.Avalonia.Markdown
        {
            Plugins = new global::Markdown.Avalonia.Full.MdAvPlugins()
        };
    }

    private sealed class RecordingCommand : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public object? LastParameter { get; private set; }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => LastParameter = parameter;
    }
}
