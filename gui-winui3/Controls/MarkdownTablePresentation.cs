using Markdig.Extensions.Tables;

namespace RuApp.Controls;

/// <summary>A bounded visual preview and a complete, selectable/copyable table.</summary>
internal sealed record MarkdownTablePresentation(
    IReadOnlyList<IReadOnlyList<string>> Rows, int Columns,
    IReadOnlyList<IReadOnlyList<string>> PreviewRows, int PreviewColumns, string FullText)
{
    internal bool IsTruncated => Rows.Count > PreviewRows.Count || Columns > PreviewColumns;
    internal string Notice => $"Showing {Math.Max(0, PreviewRows.Count - 1)} of {Math.Max(0, Rows.Count - 1)} rows · {PreviewColumns} of {Columns} columns";

    internal static MarkdownTablePresentation Create(Table table)
    {
        var rows = table.OfType<TableRow>().Select(row =>
            (IReadOnlyList<string>)row.OfType<TableCell>()
                .Select(cell => MarkdownEngine.TableCellText(cell).Replace('\n', ' ').Trim()).ToArray()).ToArray();
        var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Count);
        var text = MarkdownBlock.TableCellsToTsv(rows, columns);
        return new(rows, columns, rows.Take(50).ToArray(), Math.Min(columns, 8), text);
    }
}
