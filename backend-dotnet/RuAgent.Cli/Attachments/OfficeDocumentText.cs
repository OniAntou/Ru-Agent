using System.IO.Compression;
using System.Text;
using System.Xml;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// Extracts readable text from OOXML office documents (.docx / .pptx / .xlsx) so a
/// picked file can be inlined into a turn like a text attachment. Ported from the WinUI
/// surface; every archive operation is bounded and a crafted entry name is never used as
/// a path (parts are matched by suffix against a fixed set).
/// </summary>
public static class OfficeDocumentText
{
    public const int MaxTextChars = 400_000;
    private const long MaxEntryBytes = 16L * 1024 * 1024;
    private const int MaxEntries = 4_096;
    private const int MaxSlides = 200;

    public enum Result
    {
        /// <summary>Text was produced.</summary>
        Ok,

        /// <summary>The file is not a readable zip container, or a part is not valid XML.</summary>
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
    /// Reads the document text, or a bounded reason it could not. Malformed input is a
    /// returned value, not an exception.
    /// </summary>
    public static Extraction Extract(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return Extraction.Failed(Result.Malformed);
        }

        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
            return ExtractFrom(archive);
        }
        catch (InvalidDataException)
        {
            return Extraction.Failed(Result.Malformed);
        }
        catch (NotSupportedException)
        {
            return Extraction.Failed(Result.Malformed);
        }
    }

    private static Extraction ExtractFrom(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            return Extraction.Failed(Result.TooLarge);
        }

        var builder = new StringBuilder();
        var slides = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsTextPart(entry.FullName))
            {
                continue;
            }

            if (entry.FullName.Contains("/slides/slide", StringComparison.OrdinalIgnoreCase)
                && ++slides > MaxSlides)
            {
                return Extraction.Failed(Result.TooLarge);
            }

            var part = ReadText(entry, out var tooLarge);
            if (tooLarge)
            {
                return Extraction.Failed(Result.TooLarge);
            }

            AppendPart(builder, entry.FullName, part);
            if (builder.Length > MaxTextChars)
            {
                return Extraction.Failed(Result.TooLarge);
            }
        }

        return builder.Length == 0
            ? Extraction.Failed(Result.NoContent)
            : Extraction.Ok(builder.ToString());
    }

    /// <summary>
    /// The only parts that carry readable words. Matching is on the container's part
    /// name, never on a filesystem path.
    /// </summary>
    private static bool IsTextPart(string name) =>
        name.EndsWith("/document.xml", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("/notesSlide.xml", StringComparison.OrdinalIgnoreCase)
        || (name.Contains("/slides/slide", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        || name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase);

    private static string? ReadText(ZipArchiveEntry entry, out bool tooLarge)
    {
        tooLarge = false;
        if (entry.Length > MaxEntryBytes)
        {
            tooLarge = true;
            return null;
        }

        try
        {
            using var stream = entry.Open();
            // Bounded copy: a part that inflates past the cap is refused rather than
            // trusted, so a compression bomb cannot exhaust memory.
            var buffer = new byte[MaxEntryBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > MaxEntryBytes)
            {
                tooLarge = true;
                return null;
            }

            using var part = new MemoryStream(buffer, 0, total, writable: false);
            using var reader = XmlReader.Create(part, new XmlReaderSettings
            {
                // An office part must never resolve anything external; a document that
                // carries a DTD is refused, not fetched.
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
            });
            return ReadVisibleText(reader);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Concatenates the visible text nodes of a part. OOXML marks run text with
    /// <c>w:t</c>, <c>a:t</c> and <c>t</c> depending on the format, so the element is
    /// matched by local name <c>t</c> whatever its namespace; a paragraph, row or cell
    /// break inserts a newline where the markup ends one.
    /// </summary>
    private static string ReadVisibleText(XmlReader reader)
    {
        var text = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (IsBreakElement(reader.LocalName))
            {
                text.Append('\n');
                continue;
            }

            if (reader.LocalName == "t" && !reader.IsEmptyElement)
            {
                var value = reader.ReadElementContentAsString();
                if (!string.IsNullOrEmpty(value))
                {
                    text.Append(value);
                }
            }
        }

        return text.ToString();
    }

    private static bool IsBreakElement(string localName) =>
        localName is "p" or "br" or "tr" or "tc";

    private static void AppendPart(StringBuilder builder, string partName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        if (partName.Contains("/slides/slide", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("## ").Append(SlideLabel(partName)).Append('\n');
        }

        builder.Append(text.TrimEnd());
    }

    /// <summary>slide2 before slide10: the number is parsed, not compared as text.</summary>
    private static string SlideLabel(string partName)
    {
        var start = partName.LastIndexOf("slide", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "Slide";
        }

        var tail = partName[(start + 5)..];
        var dot = tail.IndexOf('.');
        if (dot >= 0)
        {
            tail = tail[..dot];
        }

        return int.TryParse(tail, out var number) ? $"Slide {number}" : "Slide";
    }
}
