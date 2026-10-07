using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace OoxmlBenchmark;

/// <summary>
/// The permanent extraction corpus: easy and hard shapes for each format, including the
/// content the hand-rolled extractor used to lose (xlsx inline strings and cell values, a
/// deck whose zip entry order differs from presentation order) plus table-heavy and
/// Vietnamese text. Fixtures are built with the OpenXml SDK so they are real packages.
/// </summary>
internal static class Fixtures
{
    internal static byte[] BuildDocxEasy()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("First line"))),
                new W.Paragraph(new W.Run(new W.Text("Second line")))));
        }

        return stream.ToArray();
    }

    internal static byte[] BuildDocxHard()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(new W.Run(new W.Text("Bảng dữ liệu"))),
                new W.Table(
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Ô 1")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Ô 2"))))),
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Ô 3")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Ô 4")))))),
                new W.Paragraph(new W.Run(new W.Text("Tiếng Việt: đồ ăn, phở, xe máy")))));
        }

        return stream.ToArray();
    }

    internal static byte[] BuildXlsxEasy()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            var shared = workbookPart.AddNewPart<SharedStringTablePart>();
            shared.SharedStringTable = new SharedStringTable(
                new SharedStringItem(new S.Text("shared alpha")),
                new SharedStringItem(new S.Text("shared beta")));

            var worksheet = workbookPart.AddNewPart<WorksheetPart>();
            worksheet.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("0") }),
                new Row(new Cell { DataType = CellValues.SharedString, CellValue = new CellValue("1") })));
            sheets.Append(new Sheet { Name = "Data", SheetId = 1U, Id = workbookPart.GetIdOfPart(worksheet) });
        }

        return stream.ToArray();
    }

    internal static byte[] BuildXlsxHard()
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
            sheets.Append(new Sheet { Name = "Đầu", SheetId = 1U, Id = workbookPart.GetIdOfPart(first) });

            var second = workbookPart.AddNewPart<WorksheetPart>();
            second.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new S.Text("inline two")) })));
            sheets.Append(new Sheet { Name = "Hai", SheetId = 2U, Id = workbookPart.GetIdOfPart(second) });
        }

        return stream.ToArray();
    }

    internal static byte[] BuildPptxEasy() => Pptx(("One", null), ("Two", null));

    internal static byte[] BuildPptxHard() =>
        ReorderZipEntries(
            Pptx(
                ("One", new A.Table(
                    new A.TableRow(
                        new A.TableCell(new A.TextBody(new A.Paragraph(new A.Run(new A.Text("T1"))))),
                        new A.TableCell(new A.TextBody(new A.Paragraph(new A.Run(new A.Text("T2")))))))),
                ("Two", null)),
            name => name switch
            {
                "ppt/slides/slide2.xml" => -1,
                "ppt/slides/slide1.xml" => 1,
                _ => 0,
            });

    private static byte[] Pptx(params (string Text, A.Table? Table)[] slides)
    {
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation();
            var slideIdList = presentationPart.Presentation.AppendChild(new P.SlideIdList());

            var slideId = 256U;
            foreach (var (text, table) in slides)
            {
                var shapeTree = new P.ShapeTree(
                    new P.Shape(new P.TextBody(new A.Paragraph(new A.Run(new A.Text(text))))));
                if (table is not null)
                {
                    shapeTree.Append(new P.GraphicFrame(
                        new A.Graphic(
                            new A.GraphicData(table)
                            {
                                Uri = "http://schemas.openxmlformats.org/drawingml/2006/table",
                            })));
                }

                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
                slideIdList.Append(new P.SlideId
                {
                    Id = slideId++,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart),
                });
            }
        }

        return stream.ToArray();
    }

    private static byte[] ReorderZipEntries(byte[] zip, Func<string, int> rank)
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
