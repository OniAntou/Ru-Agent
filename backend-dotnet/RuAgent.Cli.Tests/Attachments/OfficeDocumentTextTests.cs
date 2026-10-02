using System.IO.Compression;
using System.Text;
using RuAgent.Cli.Attachments;
using Xunit;

namespace RuAgent.Cli.Tests.Attachments;

public sealed class OfficeDocumentTextTests
{
    [Fact]
    public void Extract_ReadsWordTextAndParagraphBreaks()
    {
        var docx = Docx(("word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
            + "<w:p><w:r><w:t>Hello world</w:t></w:r></w:p>"
            + "<w:p><w:r><w:t>Second line</w:t></w:r></w:p>"
            + "</w:body></w:document>"));

        var result = OfficeDocumentText.Extract(docx);

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Hello world", result.Text);
        Assert.Contains("Second line", result.Text);
    }

    [Fact]
    public void Extract_LabelsSlidesInNumericOrder()
    {
        var pptx = Docx(
            ("ppt/slides/slide2.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>two</a:t></p:sld>"),
            ("ppt/slides/slide10.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>ten</a:t></p:sld>"),
            ("ppt/slides/slide1.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:t>one</a:t></p:sld>"));

        var result = OfficeDocumentText.Extract(pptx);

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Slide 1", result.Text);
        Assert.Contains("Slide 2", result.Text);
        Assert.Contains("Slide 10", result.Text);
    }

    [Fact]
    public void Extract_EmptyDocumentReportsNoContent()
    {
        var zip = Docx(("docProps/app.xml", "<Properties/>"));

        Assert.Equal(OfficeDocumentText.Result.NoContent, OfficeDocumentText.Extract(zip).Result);
    }

    [Fact]
    public void Extract_GarbageReportsMalformed()
    {
        var bytes = new byte[128];
        Random.Shared.NextBytes(bytes);

        Assert.Equal(OfficeDocumentText.Result.Malformed, OfficeDocumentText.Extract(bytes).Result);
        Assert.Equal(OfficeDocumentText.Result.Malformed, OfficeDocumentText.Extract([]).Result);
    }

    [Fact]
    public void IsSupported_MatchesOnlyOfficeExtensions()
    {
        Assert.True(OfficeDocumentText.IsSupported("report.docx"));
        Assert.True(OfficeDocumentText.IsSupported("DECK.PPTX"));
        Assert.True(OfficeDocumentText.IsSupported("book.xlsx"));
        Assert.False(OfficeDocumentText.IsSupported("notes.txt"));
        Assert.False(OfficeDocumentText.IsSupported("image.png"));
    }

    private static byte[] Docx(params (string Name, string Xml)[] parts)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, xml) in parts)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(xml);
            }
        }

        return stream.ToArray();
    }
}
