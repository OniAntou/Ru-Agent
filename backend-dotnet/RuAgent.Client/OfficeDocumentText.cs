using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
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
public static partial class OfficeDocumentText
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
        return AppendBlocks(builder, body, depth: 0, insideCell: false)
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
            var label = $"Slide {index + 1}";
            var slideText = new StringBuilder();
            if (!AppendSlideText(slideText, slide?.Slide, SectionTextLimit(builder.Length, label))
                || !TryAppendSection(builder, label, slideText.ToString()))
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

    /// <summary>
    /// Walks a block container in document order: paragraphs and tables are appended where
    /// they occur, and other containers (content controls, custom XML blocks) are descended
    /// into so their text is not lost. The depth bound keeps a hostile nesting from
    /// exhausting the stack; a file the user picked must not take the composer down.
    /// </summary>
    private static bool AppendBlocks(StringBuilder text, OpenXmlElement container, int depth, bool insideCell)
    {
        foreach (var child in container.Elements())
        {
            if (child is W.Paragraph paragraph)
            {
                if (!AppendWordParagraph(text, paragraph, insideCell) || !TryAppend(text, insideCell ? ' ' : '\n'))
                {
                    return false;
                }
            }
            else if (child is W.Table table)
            {
                if (!AppendTable(text, table, depth + 1, insideCell))
                {
                    return false;
                }
            }
            else if (child.HasChildren)
            {
                if (depth >= MaxBlockDepth || !AppendBlocks(text, child, depth + 1, insideCell))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AppendWordParagraph(StringBuilder text, W.Paragraph paragraph, bool insideCell)
    {
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case W.Text value:
                    if (!TryAppend(text, value.Text)) return false;
                    break;
                case W.Break:
                    if (!TryAppend(text, insideCell ? ' ' : '\n')) return false;
                    break;
                case W.TabChar:
                    if (!TryAppend(text, '\t')) return false;
                    break;
            }
        }

        return true;
    }

    private static bool AppendTable(StringBuilder text, W.Table table, int depth, bool insideCell)
    {
        if (depth > MaxBlockDepth) return false;
        foreach (var row in table.Elements<W.TableRow>())
        {
            var first = true;
            foreach (var cell in row.Elements<W.TableCell>())
            {
                if (!first && !TryAppend(text, '\t')) return false;
                var start = text.Length;
                if (!AppendBlocks(text, cell, depth, insideCell: true)) return false;
                TrimCell(text, start);
                first = false;
            }

            if (!TryAppend(text, insideCell ? ' ' : '\n')) return false;
        }

        return true;
    }

    private static void TrimCell(StringBuilder text, int start)
    {
        while (text.Length > start && char.IsWhiteSpace(text[^1])) text.Length--;
        var contentStart = start;
        while (contentStart < text.Length && char.IsWhiteSpace(text[contentStart])) contentStart++;
        if (contentStart > start) text.Remove(start, contentStart - start);
    }

    private static bool TryAppend(StringBuilder text, string value) =>
        value.Length <= MaxTextChars - text.Length && Append(text, value);

    private static bool Append(StringBuilder text, string value)
    {
        text.Append(value);
        return true;
    }

    private static bool TryAppend(StringBuilder text, char value) =>
        text.Length < MaxTextChars && Append(text, value);

    private static bool Append(StringBuilder text, char value)
    {
        text.Append(value);
        return true;
    }

    private static void AppendLine(StringBuilder text, string line) => text.Append(line).Append('\n');
}
