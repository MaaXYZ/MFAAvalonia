using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using System;
using System.Text.RegularExpressions;

namespace MFAAvalonia.Android;

/// <summary>
/// Small native Markdown renderer for the system-overlay log window. The overlay cannot host
/// Avalonia controls, so it converts the log-oriented Markdown subset to Android spans.
/// </summary>
internal static partial class AndroidOverlayMarkdown
{
    private static readonly Color QuoteColor = Color.Rgb(144, 202, 249);
    private static readonly Color CodeBackground = Color.Argb(120, 0, 0, 0);

    public static SpannableStringBuilder Render(string markdown)
    {
        var output = new SpannableStringBuilder();
        var lines = (markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var inCodeBlock = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                inCodeBlock = !inCodeBlock;
                if (i < lines.Length - 1 && output.Length() > 0)
                    output.Append("\n");
                continue;
            }

            var lineStart = output.Length();
            if (inCodeBlock)
            {
                output.Append(line);
                Apply(output, new TypefaceSpan("monospace"), lineStart, output.Length());
                Apply(output, new BackgroundColorSpan(CodeBackground), lineStart, output.Length());
            }
            else
            {
                AppendLine(output, line);
            }

            if (i < lines.Length - 1)
                output.Append("\n");
        }

        return output;
    }

    private static void AppendLine(SpannableStringBuilder output, string line)
    {
        var heading = HeadingRegex().Match(line);
        if (heading.Success)
        {
            var start = output.Length();
            AppendInline(output, heading.Groups[2].Value);
            Apply(output, new StyleSpan(TypefaceStyle.Bold), start, output.Length());
            var level = heading.Groups[1].Value.Length;
            Apply(output, new RelativeSizeSpan(level switch
            {
                1 => 1.4f,
                2 => 1.3f,
                3 => 1.2f,
                _ => 1.1f
            }), start, output.Length());
            return;
        }

        var quote = QuoteRegex().Match(line);
        if (quote.Success)
        {
            var markerStart = output.Length();
            output.Append("▌ ");
            Apply(output, new ForegroundColorSpan(QuoteColor), markerStart, output.Length());
            AppendInline(output, quote.Groups[1].Value);
            return;
        }

        var bullet = BulletRegex().Match(line);
        if (bullet.Success)
        {
            output.Append(new string(' ', bullet.Groups[1].Value.Length));
            output.Append("• ");
            AppendInline(output, bullet.Groups[2].Value);
            return;
        }

        var ordered = OrderedRegex().Match(line);
        if (ordered.Success)
        {
            output.Append(new string(' ', ordered.Groups[1].Value.Length));
            output.Append(ordered.Groups[2].Value);
            output.Append(". ");
            AppendInline(output, ordered.Groups[3].Value);
            return;
        }

        if (RuleRegex().IsMatch(line))
        {
            output.Append("────────────────");
            return;
        }

        AppendInline(output, line);
    }

    private static void AppendInline(SpannableStringBuilder output, string source)
    {
        var position = 0;
        foreach (Match match in InlineTokenRegex().Matches(source))
        {
            if (match.Index > position)
                output.Append(Unescape(source[position..match.Index]));

            var start = output.Length();
            if (match.Groups["image"].Success)
            {
                var alt = match.Groups["imageAlt"].Value;
                output.Append(string.IsNullOrWhiteSpace(alt) ? "图片" : $"图片：{alt}");
                Apply(output, new OverlayLinkSpan(match.Groups["imageUrl"].Value), start, output.Length());
            }
            else if (match.Groups["link"].Success)
            {
                output.Append(Unescape(match.Groups["linkText"].Value));
                Apply(output, new OverlayLinkSpan(match.Groups["linkUrl"].Value), start, output.Length());
            }
            else if (match.Groups["code"].Success)
            {
                output.Append(match.Groups["codeText"].Value);
                Apply(output, new TypefaceSpan("monospace"), start, output.Length());
                Apply(output, new BackgroundColorSpan(CodeBackground), start, output.Length());
            }
            else if (match.Groups["bold"].Success)
            {
                output.Append(Unescape(match.Groups["boldText"].Value));
                Apply(output, new StyleSpan(TypefaceStyle.Bold), start, output.Length());
            }
            else if (match.Groups["italic"].Success)
            {
                output.Append(Unescape(match.Groups["italicText"].Value));
                Apply(output, new StyleSpan(TypefaceStyle.Italic), start, output.Length());
            }
            else if (match.Groups["strike"].Success)
            {
                output.Append(Unescape(match.Groups["strikeText"].Value));
                Apply(output, new StrikethroughSpan(), start, output.Length());
            }

            position = match.Index + match.Length;
        }

        if (position < source.Length)
            output.Append(Unescape(source[position..]));
    }

    private static string Unescape(string value) => EscapeRegex().Replace(value, "$1");

    private static void Apply(SpannableStringBuilder output, Java.Lang.Object span, int start, int end)
    {
        if (end > start)
            output.SetSpan(span, start, end, SpanTypes.ExclusiveExclusive);
        else
            span.Dispose();
    }

    private sealed class OverlayLinkSpan(string url) : ClickableSpan
    {
        public override void OnClick(View widget)
        {
            try
            {
                var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
                intent.AddFlags(ActivityFlags.NewTask);
                widget.Context?.StartActivity(intent);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("MfaCurrentScreenOverlay",
                    $"Unable to open Markdown link '{url}': {ex.Message}");
            }
        }
    }

    [GeneratedRegex(@"^\s*((\x60{3})|~~~)")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(#{1,6})[ \t]+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*>[ \t]?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^(\s*)[-+*][ \t]+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\s*)(\d+)[.)][ \t]+(.*)$")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"^\s*((-{3,})|(\*{3,})|(_{3,}))\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(
        @"(?<image>!\[(?<imageAlt>[^\]]*)\]\((?<imageUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|" +
        @"(?<link>\[(?<linkText>[^\]]+)\]\((?<linkUrl>[^)\s]+)(?:\s+""[^""]*"")?\))|" +
        @"(?<code>\x60(?<codeText>[^\x60\r\n]+)\x60)|" +
        @"(?<bold>\*\*(?<boldText>[^*\r\n]+)\*\*|__(?<boldText>[^_\r\n]+)__)|" +
        @"(?<strike>~~(?<strikeText>[^~\r\n]+)~~)|" +
        @"(?<italic>\*(?<italicText>[^*\r\n]+)\*|_(?<italicText>[^_\r\n]+)_)")]
    private static partial Regex InlineTokenRegex();

    [GeneratedRegex(@"\\([\\\x60*_{}\[\]()#+\-.!>~])")]
    private static partial Regex EscapeRegex();
}
