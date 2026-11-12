using Markdig.Extensions.Tables;

namespace RuApp.Controls;

/// <summary>A complete, selectable/copyable table.</summary>
internal sealed record MarkdownTablePresentation(
    IReadOnlyList<IReadOnlyList<string>> Rows, int Columns, string FullText)
{
    internal static MarkdownTablePresentation Create(Table table)
    {
        var rows = table.OfType<TableRow>().Select(row =>
            (IReadOnlyList<string>)row.OfType<TableCell>()
                .Select(cell => MarkdownEngine.TableCellText(cell).Replace('\n', ' ').Trim()).ToArray()).ToArray();
        var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Count);
        var text = MarkdownBlock.TableCellsToTsv(rows, columns);
        // The whole table is rendered; the transcript's bounded context is the
        // practical row bound, and the table's own scroll keeps a long one from
        // taking over the reading column.
        return new(rows, columns, text);
    }
}
