using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RuApp.Services;

/// <summary>Named failure while extracting a document attachment.</summary>
public sealed class AttachmentExtractionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Extracts readable text from the document kinds a chat can attach. Everything is
/// bounded: compressed entries, page count, extracted characters. The document is
/// untrusted input; nothing is rendered or executed.
/// </summary>
internal static class AttachmentDocumentExtractor
{
    /// <summary>
    /// Extracted characters kept per document: half the run prompt bound
    /// (<see cref="RuAgent.Protocol.Harness.AgentRunLimits.MaxPromptChars"/>), so one
    /// long document still leaves room for the message and a second attachment.
    /// </summary>
    internal const int MaxExtractedChars = RuAgent.Protocol.Harness.AgentRunLimits.MaxPromptChars / 2;
    private const int MaxPdfPages = 200;
    private const int MaxCompressedEntryBytes = 32 * 1024 * 1024;
    private const int MaxArchiveEntries = 4_096;

    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal static bool IsSupportedDocument(string name) => Extension(name) is ".pdf" or ".docx" or ".pptx" or ".xlsx";

    internal static bool IsDocumentExtension(string extension) =>
        extension is ".pdf" or ".docx" or ".pptx" or ".xlsx";

    internal static string Extract(string name, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Extension(name);
        try
        {
            var text = extension switch
            {
                ".pdf" => ExtractPdf(bytes, cancellationToken),
                ".docx" => ExtractWord(bytes, cancellationToken),
                ".pptx" => ExtractSlides(bytes, cancellationToken),
                ".xlsx" => ExtractWorkbook(bytes, cancellationToken),
                _ => throw new AttachmentExtractionException("document_unsupported", "This document type cannot be read."),
            };

            return Limit(text.Trim(), cancellationToken);
        }
        catch (AttachmentExtractionException)
        {
            throw;
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new AttachmentExtractionException(
                "document_unreadable",
                "The document could not be read. It may be damaged, encrypted or unsupported.");
        }
    }

    private static string Limit(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            throw new AttachmentExtractionException(
                "document_no_text",
                "The document contains no extractable text (it may be a scan or only images).");
        }

        if (text.Length <= MaxExtractedChars)
        {
            return text;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return text[..MaxExtractedChars]
            + $"\n\n[Extracted text truncated at {MaxExtractedChars:N0} characters. The document is longer.]";
    }

    private static string ExtractPdf(byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
        if (document.IsEncrypted)
        {
            throw new AttachmentExtractionException("document_encrypted", "This PDF is encrypted and cannot be read.");
        }

        var builder = new StringBuilder();
        var pages = Math.Min(document.NumberOfPages, MaxPdfPages);
        for (var number = 1; number <= pages && builder.Length <= MaxExtractedChars; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = ContentOrderTextExtractor.GetText(document.GetPage(number));
            if (string.IsNullOrWhiteSpace(page))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(page.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
        }

        return builder.ToString();
    }

    private static string ExtractWord(byte[] bytes, CancellationToken cancellationToken)
    {
        using var archive = OpenArchive(bytes);
        var entry = FindEntry(archive, "word/document.xml")
            ?? throw new AttachmentExtractionException("document_unreadable", "The Word document has no readable body.");
        var document = LoadXml(entry, cancellationToken);
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(Word + "p"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Concat(paragraph.Descendants(Word + "t").Select(node => node.Value));
            if (text.Length > 0)
            {
                builder.AppendLine(text);
            }
        }

        return builder.ToString();
    }

    private static string ExtractSlides(byte[] bytes, CancellationToken cancellationToken)
    {
        using var archive = OpenArchive(bytes);
        var slides = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => ExtractTrailingNumber(name))
            .Take(MaxArchiveEntries)
            .ToArray();
        if (slides.Length == 0)
        {
            throw new AttachmentExtractionException("document_no_text", "The presentation contains no readable slides.");
        }

        var builder = new StringBuilder();
        foreach (var name in slides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slide = LoadXml(FindEntry(archive, name)!, cancellationToken);
            var text = string.Join(" ", slide.Descendants(Drawing + "t").Select(node => node.Value));
            if (text.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.AppendLine(text.Trim());
            }

            if (builder.Length > MaxExtractedChars)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string ExtractWorkbook(byte[] bytes, CancellationToken cancellationToken)
    {
        using var archive = OpenArchive(bytes);
        var shared = new List<string>();
        if (FindEntry(archive, "xl/sharedStrings.xml") is { } sharedEntry)
        {
            var document = LoadXml(sharedEntry, cancellationToken);
            shared.AddRange(document.Descendants(Spreadsheet + "si")
                .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(node => node.Value))));
        }

        var sheets = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => name.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => ExtractTrailingNumber(name))
            .Take(MaxArchiveEntries)
            .ToArray();
        if (sheets.Length == 0)
        {
            throw new AttachmentExtractionException("document_no_text", "The workbook contains no readable sheets.");
        }

        var builder = new StringBuilder();
        foreach (var name in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = LoadXml(FindEntry(archive, name)!, cancellationToken);
            builder.AppendLine($"[Sheet: {Path.GetFileNameWithoutExtension(name)}]");
            foreach (var row in sheet.Descendants(Spreadsheet + "row"))
            {
                var cells = new List<string>();
                foreach (var cell in row.Descendants(Spreadsheet + "c"))
                {
                    var value = CellText(cell, shared);
                    cells.Add(value);
                }

                var line = string.Join('\t', cells).TrimEnd('\t');
                if (line.Length > 0)
                {
                    builder.AppendLine(line);
                }
            }

            if (builder.Length > MaxExtractedChars)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string CellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(node => node.Value));
        }

        var value = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;
        if (string.Equals(type, "s", StringComparison.Ordinal)
            && int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < shared.Count)
        {
            return shared[index];
        }

        return value;
    }

    private static ZipArchive OpenArchive(byte[] bytes) => new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static XDocument LoadXml(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaxCompressedEntryBytes)
        {
            throw new AttachmentExtractionException("document_too_large", "A document part is too large to read.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCompressedEntryBytes,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static int ExtractTrailingNumber(string name)
    {
        var digits = new string(name.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static string Extension(string name) => Path.GetExtension(name).ToLowerInvariant();
}
