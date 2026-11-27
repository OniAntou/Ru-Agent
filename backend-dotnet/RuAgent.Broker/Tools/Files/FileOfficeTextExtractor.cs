using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RuAgent.Broker.Tools.Files;

/// <summary>Bounded text extracted from one part range of an Office or OpenDocument file.</summary>
internal sealed record FileOfficeText(
    string Format,
    string Unit,
    string Text,
    int PartCount,
    int FirstPart,
    int LastPart,
    bool MoreParts,
    bool TruncatedByCharacters);

/// <summary>
/// Text extraction for Office Open XML (.docx, .xlsx, .pptx and their macro/template
/// variants) and OpenDocument (.odt, .ods, .odp) files (ADR 0053). Both are ZIP archives of
/// XML, so extraction needs no parser beyond the BCL: no new dependency, nothing rendered or
/// executed.
/// </summary>
/// <remarks>
/// A document is a sequence of parts in reading order: paragraphs of a word-processing
/// file, slides of a presentation, rows of a spreadsheet (each sheet's first row carries
/// the sheet name). <c>pages</c> selects a 1-based part range, so a long document is read
/// on from where the character cap stopped, the way a PDF is read by page range.
/// The bytes are untrusted: DTDs are prohibited, every XML part and the number of archive
/// entries are bounded, and the caller's token carries the parse deadline.
/// </remarks>
internal static class FileOfficeTextExtractor
{
    /// <summary>Extracted characters one call returns, the same cap as a PDF page range.</summary>
    public const int MaxCharacters = FilePdfTextExtractor.MaxCharacters;

    /// <summary>
    /// One XML part's bound. XDocument.Load cannot be cancelled mid-part, so the parse
    /// deadline is checked between parts; this bound keeps a single part's load short.
    /// </summary>
    private const int MaxXmlPartCharacters = 256 * 1024 * 1024;
    private const int MaxArchiveEntries = 65_536;

    /// <summary>Empty columns a sparse row may pad before a cell, so columns stay aligned.</summary>
    private const int MaxColumnGap = 64;

    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace OdfDrawing = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

    /// <summary>Whether <paramref name="extension"/> (with its dot) is a format this extracts.</summary>
    public static bool IsSupported(string extension) => FormatOf(extension) is not null;

    public static FileOfficeText Extract(byte[] bytes, string extension, string? pagesArgument, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var format = FormatOf(extension) ?? throw new ArgumentException("unsupported_document_extension", nameof(extension));
        var (first, last) = ParseRange(pagesArgument);
        var collector = new PartCollector(first, last, cancellationToken);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new FileReadDocumentException(
                    "document_too_large",
                    "The document has too many internal parts to read. Ask the user for a PDF or text export.");
            }

            var unit = format.Kind switch
            {
                "docx" => ReadWordprocessing(archive, collector),
                "pptx" => ReadPresentation(archive, collector),
                "xlsx" => ReadWorkbook(archive, collector),
                "odt" => ReadOdfText(archive, collector),
                "odp" => ReadOdfPresentation(archive, collector),
                _ => ReadOdfSpreadsheet(archive, collector),
            };
            return collector.Finish(format.Name, unit);
        }
        catch (FileReadDocumentException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidDataException && LooksLikeCompoundFile(bytes))
        {
            // A password-protected .docx/.xlsx/.pptx is an OLE compound file, not a ZIP.
            throw new FileReadDocumentException(
                "document_encrypted",
                "The document is password-protected (or a legacy binary file renamed). Ask the user for an unlocked copy, a PDF or a text export.",
                error);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new FileReadDocumentException(
                "document_unreadable",
                "The document could not be parsed; it may be damaged. Ask the user for a PDF or text export, or inspect it with a shell command.",
                error);
        }
    }

    private static string ReadWordprocessing(ZipArchive archive, PartCollector collector)
    {
        var body = LoadRequired(archive, "word/document.xml", collector.Token);
        foreach (var paragraph in body.Descendants(Word + "p"))
        {
            collector.Add(string.Concat(paragraph.Descendants(Word + "t").Select(node => node.Value)));
        }

        return "paragraph";
    }

    private static string ReadPresentation(ZipArchive archive, PartCollector collector)
    {
        foreach (var (name, number) in NumberedEntries(archive, "ppt/slides/slide"))
        {
            var slide = Load(archive.GetEntry(name)!, collector.Token);
            var text = string.Join(" ", slide.Descendants(Drawing + "t").Select(node => node.Value)).Trim();
            collector.Add($"[Slide {number}]\n{text}", countEmpty: true);
        }

        return "slide";
    }

    private static string ReadWorkbook(ZipArchive archive, PartCollector collector)
    {
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            shared.AddRange(Load(sharedEntry, collector.Token).Descendants(Spreadsheet + "si")
                .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(node => node.Value))));
        }

        foreach (var (entryName, label) in WorkbookSheets(archive, collector.Token))
        {
            var sheet = Load(archive.GetEntry(entryName)!, collector.Token);
            var header = $"[Sheet: {label}]\n";
            foreach (var row in sheet.Descendants(Spreadsheet + "row"))
            {
                var line = RowText(row, shared);
                if (line.Length > 0 && collector.Add(header + line))
                {
                    header = string.Empty;
                }
            }
        }

        return "row";
    }

    private static string ReadOdfText(ZipArchive archive, PartCollector collector)
    {
        var content = LoadRequired(archive, "content.xml", collector.Token);
        foreach (var paragraph in content.Descendants().Where(node => node.Name == OdfText + "p" || node.Name == OdfText + "h"))
        {
            collector.Add(paragraph.Value);
        }

        return "paragraph";
    }

    private static string ReadOdfPresentation(ZipArchive archive, PartCollector collector)
    {
        var content = LoadRequired(archive, "content.xml", collector.Token);
        var number = 0;
        foreach (var page in content.Descendants(OdfDrawing + "page"))
        {
            number++;
            var text = string.Join(" ", page.Descendants(OdfText + "p").Select(node => node.Value)).Trim();
            collector.Add($"[Slide {number}]\n{text}", countEmpty: true);
        }

        return "slide";
    }

    private static string ReadOdfSpreadsheet(ZipArchive archive, PartCollector collector)
    {
        var content = LoadRequired(archive, "content.xml", collector.Token);
        foreach (var table in content.Descendants(OdfTable + "table"))
        {
            var header = $"[Sheet: {table.Attribute(OdfTable + "name")?.Value ?? "Sheet"}]\n";
            foreach (var row in table.Descendants(OdfTable + "table-row"))
            {
                var line = string.Join('\t', row.Elements(OdfTable + "table-cell")
                    .Select(cell => string.Join(" ", cell.Elements(OdfText + "p").Select(node => node.Value)))).TrimEnd('\t');
                if (line.Length > 0 && collector.Add(header + line))
                {
                    header = string.Empty;
                }
            }
        }

        return "row";
    }

    /// <summary>
    /// Sheets in workbook order with their names, resolved through the workbook's
    /// relationships (a sheet's file number need not match its position). A workbook
    /// without them falls back to <c>sheetN.xml</c> numbering.
    /// </summary>
    private static List<(string Entry, string Label)> WorkbookSheets(ZipArchive archive, CancellationToken cancellationToken)
    {
        var sheets = new List<(string Entry, string Label)>();
        var declared = archive.GetEntry("xl/workbook.xml") is { } workbook
            ? Load(workbook, cancellationToken).Descendants(Spreadsheet + "sheet").ToArray()
            : [];
        if (declared.Length > 0 && archive.GetEntry("xl/_rels/workbook.xml.rels") is { } relationships)
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relationship in Load(relationships, cancellationToken).Descendants(PackageRelationships + "Relationship"))
            {
                if (relationship.Attribute("Id")?.Value is { } id && relationship.Attribute("Target")?.Value is { } target)
                {
                    targets.TryAdd(id, target);
                }
            }

            foreach (var sheet in declared)
            {
                if (sheet.Attribute(OfficeRelationships + "id")?.Value is not { } id || !targets.TryGetValue(id, out var target))
                {
                    continue;
                }

                var entry = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                if (archive.GetEntry(entry) is null)
                {
                    continue;
                }

                var name = sheet.Attribute("name")?.Value;
                sheets.Add((entry, string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(entry) : name));
            }
        }

        if (sheets.Count == 0)
        {
            foreach (var (entry, number) in NumberedEntries(archive, "xl/worksheets/sheet"))
            {
                var name = number - 1 < declared.Length ? declared[number - 1].Attribute("name")?.Value : null;
                sheets.Add((entry, string.IsNullOrEmpty(name) ? $"Sheet{number}" : name));
            }
        }

        return sheets;
    }

    /// <summary>
    /// A row's cells, tab-separated, placed by their reference (<c>r="D5"</c>) so a sparse
    /// row keeps its columns; a gap pads at most <see cref="MaxColumnGap"/> empty columns.
    /// </summary>
    private static string RowText(XElement row, IReadOnlyList<string> shared)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            if (ColumnIndex(cell.Attribute("r")?.Value) is { } index)
            {
                var padding = 0;
                while (cells.Count < index && padding++ < MaxColumnGap)
                {
                    cells.Add(string.Empty);
                }
            }

            cells.Add(CellText(cell, shared));
        }

        return string.Join('\t', cells).TrimEnd('\t');
    }

    /// <summary>The 0-based column of a cell reference such as "D5", or null.</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z' && letters < 3)
            {
                column = (column * 26) + (character - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters == 0 ? null : column - 1;
    }

    private static string CellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(node => node.Value));
        }

        var value = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
        return type == "s"
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index < shared.Count
                ? shared[index]
                : value;
    }

    /// <summary>Entries named <c>{prefix}{n}.xml</c>, in numeric order.</summary>
    private static IEnumerable<(string Name, int Number)> NumberedEntries(ZipArchive archive, string prefix) =>
        archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Number: int.TryParse(
                name.AsSpan(prefix.Length, name.Length - prefix.Length - 4),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) ? number : int.MaxValue))
            .Where(item => item.Number != int.MaxValue)
            .OrderBy(item => item.Number)
            .ToArray();

    private static XDocument LoadRequired(ZipArchive archive, string name, CancellationToken cancellationToken) =>
        archive.GetEntry(name) is { } entry
            ? Load(entry, cancellationToken)
            : throw new FileReadDocumentException(
                "document_unreadable",
                $"The document has no {name} part; it may be damaged or a different format. Ask the user for a PDF or text export.");

    private static XDocument Load(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Length > MaxXmlPartCharacters)
        {
            throw new FileReadDocumentException(
                "document_too_large",
                "A part of the document is too large to read. Ask the user for a PDF or text export.");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlPartCharacters,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool LooksLikeCompoundFile(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

    private static (string Name, string Kind)? FormatOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" or ".docm" or ".dotx" or ".dotm" => (extension.TrimStart('.').ToLowerInvariant(), "docx"),
        ".pptx" or ".pptm" or ".potx" or ".potm" or ".ppsx" => (extension.TrimStart('.').ToLowerInvariant(), "pptx"),
        ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => (extension.TrimStart('.').ToLowerInvariant(), "xlsx"),
        ".odt" or ".ott" => ("odt", "odt"),
        ".odp" or ".otp" => ("odp", "odp"),
        ".ods" or ".ots" => ("ods", "ods"),
        _ => null,
    };

    /// <summary>A 1-based part range: "5" or "5-120". No range reads from the start.</summary>
    private static (int First, int Last) ParseRange(string? pagesArgument)
    {
        if (string.IsNullOrWhiteSpace(pagesArgument))
        {
            return (1, int.MaxValue);
        }

        var value = pagesArgument.Trim();
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        var firstText = separator < 0 ? value : value[..separator];
        var lastText = separator < 0 ? value : value[(separator + 1)..];
        if (!int.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || !int.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || first < 1
            || last < first)
        {
            throw new FileReadDocumentException(
                "invalid_document_pages",
                "For a document, pages selects a 1-based range of its parts (paragraphs, slides or rows), such as \"3\" or \"120-400\".");
        }

        return (first, last);
    }

    /// <summary>
    /// Counts every part and keeps the text of the selected range until the character cap.
    /// Parts past the range are still counted so the result can say how many remain.
    /// </summary>
    private sealed class PartCollector(int first, int last, CancellationToken token)
    {
        private readonly StringBuilder _text = new();
        private int _count;
        private int _lastKept;
        private bool _truncated;

        public CancellationToken Token { get; } = token;

        /// <summary>Adds one part; returns whether it was counted (empty parts are skipped).</summary>
        public bool Add(string part, bool countEmpty = false)
        {
            Token.ThrowIfCancellationRequested();
            part = part.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (part.Length == 0 && !countEmpty)
            {
                return false;
            }

            _count++;
            if (_count < first || _count > last || _truncated)
            {
                return true;
            }

            if (_text.Length > 0)
            {
                _text.Append('\n');
            }

            _text.Append(part);
            _lastKept = _count;
            if (_text.Length > MaxCharacters)
            {
                _text.Length = MaxCharacters;
                _truncated = true;
            }

            return true;
        }

        public FileOfficeText Finish(string format, string unit)
        {
            if (_count == 0)
            {
                return new FileOfficeText(format, unit, string.Empty, 0, 0, 0, false, false);
            }

            if (first > _count)
            {
                throw new FileReadDocumentException(
                    "document_pages_out_of_range",
                    $"The document has {_count} {unit}(s); ask for a 1-based range within 1-{_count}.");
            }

            return new FileOfficeText(format, unit, _text.ToString(), _count, first, _lastKept, _lastKept < _count, _truncated);
        }
    }
}
