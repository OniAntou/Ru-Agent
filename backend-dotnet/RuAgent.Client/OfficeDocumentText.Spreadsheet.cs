using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace RuAgent.Client;

public static partial class OfficeDocumentText
{
    private static Extraction ExtractSpreadsheet(MemoryStream source, OpenSettings settings)
    {
        using var document = SpreadsheetDocument.Open(source, false, settings);
        var workbookPart = document.WorkbookPart;
        var sheets = workbookPart?.Workbook?.Sheets?.Elements<S.Sheet>().ToList();
        if (workbookPart is null || sheets is null)
        {
            return Extraction.Failed(Result.NoContent);
        }

        if (sheets.Count > MaxSheets)
        {
            return Extraction.Failed(Result.TooLarge);
        }

        var sharedStrings = SharedStrings(workbookPart);
        var builder = new StringBuilder();
        foreach (var sheet in sheets)
        {
            var worksheetPart = WorksheetPart(workbookPart, sheet);
            var label = $"Sheet: {sheet.Name?.Value ?? "Sheet"}";
            if (!TryBuildSheet(builder, label, worksheetPart?.Worksheet, sharedStrings, out var sheetText)
                || !TryAppendSection(builder, label, sheetText))
            {
                return Extraction.Failed(Result.TooLarge);
            }
        }

        return Finish(builder);
    }

    private static WorksheetPart? WorksheetPart(WorkbookPart workbookPart, S.Sheet sheet)
    {
        var relationshipId = sheet.Id?.Value;
        return string.IsNullOrEmpty(relationshipId)
            ? null
            : workbookPart.Parts
                .FirstOrDefault(pair => pair.RelationshipId == relationshipId)
                .OpenXmlPart as WorksheetPart;
    }

    private static bool TryBuildSheet(
        StringBuilder document,
        string label,
        S.Worksheet? worksheet,
        IReadOnlyList<S.SharedStringItem>? sharedStrings,
        out string sheetText)
    {
        sheetText = string.Empty;
        var sheetData = worksheet?.GetFirstChild<S.SheetData>();
        if (sheetData is null) return true;

        var text = new StringBuilder();
        foreach (var row in sheetData.Elements<S.Row>())
        {
            var previousColumn = 0;
            var first = true;
            foreach (var cell in row.Elements<S.Cell>())
            {
                var column = CellColumn(cell.CellReference?.Value, previousColumn + 1);
                var separators = first ? Math.Max(0, column - 1) : Math.Max(1, column - previousColumn);
                if (!TryAppendTabs(text, separators, document.Length, label)
                    || !TryAppendCell(text, cell, sharedStrings, document.Length, label))
                {
                    return false;
                }

                previousColumn = column;
                first = false;
            }

            if (!TryAppendBounded(text, "\n", document.Length, label)) return false;
        }

        sheetText = text.ToString();
        return true;
    }

    private static bool TryAppendTabs(StringBuilder text, int count, int documentLength, string label)
    {
        for (var index = 0; index < count; index++)
        {
            if (!TryAppendBounded(text, "\t", documentLength, label)) return false;
        }

        return true;
    }

    private static bool TryAppendCell(
        StringBuilder text,
        S.Cell cell,
        IReadOnlyList<S.SharedStringItem>? sharedStrings,
        int documentLength,
        string label)
    {
        var remaining = SectionTextLimit(documentLength, label) - text.Length;
        if (cell.DataType?.Value == S.CellValues.InlineString)
        {
            return AppendSpreadsheetText(text, cell.InlineString, remaining);
        }

        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && sharedStrings is not null
                && index >= 0
                && index < sharedStrings.Count
                ? AppendSpreadsheetText(text, sharedStrings[index], remaining)
                : true;
        }

        return TryAppendBounded(text, value, documentLength, label);
    }

    private static bool AppendSpreadsheetText(StringBuilder text, OpenXmlElement? container, int remaining)
    {
        if (container is null) return true;
        foreach (var value in container.Descendants<S.Text>())
        {
            var part = value.Text;
            if (part.Length > remaining) return false;
            text.Append(part);
            remaining -= part.Length;
        }

        return true;
    }

    private static bool AppendSpreadsheetText(StringBuilder text, S.SharedStringItem item, int remaining)
    {
        foreach (var value in item.Descendants<S.Text>())
        {
            var part = value.Text;
            if (part.Length > remaining) return false;
            text.Append(part);
            remaining -= part.Length;
        }

        return true;
    }

    private static bool TryAppendBounded(StringBuilder text, string value, int documentLength, string label)
    {
        var remaining = SectionTextLimit(documentLength, label) - text.Length;
        if (value.Length > remaining) return false;
        text.Append(value);
        return true;
    }

    private static int CellColumn(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        var column = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z' && character is < 'a' or > 'z') break;
            var digit = char.ToUpperInvariant(character) - 'A' + 1;
            if (column > MaxTextChars) return MaxTextChars + 1;
            column = (column * 26) + digit;
        }

        return column > 0 ? column : fallback;
    }

    private static IReadOnlyList<S.SharedStringItem>? SharedStrings(WorkbookPart workbookPart)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        return table is null ? null : table.Elements<S.SharedStringItem>().ToArray();
    }

    private static int SectionTextLimit(int documentLength, string label) =>
        MaxTextChars - documentLength - (documentLength == 0 ? 0 : 2) - label.Length - 4;
}
