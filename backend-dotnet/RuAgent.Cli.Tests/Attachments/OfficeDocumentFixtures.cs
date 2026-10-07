using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RuAgent.Cli.Tests.Attachments;

/// <summary>
/// Builds real OOXML packages with the OpenXml SDK, so the extractor is tested against the
/// actual zip-of-XML shape it will meet rather than a stand-in. Hostile shapes (a malformed
/// part, a reordered container) are produced by replacing or reordering entries of an
/// otherwise valid package.
/// </summary>
internal static class OfficeDocumentFixtures
{
    /// <summary>A valid .docx; a caller-supplied body replaces the main document part.</summary>
    internal static byte[] Docx(string? documentXml = null)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("First line"))),
                new W.Paragraph(new W.Run(new W.Text("Second line")))));
        }

        var bytes = stream.ToArray();
        return documentXml is null ? bytes : ReplaceEntry(bytes, "word/document.xml", documentXml);
    }

    /// <summary>A valid deck whose slides carry the given text in presentation order.</summary>
    internal static byte[] Pptx(params string[] slideTexts)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = presentationPart.Presentation.AppendChild(new P.SlideIdList());

            var slideId = 256U;
            foreach (var text in slideTexts)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                    new P.Shape(new P.TextBody(new A.Paragraph(new A.Run(new A.Text(text))))))));
                slideIdList.Append(new P.SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The regression shape: slide2.xml is stored before slide1.xml in the container, while
    /// the presentation lists slide1 first. Zip order must not reorder the deck.
    /// </summary>
    internal static byte[] PptxWithReorderedZipEntries() =>
        ReorderZipEntries(
            Pptx("One", "Two"),
            name => name switch
            {
                "ppt/slides/slide2.xml" => -1,
                "ppt/slides/slide1.xml" => 1,
                _ => 0,
            });

    /// <summary>A valid workbook: shared strings, inline strings, numbers, two ordered sheets.</summary>
    internal static byte[] Xlsx()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            var shared = workbookPart.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(new SharedStringItem(new S.Text("shared text")));

            var first = workbookPart.AddNewPart<WorksheetPart>();
            first.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("inline one")) }),
                new Row(new Cell { DataType = CellValues.Number, CellValue = new CellValue("42") }),
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("0") })));
            sheets.Append(new Sheet { Name = "First", SheetId = 1U, Id = workbookPart.GetIdOfPart(first) });

            var second = workbookPart.AddNewPart<WorksheetPart>();
            second.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("inline two")) })));
            sheets.Append(new Sheet { Name = "Second", SheetId = 2U, Id = workbookPart.GetIdOfPart(second) });
        }

        return stream.ToArray();
    }

    internal static byte[] ReplaceEntry(byte[] zip, string entryName, string body) =>
        ReplaceEntry(zip, entryName, Encoding.UTF8.GetBytes(body));

    internal static byte[] ReplaceEntry(byte[] zip, string entryName, byte[] body)
    {
        using var output = new MemoryStream();
        using (var source = new MemoryStream(zip, writable: false))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries)
            {
                var copy = target.CreateEntry(entry.FullName);
                using var targetStream = copy.Open();
                if (entry.FullName == entryName)
                {
                    targetStream.Write(body, 0, body.Length);
                }
                else
                {
                    using var sourceStream = entry.Open();
                    sourceStream.CopyTo(targetStream);
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] ReorderZipEntries(byte[] zip, System.Func<string, int> rank)
    {
        using var output = new MemoryStream();
        using (var source = new MemoryStream(zip, writable: false))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in archive.Entries.OrderBy(entry => rank(entry.FullName)))
            {
                var copy = target.CreateEntry(entry.FullName);
                using var targetStream = copy.Open();
                using var sourceStream = entry.Open();
                sourceStream.CopyTo(targetStream);
            }
        }

        return output.ToArray();
    }
}
