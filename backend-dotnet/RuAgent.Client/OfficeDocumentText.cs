using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RuAgent.Client;

/// <summary>
/// Extracts readable text from OOXML office documents (.docx / .pptx / .xlsx) so a
/// picked file can be inlined into a turn like a text attachment. The package parts are
/// read through Microsoft's DocumentFormat.OpenXml SDK; only the text body is kept --
/// styles, numbering, relationships and media are skipped, because the model wants the
/// words, not the layout.
/// </summary>
/// <remarks>
/// Malformed input is a returned value, not an exception: a file the user picked must not
/// take the composer down. A zip-hostile container is refused before the SDK opens it: the
/// entry count is capped and every XML or relationship entry must inflate within the part
/// bound, so a compression bomb cannot exhaust memory. The SDK's readers prohibit DTDs, so
/// an external entity is refused rather than resolved.
/// </remarks>
public static class OfficeDocumentText
{
    public const int MaxTextChars = 400_000;
    private const long MaxEntryBytes = 16L * 1024 * 1024;
    private const int MaxEntries = 4_096;
    private const int MaxSlides = 200;
    private const int MaxSheets = 200;
    private const int MaxBlockDepth = 32;
    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>Why an extraction produced no text, when it did not succeed.</summary>
    public enum Result
    {
        /// <summary>Text was produced.</summary>
        Ok,

        /// <summary>The file is not a readable OOXML package, or a part is not valid XML.</summary>
        Malformed,

        /// <summary>No text-bearing part was found: an empty document, or a part we do not read.</summary>
        NoContent,

        /// <summary>A bound was hit: an oversized part, entry count, or extracted length.</summary>
        TooLarge,
    }

    public readonly record struct Extraction(Result Result, string Text)
    {
        public static Extraction Ok(string text) => new(Result.Ok, text);

        public static Extraction Failed(Result result) => new(result, string.Empty);
    }

    /// <summary>True for the office formats whose text this extractor can read.</summary>
    public static bool IsSupported(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx";

    /// <summary>
    /// Reads the document text, or a bounded reason it could not. The file name selects the
    /// format; a name that is not a supported office document is refused rather than guessed.
    /// </summary>
    public static Extraction Extract(string fileName, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return Extraction.Failed(Result.Malformed);
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".docx" or ".pptx" or ".xlsx"))
        {
            return Extraction.Failed(Result.Malformed);
        }

        try
        {
            using var source = new MemoryStream(bytes.ToArray(), writable: false);
            var envelope = CheckEnvelope(source);
            if (envelope != Result.Ok)
            {
                return Extraction.Failed(envelope);
            }

            source.Position = 0;
            var settings = new OpenSettings { MaxCharactersInPart = (int)MaxEntryBytes };
            return extension switch
            {
                ".docx" => ExtractWord(source, settings),
                ".pptx" => ExtractPresentation(source, settings),
                _ => ExtractSpreadsheet(source, settings),
            };
        }
        catch (Exception ex) when (ex is FileFormatException
            or InvalidDataException
            or XmlException
            or NotSupportedException
            or OpenXmlPackageException
            or KeyNotFoundException
            or InvalidCastException
            or ArgumentException)
        {
            return Extraction.Failed(Result.Malformed);
        }
    }

    /// <summary>
    /// Refuses a zip-hostile container before the SDK opens it. Every XML or relationship
    /// entry is inflated once under a bounded copy, so a lying header cannot smuggle a
    /// compression bomb into the part readers. Media entries are never parsed, so they are
    /// not decompressed here.
    /// </summary>
    private static Result CheckEnvelope(MemoryStream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxEntries)
        {
            return Result.TooLarge;
        }

        var buffer = new byte[CopyBufferBytes];
        foreach (var entry in archive.Entries)
        {
            if (!IsXmlEntry(entry.FullName))
            {
                continue;
            }

            if (entry.Length > MaxEntryBytes || !InflatesWithinBound(entry, buffer))
            {
                return Result.TooLarge;
            }
        }

        return Result.Ok;
    }

    private static bool IsXmlEntry(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static bool InflatesWithinBound(ZipArchiveEntry entry, byte[] buffer)
    {
        using var stream = entry.Open();
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxEntryBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static Extraction ExtractWord(MemoryStream source, OpenSettings settings)
    {
        using var document = WordprocessingDocument.Open(source, false, settings);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return Extraction.Failed(Result.NoContent);
        }

        var builder = new StringBuilder();
        return TryAppendSection(builder, label: null, BodyText(body))
            ? Finish(builder)
            : Extraction.Failed(Result.TooLarge);
    }

    private static Extraction ExtractPresentation(MemoryStream source, OpenSettings settings)
    {
        using var document = PresentationDocument.Open(source, false, settings);
        var presentationPart = document.PresentationPart;
        var slideIds = presentationPart?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
        if (presentationPart is null || slideIds is null)
        {
            return Extraction.Failed(Result.NoContent);
        }

        if (slideIds.Count > MaxSlides)
        {
            return Extraction.Failed(Result.TooLarge);
        }

        var builder = new StringBuilder();
        for (var index = 0; index < slideIds.Count; index++)
        {
            var slide = SlidePart(presentationPart, slideIds[index]);
            if (!TryAppendSection(builder, $"Slide {index + 1}", SlideText(slide?.Slide)))
            {
                return Extraction.Failed(Result.TooLarge);
            }
        }

        return Finish(builder);
    }

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
            if (!TryAppendSection(builder, label, SheetText(worksheetPart?.Worksheet, sharedStrings)))
            {
                return Extraction.Failed(Result.TooLarge);
            }
        }

        return Finish(builder);
    }

    /// <summary>
    /// Appends one text-bearing part the way the previous extractor did: empty parts are
    /// skipped, parts are separated by a blank line, and a slide or sheet carries its label.
    /// The extracted-length bound is checked after every append.
    /// </summary>
    private static bool TryAppendSection(StringBuilder builder, string? label, string text)
    {
        var body = text.TrimEnd();
        if (body.Length == 0)
        {
            return true;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        if (label is not null)
        {
            builder.Append("## ").Append(label).Append('\n');
        }

        builder.Append(body);
        return builder.Length <= MaxTextChars;
    }

    private static Extraction Finish(StringBuilder builder)
    {
        var text = builder.ToString().TrimEnd();
        return text.Length == 0 ? Extraction.Failed(Result.NoContent) : Extraction.Ok(text);
    }

    private static string BodyText(W.Body body)
    {
        var text = new StringBuilder();
        AppendBlocks(text, body, depth: 0);
        return text.ToString();
    }

    /// <summary>
    /// Walks a block container in document order: paragraphs and tables are appended where
    /// they occur, and other containers (content controls, custom XML blocks) are descended
    /// into so their text is not lost. The depth bound keeps a hostile nesting from
    /// exhausting the stack; a file the user picked must not take the composer down.
    /// </summary>
    private static void AppendBlocks(StringBuilder text, OpenXmlElement container, int depth)
    {
        foreach (var child in container.Elements())
        {
            if (child is W.Paragraph paragraph)
            {
                AppendLine(text, ParagraphText(paragraph));
            }
            else if (child is W.Table table)
            {
                AppendTable(text, table);
            }
            else if (depth < MaxBlockDepth && child.HasChildren)
            {
                AppendBlocks(text, child, depth + 1);
            }
        }
    }

    private static string ParagraphText(W.Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case W.Text value:
                    text.Append(value.Text);
                    break;
                case W.Break:
                    text.Append('\n');
                    break;
                case W.TabChar:
                    text.Append('\t');
                    break;
            }
        }

        return text.ToString();
    }

    private static void AppendTable(StringBuilder text, W.Table table)
    {
        foreach (var row in table.Elements<W.TableRow>())
        {
            var cells = row.Elements<W.TableCell>().Select(CellText);
            AppendLine(text, string.Join('\t', cells));
        }
    }

    private static string CellText(W.TableCell cell)
    {
        var text = new StringBuilder();
        AppendBlocks(text, cell, depth: 0);
        return text.ToString().Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// Slides are read in presentation order from the presentation part's slide list, not in
    /// part or zip order: the SDK's part enumeration follows the container, and a deck whose
    /// entries are stored out of order would otherwise be reordered.
    /// </summary>
    private static SlidePart? SlidePart(PresentationPart presentationPart, P.SlideId slideId)
    {
        var relationshipId = slideId.RelationshipId?.Value;
        return string.IsNullOrEmpty(relationshipId)
            ? null
            : presentationPart.Parts
                .FirstOrDefault(pair => pair.RelationshipId == relationshipId)
                .OpenXmlPart as SlidePart;
    }

    private static string SlideText(P.Slide? slide)
    {
        if (slide is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var element in slide.Descendants())
        {
            if (element is P.Shape shape)
            {
                AppendTextBody(text, shape.TextBody);
            }
            else if (element is A.Table table)
            {
                AppendTable(text, table);
            }
        }

        return text.ToString();
    }

    private static void AppendTextBody(StringBuilder text, P.TextBody? textBody)
    {
        if (textBody is null)
        {
            return;
        }

        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            AppendLine(text, DrawingText(paragraph));
        }
    }

    private static string DrawingText(A.Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case A.Text value:
                    text.Append(value.Text);
                    break;
                case A.Break:
                    text.Append('\n');
                    break;
            }
        }

        return text.ToString();
    }

    private static void AppendTable(StringBuilder text, A.Table table)
    {
        foreach (var row in table.Elements<A.TableRow>())
        {
            var cells = row.Elements<A.TableCell>().Select(CellText);
            AppendLine(text, string.Join('\t', cells));
        }
    }

    private static string CellText(A.TableCell cell) =>
        string.Join(' ', cell.Elements<A.TextBody>()
            .SelectMany(body => body.Elements<A.Paragraph>().Select(DrawingText))
            .Where(line => line.Length > 0));

    private static WorksheetPart? WorksheetPart(WorkbookPart workbookPart, S.Sheet sheet)
    {
        var relationshipId = sheet.Id?.Value;
        return string.IsNullOrEmpty(relationshipId)
            ? null
            : workbookPart.Parts
                .FirstOrDefault(pair => pair.RelationshipId == relationshipId)
                .OpenXmlPart as WorksheetPart;
    }

    private static string SheetText(S.Worksheet? worksheet, IReadOnlyList<string>? sharedStrings)
    {
        var sheetData = worksheet?.GetFirstChild<S.SheetData>();
        if (sheetData is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var row in sheetData.Elements<S.Row>())
        {
            var values = row.Elements<S.Cell>().Select(cell => CellText(cell, sharedStrings));
            AppendLine(text, string.Join('\t', values));
        }

        return text.ToString();
    }

    private static string CellText(S.Cell cell, IReadOnlyList<string>? sharedStrings)
    {
        if (cell.DataType?.Value == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString)
        {
            // A shared string that cannot be resolved has no readable content; the index
            // itself must never leak into the prompt as if it were the cell's text.
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && sharedStrings is not null
                && index >= 0
                && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        return value;
    }

    private static IReadOnlyList<string>? SharedStrings(WorkbookPart workbookPart)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        return table is null
            ? null
            : table.Elements<S.SharedStringItem>().Select(item => item.InnerText).ToArray();
    }

    private static void AppendLine(StringBuilder text, string line) => text.Append(line).Append('\n');
}
