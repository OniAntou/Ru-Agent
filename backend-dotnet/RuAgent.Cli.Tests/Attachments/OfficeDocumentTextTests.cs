using System.Linq;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Attachments;

public sealed class OfficeDocumentTextTests
{
    [Fact]
    public void Extract_ReadsWordTextAndParagraphBreaks()
    {
        var result = OfficeDocumentText.Extract("report.docx", OfficeDocumentFixtures.Docx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("First line", result.Text);
        Assert.Contains("Second line", result.Text);
        Assert.Contains('\n', result.Text);
    }

    [Fact]
    public void Extract_LabelsSlidesInNumericOrder()
    {
        var slides = Enumerable.Range(1, 10).Select(index => $"Slide text {index}").ToArray();

        var result = OfficeDocumentText.Extract("deck.pptx", OfficeDocumentFixtures.Pptx(slides));

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Slide 1", result.Text);
        Assert.Contains("Slide 2", result.Text);
        Assert.Contains("Slide 10", result.Text);
    }

    [Fact]
    public void Extract_FollowsPresentationOrderOverZipOrder()
    {
        var result = OfficeDocumentText.Extract("deck.pptx", OfficeDocumentFixtures.PptxWithReorderedZipEntries());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.True(
            result.Text.IndexOf("One", System.StringComparison.Ordinal)
                < result.Text.IndexOf("Two", System.StringComparison.Ordinal),
            "slide1 must precede slide2");
    }

    [Fact]
    public void Extract_KeepsInlineStringsAndNumbers()
    {
        var result = OfficeDocumentText.Extract("book.xlsx", OfficeDocumentFixtures.Xlsx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("inline one", result.Text);
        Assert.Contains("inline two", result.Text);
        Assert.Contains("42", result.Text);
        Assert.Contains("shared text", result.Text);
        Assert.True(
            result.Text.IndexOf("Sheet: First", System.StringComparison.Ordinal)
                < result.Text.IndexOf("Sheet: Second", System.StringComparison.Ordinal),
            "sheets must keep workbook order");
    }

    [Fact]
    public void Extract_EmptyDocumentReportsNoContent()
    {
        var empty = OfficeDocumentFixtures.Docx(
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>");

        Assert.Equal(OfficeDocumentText.Result.NoContent, OfficeDocumentText.Extract("empty.docx", empty).Result);
    }

    [Fact]
    public void Extract_GarbageReportsMalformed()
    {
        var bytes = new byte[128];
        Random.Shared.NextBytes(bytes);

        Assert.Equal(OfficeDocumentText.Result.Malformed, OfficeDocumentText.Extract("broken.docx", bytes).Result);
        Assert.Equal(OfficeDocumentText.Result.Malformed, OfficeDocumentText.Extract("broken.docx", []).Result);
    }

    [Fact]
    public void Extract_OneOversizedPartIsRefused()
    {
        var big = new string('x', OfficeDocumentText.MaxTextChars + 1_000);
        var docx = OfficeDocumentFixtures.Docx(
            $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{big}</w:t></w:r></w:p></w:body></w:document>");

        Assert.Equal(OfficeDocumentText.Result.TooLarge, OfficeDocumentText.Extract("big.docx", docx).Result);
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
}
