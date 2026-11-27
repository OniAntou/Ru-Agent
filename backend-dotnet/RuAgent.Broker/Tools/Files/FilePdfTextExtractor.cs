using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace RuAgent.Broker.Tools.Files;

/// <summary>Bounded text extracted from one PDF page range.</summary>
internal sealed record FilePdfText(
    string? Title,
    string Text,
    int PageCount,
    int FirstPage,
    int LastPage,
    bool MorePages,
    bool TruncatedByCharacters);

/// <summary>
/// Text extraction for a workspace PDF. PDFs are the one binary document format coding agents
/// treat as first-class (Claude Code renders pages, Gemini and OpenCode pass the document);
/// Ru has PdfPig in-process, so the model reads extracted text instead.
/// </summary>
/// <remarks>
/// The bytes are untrusted: PdfPig only parses them, nothing is rendered or executed. Short
/// PDFs are read whole; longer ones by a 1-based page range of at most
/// <see cref="MaxPagesPerRead"/> pages, mirroring the range-read convention other agents use.
/// Character count, page scan and cancellation are bounded so a hostile or enormous document
/// cannot keep the Broker busy.
/// </remarks>
internal static class FilePdfTextExtractor
{
    /// <summary>Pages one call may select, matching the range size Claude Code accepts.</summary>
    public const int MaxPagesPerRead = 100;

    /// <summary>Pages read when the caller names no range and the document is longer.</summary>
    public const int DefaultPagesWhenUnspecified = 10;

    /// <summary>Extracted characters one call returns before it reports truncation.</summary>
    public const int MaxCharacters = 200_000;

    public static FilePdfText Extract(byte[] pdf, string? pagesArgument, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            using var document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
            if (document.IsEncrypted)
            {
                throw new FileReadDocumentException(
                    "pdf_encrypted",
                    "The PDF is encrypted. Ask the user for an unlocked copy or a text export.");
            }

            var pageCount = document.NumberOfPages;
            if (pageCount <= 0)
            {
                throw new FileReadDocumentException("pdf_unreadable", "The PDF has no readable pages.");
            }

            var (firstPage, lastPage, morePages) = SelectRange(pagesArgument, pageCount);
            var text = new StringBuilder();
            var truncatedByCharacters = false;
            for (var pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
                if (string.IsNullOrWhiteSpace(page))
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.Append("\n\n");
                }

                text.Append(page.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
                if (text.Length > MaxCharacters)
                {
                    text.Length = MaxCharacters;
                    truncatedByCharacters = true;
                    break;
                }
            }

            var title = document.Information.Title;
            return new FilePdfText(
                string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                text.ToString(),
                pageCount,
                firstPage,
                lastPage,
                morePages,
                truncatedByCharacters);
        }
        catch (FileReadDocumentException)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException error)
        {
            throw new FileReadDocumentException(
                "pdf_encrypted",
                "The PDF is encrypted. Ask the user for an unlocked copy or a text export.",
                error);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            throw new FileReadDocumentException(
                "pdf_unreadable",
                "The PDF could not be parsed. Ask the user for a text export or a smaller copy.",
                error);
        }
    }

    /// <summary>
    /// Resolves the requested page range against the document. A range is 1-based, either
    /// "5" or "5-12"; an omitted range reads the whole short document or its first pages.
    /// </summary>
    internal static (int FirstPage, int LastPage, bool MorePages) SelectRange(string? pagesArgument, int pageCount)
    {
        if (pageCount <= 0)
        {
            throw new FileReadDocumentException("pdf_unreadable", "The PDF has no readable pages.");
        }

        if (string.IsNullOrWhiteSpace(pagesArgument))
        {
            var lastPage = Math.Min(pageCount, DefaultPagesWhenUnspecified);
            return (1, lastPage, pageCount > lastPage);
        }

        var value = pagesArgument.Trim();
        var separator = value.IndexOf('-', StringComparison.Ordinal);
        int first;
        int last;
        if (separator < 0)
        {
            if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first))
            {
                throw InvalidPages();
            }

            last = first;
        }
        else
        {
            if (!int.TryParse(value[..separator], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out first)
                || !int.TryParse(value[(separator + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out last))
            {
                throw InvalidPages();
            }
        }

        if (last < first)
        {
            throw InvalidPages();
        }

        if (first < 1 || first > pageCount || last > pageCount)
        {
            throw new FileReadDocumentException(
                "pdf_pages_out_of_range",
                $"The PDF has {pageCount} page(s); ask for a 1-based range such as \"1-{Math.Min(pageCount, 10)}\".");
        }

        if (last - first + 1 > MaxPagesPerRead)
        {
            throw new FileReadDocumentException(
                "pdf_pages_too_large",
                $"Read at most {MaxPagesPerRead} pages per call and continue with the next range.");
        }

        return (first, last, last < pageCount);
    }

    private static FileReadDocumentException InvalidPages() => new(
        "invalid_pdf_pages",
        "pages must be a 1-based page number or range, such as \"3\" or \"1-10\".");
}
