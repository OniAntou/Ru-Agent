using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using E = DocumentFormat.OpenXml.ExtendedProperties;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RuApp.Tests;

/// <summary>
/// Builds real OOXML packages with the OpenXml SDK, so the extractor is tested against the
/// actual zip-of-XML shape it will meet rather than a stand-in. Hostile shapes (a DTD, a
/// malformed part, a reordered container) are produced by replacing or reordering entries
/// of an otherwise valid package.
/// </summary>
internal static class OfficeDocumentFixtures
{
    internal const string DocumentXml =
        """
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>First line</w:t></w:r></w:p>
    <w:p><w:r><w:t xml:space="preserve">Second line</w:t></w:r></w:p>
  </w:body>
</w:document>
""";

    internal const string DtdDocumentXml =
        "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>"
        + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
        + "<w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>";

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

            // A text-bearing part the extractor must not read: the model wants the words,
            // not the application metadata.
            var extended = document.AddExtendedFilePropertiesPart();
            extended.Properties = new E.Properties(new E.Application("styles"));
        }

        var bytes = stream.ToArray();
        return documentXml is null ? bytes : ReplaceEntry(bytes, "word/document.xml", documentXml);
    }

    /// <summary>A table-heavy document with unicode text, in one valid package.</summary>
    internal static byte[] DocxWithTable()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Before table"))),
                new W.Table(
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("A1")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("B1"))))),
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("A2")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("B2")))))),
                new W.Paragraph(new W.Run(new W.Text("Tiếng Việt: đồ ăn, phở, xe máy")))));
        }

        return stream.ToArray();
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

    /// <summary>Two slides, so ordering and slide labels are observable.</summary>
    internal static byte[] PptxTwoSlides() => Pptx("One", "Two");

    /// <summary>A slide whose text lives in a drawing table rather than a text frame.</summary>
    internal static byte[] PptxWithTable()
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = presentationPart.Presentation.AppendChild(new P.SlideIdList());

            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
                new P.GraphicFrame(
                    new A.Graphic(
                        new A.GraphicData(
                            new A.Table(
                                new A.TableRow(
                                    new A.TableCell(new A.TextBody(new A.Paragraph(new A.Run(new A.Text("T1"))))),
                                    new A.TableCell(new A.TextBody(new A.Paragraph(new A.Run(new A.Text("T2"))))))))
                        {
                            Uri = "http://schemas.openxmlformats.org/drawingml/2006/table",
                        })))));
            slideIdList.Append(new P.SlideId
            {
                Id = 256U,
                RelationshipId = presentationPart.GetIdOfPart(slidePart),
            });
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The regression shape: slide2.xml is stored before slide1.xml in the container, while
    /// the presentation lists slide1 first. Zip order must not reorder the deck.
    /// </summary>
    internal static byte[] PptxWithReorderedZipEntries() =>
        ReorderZipEntries(
            PptxTwoSlides(),
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
                // A date cell stores its serial number; the value must survive extraction.
                new Row(new Cell { DataType = CellValues.Number, CellValue = new CellValue("45123") }),
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("0") })));
            sheets.Append(new Sheet { Name = "First", SheetId = 1U, Id = workbookPart.GetIdOfPart(first) });

            var second = workbookPart.AddNewPart<WorksheetPart>();
            second.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("inline two")) })));
            sheets.Append(new Sheet { Name = "Second", SheetId = 2U, Id = workbookPart.GetIdOfPart(second) });
        }

        return stream.ToArray();
    }

    internal static byte[] SparseXlsx() => XlsxWithCells(
        new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("Name")) },
        new Cell { CellReference = "C1", DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("Email")) });

    internal static byte[] XlsxWithAmplifiedSharedString(int references, int valueLength)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var shared = workbookPart.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(new SharedStringItem(new S.Text(new string('x', valueLength))));
            var worksheet = workbookPart.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(new Row(
                Enumerable.Range(1, references).Select(index => new Cell
                {
                    CellReference = $"A{index}",
                    DataType = CellValues.SharedString,
                    CellValue = new CellValue("0"),
                }))));
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Name = "Amplified",
                SheetId = 1U,
                Id = workbookPart.GetIdOfPart(worksheet),
            });
        }

        return stream.ToArray();
    }

    private static byte[] XlsxWithCells(params Cell[] cells)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var worksheet = workbookPart.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(new Row(cells)));
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Name = "Sparse",
                SheetId = 1U,
                Id = workbookPart.GetIdOfPart(worksheet),
            });
        }

        return stream.ToArray();
    }

    internal static byte[] DocxWithNestedTables(int levels)
    {
        var nested = "<w:p><w:r><w:t>leaf</w:t></w:r></w:p>";
        for (var index = 0; index < levels; index++)
        {
            nested = $"<w:tbl><w:tr><w:tc>{nested}</w:tc></w:tr></w:tbl>";
        }

        return Docx($"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{nested}</w:body></w:document>");
    }

    internal static byte[] PptxWithLargeText(bool useTable, int runCount, int runLength)
    {
        var paragraph = new A.Paragraph();
        for (var index = 0; index < runCount; index++)
        {
            paragraph.AppendChild(new A.Run(new A.Text(new string('x', runLength))));
        }

        var shapeTree = new P.ShapeTree();
        if (useTable)
        {
            var table = new A.Table();
            var row = new A.TableRow();
            row.AppendChild(new A.TableCell(new A.TextBody(paragraph)));
            table.AppendChild(row);
            shapeTree.AppendChild(new P.GraphicFrame(new A.Graphic(new A.GraphicData(table)
            {
                Uri = "http://schemas.openxmlformats.org/drawingml/2006/table",
            })));
        }
        else
        {
            shapeTree.AppendChild(new P.Shape(new P.TextBody(paragraph)));
        }

        return PptxWithShapeTree(shapeTree);
    }

    internal static byte[] XlsxWithLargeRichString(bool useSharedString, int runCount, int runLength)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new Sheets());
            var cell = new Cell { CellReference = "A1" };
            if (useSharedString)
            {
                var tablePart = workbookPart.AddNewPart<SharedStringTablePart>();
                var item = new SharedStringItem();
                for (var index = 0; index < runCount; index++)
                {
                    item.AppendChild(new S.Run(new S.Text(new string('x', runLength))));
                }

                tablePart.SharedStringTable = new SharedStringTable(item);
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue("0");
            }
            else
            {
                var inline = new InlineString();
                for (var index = 0; index < runCount; index++)
                {
                    inline.AppendChild(new S.Run(new S.Text(new string('x', runLength))));
                }

                cell.DataType = CellValues.InlineString;
                cell.InlineString = inline;
            }

            var worksheet = workbookPart.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(new Row(cell)));
            workbookPart.Workbook.Sheets!.Append(new Sheet
            {
                Name = "LargeText",
                SheetId = 1U,
                Id = workbookPart.GetIdOfPart(worksheet),
            });
        }

        return stream.ToArray();
    }

    private static byte[] PptxWithShapeTree(P.ShapeTree shapeTree)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = presentationPart.Presentation.AppendChild(new P.SlideIdList());
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
            slideIdList.Append(new P.SlideId
            {
                Id = 256U,
                RelationshipId = presentationPart.GetIdOfPart(slidePart),
            });
        }

        return stream.ToArray();
    }

    /// <summary>A renamed text file: the case that must fail closed, not be read as text.</summary>
    internal static byte[] NotAZip() => Encoding.UTF8.GetBytes("plain text pretending to be a docx");

    /// <summary>A valid container whose only text part is empty.</summary>
    internal static byte[] EmptyDocumentPart() =>
        Docx("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");

    /// <summary>A valid package whose main part carries a DTD, which must be refused rather than resolved.</summary>
    internal static byte[] DocumentWithDtd() => Docx(DtdDocumentXml);

    internal static byte[] Pack(params (string Name, string Body)[] parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in parts)
            {
                using var stream = archive.CreateEntry(name).Open();
                var payload = Encoding.UTF8.GetBytes(body);
                stream.Write(payload, 0, payload.Length);
            }
        }

        return buffer.ToArray();
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
