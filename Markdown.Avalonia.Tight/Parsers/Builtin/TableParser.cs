using Avalonia.Layout;
using Avalonia.Media;
using ColorDocument.Avalonia;
using ColorDocument.Avalonia.DocumentElements;
using ColorTextBlock.Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Markdown.Avalonia.Parsers.Builtin
{
    internal class TableParser : BlockParser2
    {
        private static readonly Regex _table = new(@"
            (                               # whole table
                [ \n]*
                (?<hdr>                     # table header
                    ([^\n\|]*\|[^\n]+)
                )
                [ ]*\n[ ]*
                (?<col>                     # column style
                    \|?([ ]*:?-+:?[ ]*(\||$))+
                )
                (?<row>                     # table row
                    (
                        [ ]*\n[ ]*
                        ([^\n\|]*\|[^\n]+)
                    )+
                )
            )",
            RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        public TableParser() : base(_table, "TableEvalutor")
        {
        }

        public override IEnumerable<DocumentElement>? Convert2(
            string text, Match firstMatch,
            ParseStatus status,
            IMarkdownEngine2 engine,
            out int parseTextBegin, out int parseTextEnd)
        {
            parseTextBegin = firstMatch.Index;
            parseTextEnd = parseTextBegin + firstMatch.Length;

            return new[] { TableEvalutor(firstMatch, engine) };
        }

        private TableBlockElement TableEvalutor(Match match, IMarkdownEngine2 engine)
        {
            Dictionary<int, TextAlignment> styleMt =
                SplitRowCells(match.Groups["col"].Value.Trim())
                    .Select((styleText, idx) =>
                    {
                        var text = styleText.Trim();
                        var firstChar = text[0];
                        var lastChar = text[text.Length - 1];

                        return
                            firstChar == ':' && lastChar == ':' ?
                                 Tuple.Create(idx, (TextAlignment?)TextAlignment.Center) :

                            lastChar == ':' ?
                                Tuple.Create(idx, (TextAlignment?)TextAlignment.Right) :

                            firstChar == ':' ?
                                Tuple.Create(idx, (TextAlignment?)TextAlignment.Left) :

                                Tuple.Create(idx, (TextAlignment?)null);
                    })
                    .Where(tpl => tpl.Item2.HasValue)
                    .ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2!.Value);


            int colOffset = 0;
            TableCellElement[][] headerCells = new[] { CreateRow(styleMt, match.Groups["hdr"].Value, engine, true) };

            List<TableCellElement[]> detailCells = new();
            foreach (var cellline in match.Groups["row"].Value.Trim().Split('\n'))
            {
                detailCells.Add(CreateRow(styleMt, cellline, engine, false));
            }


            return new TableBlockElement(headerCells, detailCells.ToArray(), Array.Empty<TableCellElement[]>(), true);
        }

        private TableCellElement[] CreateRow(Dictionary<int, TextAlignment> styleMt, string txt, IMarkdownEngine2 engine, bool ignoreRowSpan)
        {
            int colOffset = 0;
            List<TableCellElement> cells = new();
            foreach (var celltxt in SplitRowCells(txt.Trim()))
            {
                var cell = CreateCell(celltxt, engine);

                if (ignoreRowSpan)
                    cell.RowSpan = 1;

                // apply text align
                if (styleMt.TryGetValue(colOffset, out var style))
                    cell.Horizontal = style;

                cells.Add(cell);

                colOffset += cell.ColSpan;
            }
            return cells.ToArray();
        }

        private TableCellElement CreateCell(string txt, IMarkdownEngine2 engine)
        {
            int colspan = 1;
            int rowspan = 1;
            TextAlignment? horizontal = null;
            VerticalAlignment? vertical = null;

            int idx = txt.IndexOf('.');
            if (idx != -1)
            {
                var styleTxt = txt.Substring(0, idx);

                for (var i = 0; i < styleTxt.Length; ++i)
                {
                    switch (styleTxt[i])
                    {
                        case '/': // /2 rowspan
                            ++i;
                            var numTxt = ContinueToNum(styleTxt, ref i);
                            if (numTxt.Length == 0) goto default;
                            rowspan = int.Parse(numTxt);

                            break;

                        case '\\': // \2 colspan
                            ++i;
                            numTxt = ContinueToNum(styleTxt, ref i);
                            if (numTxt.Length == 0) goto default;
                            colspan = int.Parse(numTxt);
                            break;

                        case '<': // < left align
                            horizontal = TextAlignment.Left;
                            break;

                        case '>': // > right align
                            horizontal = TextAlignment.Right;
                            break;

                        case '=': // = center align 
                            horizontal = TextAlignment.Center;
                            break;

                        case '^': // ^ top align
                            vertical = VerticalAlignment.Top;
                            break;

                        case '~': // ~ bottom align
                            vertical = VerticalAlignment.Bottom;
                            break;

                        default:
                            rowspan = 1;
                            colspan = 1;
                            horizontal = null;
                            vertical = null;
                            goto endparse;
                    }
                }

                txt = txt.Substring(idx + 1);

            endparse:;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < txt.Length; ++i)
            {
                var c = txt[i];

                if (c == '\\')
                {
                    if (++i < txt.Length)
                    {
                        if (txt[i] == 'n')
                            sb.Append("  \n"); // \n => linebreak
                        else
                            sb.Append('\\').Append(txt[i]);
                    }
                    else
                        sb.Append('\\');
                }
                else
                    sb.Append(c);
            }

            return new TableCellElement(new CTextBlockElement(engine.ParseGamutInline(sb.ToString().Trim())))
            {
                ColSpan = colspan,
                RowSpan = rowspan,
                Horizontal = horizontal,
                Vertical = vertical,
            };
        }

        internal static IReadOnlyList<string> SplitRowCells(string text)
        {
            text = text.Trim();
            if (text.Length == 0)
                return [string.Empty];

            var cells = new List<string>();
            var cell = new StringBuilder();
            var pendingBackslashes = 0;

            foreach (var c in text)
            {
                if (c == '\\')
                {
                    pendingBackslashes++;
                    continue;
                }

                if (c == '|')
                {
                    cell.Append('\\', pendingBackslashes / 2);
                    if (pendingBackslashes % 2 == 1)
                    {
                        // An odd backslash escapes the pipe and is consumed by
                        // the table tokenizer. The inline parser receives the
                        // literal pipe as cell content.
                        cell.Append('|');
                    }
                    else
                    {
                        cells.Add(cell.ToString());
                        cell.Clear();
                    }

                    pendingBackslashes = 0;
                    continue;
                }

                cell.Append('\\', pendingBackslashes);
                pendingBackslashes = 0;
                cell.Append(c);
            }

            cell.Append('\\', pendingBackslashes);
            cells.Add(cell.ToString());

            if (text[0] == '|' && cells.Count > 0 && cells[0].Length == 0)
                cells.RemoveAt(0);
            if (IsUnescapedTrailingPipe(text) && cells.Count > 0 && cells[^1].Length == 0)
                cells.RemoveAt(cells.Count - 1);

            return cells;

            static bool IsUnescapedTrailingPipe(string source)
            {
                if (source[^1] != '|')
                    return false;

                var backslashes = 0;
                for (var index = source.Length - 2; index >= 0 && source[index] == '\\'; index--)
                    backslashes++;
                return backslashes % 2 == 0;
            }
        }

        private static string ContinueToNum(string charSource, ref int idx)
        {
            var builder = new StringBuilder();

            for (; idx < charSource.Length; ++idx)
            {
                var c = charSource[idx];

                if ('0' <= c && c <= '9')
                    builder.Append(c);

                else break;
            }
            --idx;
            return builder.ToString();
        }
    }
}
