using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace RuAgent.Broker.Tools.Web;

/// <summary>
/// Reads the text of a downloaded PDF for <c>web_fetch</c>. Papers, specifications and
/// reports are often published only as PDF.
/// </summary>
/// <remarks>
/// The bytes are untrusted: PdfPig only parses them, nothing is rendered or executed.
/// The page count and the extracted length are bounded so a hostile document cannot
/// keep the Broker busy, and the caller's cancellation is checked between pages.
/// </remarks>
internal static class WebPdfTextExtractor
{
    public const int MaxPages = 500;

    public static (string? Title, string Text) Extract(byte[] pdf, int maxCharacters, CancellationToken cancellationToken)
    {
        try
        {
            using var document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
            if (document.IsEncrypted) throw new InvalidDataException("web_pdf_encrypted");
            var text = new StringBuilder();
            var pages = Math.Min(document.NumberOfPages, MaxPages);
            for (var number = 1; number <= pages && text.Length < maxCharacters; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = ContentOrderTextExtractor.GetText(document.GetPage(number));
                if (string.IsNullOrWhiteSpace(page)) continue;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(page.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
            }

            if (text.Length > maxCharacters) text.Length = maxCharacters;
            var title = document.Information.Title;
            return (string.IsNullOrWhiteSpace(title) ? null : title, text.ToString());
        }
        catch (PdfDocumentEncryptedException error)
        {
            throw new InvalidDataException("web_pdf_encrypted", error);
        }
        // A malformed document can fail anywhere inside the parser and with any exception
        // type; all of them mean the same thing to the model.
        catch (Exception error) when (error is not (OperationCanceledException or InvalidDataException or OutOfMemoryException))
        {
            throw new InvalidDataException("web_pdf_unreadable", error);
        }
    }
}
