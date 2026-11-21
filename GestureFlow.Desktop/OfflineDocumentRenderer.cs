using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GestureFlow.Desktop;

// The built-in documents use headings, lists, fenced code, tables and local links.
internal static partial class OfflineDocumentRenderer
{
    public static FlowDocument Render(string text, bool markdown, Func<string, Action?> resolveLink)
    {
        var document = new FlowDocument { FontSize = 14, PagePadding = new Thickness(18, 14, 18, 18) };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (!markdown)
        {
            document.Blocks.Add(new Paragraph(new Run(string.Join('\n', lines))) { FontFamily = new FontFamily("Consolas") });
            return document;
        }

        List? list = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            var marker = ListMarker().Match(line);
            if (marker.Success)
            {
                var style = marker.Value[0] == '-' ? TextMarkerStyle.Disc : TextMarkerStyle.Decimal;
                if (list is null || list.MarkerStyle != style)
                {
                    list = new List { MarkerStyle = style, Margin = new Thickness(0, 2, 0, 10) };
                    document.Blocks.Add(list);
                }

                var item = MakeParagraph(line[marker.Length..], resolveLink);
                item.Margin = new Thickness(0, 0, 0, 5);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            list = null;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var start = ++index;
                while (index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal))
                    index++;
                var code = new Paragraph(new Run(string.Join('\n', lines[start..index])))
                {
                    FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10, 7, 10, 7)
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorDefaultBrush");
                document.Blocks.Add(code);
                continue;
            }

            if (line.StartsWith('|') && index + 1 < lines.Length && TableSeparator().IsMatch(lines[index + 1]))
            {
                var header = SplitCells(line);
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 3, 0, 12) };
                for (var column = 0; column < header.Length; column++)
                    table.Columns.Add(new TableColumn { Width = new GridLength(column == 0 ? 1 : 3, GridUnitType.Star) });
                var rows = new TableRowGroup();
                table.RowGroups.Add(rows);
                rows.Rows.Add(MakeRow(header, header.Length, true, resolveLink));
                index += 2;
                while (index < lines.Length && lines[index].StartsWith('|'))
                {
                    rows.Rows.Add(MakeRow(SplitCells(lines[index]), header.Length, false, resolveLink));
                    index++;
                }
                index--;
                document.Blocks.Add(table);
                continue;
            }

            var heading = HeadingMarker().Match(line);
            var paragraph = MakeParagraph(line[heading.Length..], resolveLink);
            if (heading.Success)
            {
                paragraph.FontSize = heading.Length switch { 2 => 25, 3 => 20, _ => 16 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Margin = new Thickness(0, 10, 0, 7);
            }
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private static string[] SplitCells(string line) => line.Trim().Trim('|').Split('|', StringSplitOptions.TrimEntries);

    private static TableRow MakeRow(string[] cells, int columns, bool header, Func<string, Action?> resolveLink)
    {
        var row = new TableRow();
        for (var index = 0; index < columns; index++)
        {
            var paragraph = MakeParagraph(index < cells.Length ? cells[index] : "", resolveLink);
            paragraph.Margin = new Thickness(0);
            var cell = new TableCell(paragraph)
            {
                Padding = new Thickness(7, 5, 7, 5), BorderThickness = new Thickness(0, 0, 0, 1),
                FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal
            };
            cell.SetResourceReference(TableCell.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            row.Cells.Add(cell);
        }
        return row;
    }

    private static Paragraph MakeParagraph(string text, Func<string, Action?> resolveLink)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
        var position = 0;
        foreach (Match match in InlineMarkup().Matches(text))
        {
            if (match.Index > position)
                paragraph.Inlines.Add(new Run(text[position..match.Index]));

            if (match.Groups["code"].Success)
                paragraph.Inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") });
            else
            {
                var label = new Run(match.Groups["label"].Value);
                if (resolveLink(match.Groups["target"].Value) is { } open)
                {
                    var link = new Hyperlink(label);
                    link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                    link.Click += (_, _) => open();
                    paragraph.Inlines.Add(link);
                }
                else
                    paragraph.Inlines.Add(label);
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length)
            paragraph.Inlines.Add(new Run(text[position..]));
        return paragraph;
    }

    [GeneratedRegex(@"^(?:- |\d+\. )")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^#{1,6} ")]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^\s*\|(?:\s*:?-{3,}:?\s*\|)+\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<target>[^()]+)\)")]
    private static partial Regex InlineMarkup();
}
