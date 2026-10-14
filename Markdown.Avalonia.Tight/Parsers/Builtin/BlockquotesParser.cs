using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Markdown.Avalonia.Parsers.Builtin
{
    internal class BlockquotesParser : BlockParser2
    {
        private static readonly Regex _blockquoteFirst = new(@"
            ^
            ([>].*)
            (\n[>].*)*
            [\n]*
            ", RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

        // GitHub alerts and Obsidian callouts. The optional suffix/title is
        // recognized now so it never leaks into the rendered body.
        private static readonly Regex _alertPattern = new(
            @"^\[!(?<type>[A-Z0-9_-]+)\](?<fold>[+-])?(?:[ \t]+(?<title>.*?))?[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool _supportTextAlignment;

        public BlockquotesParser(bool supportTextAlignment) : base(_blockquoteFirst, "BlockquotesEvaluator")
        {
            _supportTextAlignment = supportTextAlignment;
        }

        public override IEnumerable<DocumentElement>? Convert2(string text, Match firstMatch, ParseStatus status, IMarkdownEngine2 engine, out int parseTextBegin, out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = firstMatch.Index + firstMatch.Length;

            // trim '>'
            var lines = firstMatch.Value.Trim().Split('\n')
                .Select(txt =>
                {
                    if (txt.Length <= 1) return string.Empty;
                    var trimmed = txt.Substring(1);
                    if (trimmed.FirstOrDefault() == ' ') trimmed = trimmed.Substring(1);
                    return trimmed;
                })
                .ToArray();

            // Check if first line is a GitHub-style alert marker
            if (lines.Length > 0)
            {
                var alertMatch = _alertPattern.Match(lines[0]);
                if (alertMatch.Success)
                {
                    var rawType = alertMatch.Groups["type"].Value;
                    var alertType = rawType.ToUpperInvariant() switch
                    {
                        "ABSTRACT" or "SUMMARY" or "TLDR" => AlertType.Abstract,
                        "INFO" => AlertType.Info,
                        "TODO" => AlertType.Todo,
                        "TIP" => AlertType.Tip,
                        "HINT" => AlertType.Tip,
                        "IMPORTANT" => AlertType.Important,
                        "SUCCESS" or "CHECK" or "DONE" => AlertType.Success,
                        "QUESTION" or "HELP" or "FAQ" => AlertType.Question,
                        "WARNING" => AlertType.Warning,
                        "ATTENTION" => AlertType.Warning,
                        "CAUTION" => AlertType.Caution,
                        "FAILURE" or "FAIL" or "MISSING" => AlertType.Failure,
                        "DANGER" or "ERROR" => AlertType.Danger,
                        "BUG" => AlertType.Bug,
                        "EXAMPLE" => AlertType.Example,
                        "QUOTE" or "CITE" => AlertType.Quote,
                        _ => AlertType.Note
                    };
                    var customTitle = alertMatch.Groups["title"].Value.Trim();
                    var alertTitle = customTitle.Length > 0
                        ? customTitle
                        : char.ToUpperInvariant(rawType[0]) + rawType[1..].ToLowerInvariant();

                    // Get content after the alert marker (skip first line)
                    var contentLines = lines.Skip(1).ToArray();
                    var trimmedTxt = string.Join("\n", contentLines);

                    var newStatus = new ParseStatus(true & _supportTextAlignment);
                    var blocks = engine.ParseGamutElement(trimmedTxt + "\n", newStatus);

                    return new[] { new AlertBlockElement(blocks, alertType, alertTitle) };
                }
            }

            // Regular blockquote
            var trimmedText = string.Join("\n", lines);

            var regularStatus = new ParseStatus(true & _supportTextAlignment);
            var regularBlocks = engine.ParseGamutElement(trimmedText + "\n", regularStatus);

            return new[] { new BlockquoteElement(regularBlocks) };
        }
    }
}

