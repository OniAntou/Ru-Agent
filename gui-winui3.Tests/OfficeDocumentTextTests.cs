using System;
using System.IO.Compression;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class OfficeDocumentTextTests
{
    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.pptx")]
    [InlineData("a.xlsx")]
    [InlineData("A.DOCX")]
    public void The_office_formats_the_extractor_can_read_are_accepted(string name) =>
        Assert.True(ChatAttachmentPolicy.IsOfficeDocumentFile(name));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.cs")]
    [InlineData("a.png")]
    [InlineData("a.pdf")]
    [InlineData("a.doc")]     // the legacy binary format, not OOXML
    [InlineData("a.ppt")]
    [InlineData("noextension")]
    public void Anything_else_is_not_an_office_document(string name) =>
        Assert.False(ChatAttachmentPolicy.IsOfficeDocumentFile(name));

    [Fact]
    public void A_docx_yields_its_paragraphs()
    {
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("First line", result.Text, StringComparison.Ordinal);
        Assert.Contains("Second line", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraph_marks_become_line_breaks() =>
        Assert.Contains('\n', OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx()).Text);

    [Fact]
    public void A_pptx_yields_every_slide_with_a_label()
    {
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.PptxTwoSlides());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("One", result.Text, StringComparison.Ordinal);
        Assert.Contains("Two", result.Text, StringComparison.Ordinal);
        Assert.Contains("Slide 1", result.Text, StringComparison.Ordinal);
        Assert.Contains("Slide 2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slide_numbers_order_numerically_not_lexically()
    {
        // slide10 must not be read before slide2: the label comes from the parsed
        // number, and the text must appear in that order.
        var parts = new (string, string)[11];
        for (var index = 1; index <= 11; index++)
        {
            parts[index - 1] = ($"ppt/slides/slide{index}.xml", OfficeDocumentFixtures.SlideXml.Replace("SLIDE", $"Body{index}"));
        }

        var text = OfficeDocumentText.Extract(OfficeDocumentFixtures.Pptx(parts)).Text;

        Assert.True(
            text.IndexOf("Body2", StringComparison.Ordinal) < text.IndexOf("Body10", StringComparison.Ordinal),
            "slide2 must precede slide10");
    }

    [Fact]
    public void A_renamed_text_file_fails_closed_rather_than_being_read_as_text()
    {
        // The dangerous case: treating arbitrary bytes as a prompt would hand the model
        // binary noise while claiming the file was read successfully.
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.NotAZip());

        Assert.Equal(OfficeDocumentText.Result.Malformed, result.Result);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void An_empty_input_is_malformed() =>
        Assert.Equal(OfficeDocumentText.Result.Malformed, OfficeDocumentText.Extract(ReadOnlySpan<byte>.Empty).Result);

    [Fact]
    public void A_container_with_no_text_reports_no_content()
    {
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.EmptyDocumentPart());

        Assert.Equal(OfficeDocumentText.Result.NoContent, result.Result);
    }

    [Fact]
    public void A_part_carrying_a_dtd_is_refused_not_resolved()
    {
        // XXE: an external entity must never be fetched, and its expansion must never
        // reach the prompt.
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.DocumentWithDtd());

        Assert.DoesNotContain("root:", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_xml_inside_a_valid_container_yields_no_text()
    {
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx(
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><unclosed>"));

        Assert.NotEqual(OfficeDocumentText.Result.Ok, result.Result);
    }

    [Fact]
    public void Styles_and_other_unread_parts_are_skipped()
    {
        // word/styles.xml must not contribute: the model wants the words, not the
        // layout, and style names would read as if they were content.
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx());

        Assert.DoesNotContain("styles", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_large_document_is_refused_instead_of_unbounded()
    {
        var big = new string('x', OfficeDocumentText.MaxTextChars + 1_000);
        var body = $"<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:t>{big}</w:t></w:body></w:document>";

        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx(body));

        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
    }

    [Fact]
    public void An_entry_count_past_the_bound_is_refused()
    {
        var parts = new (string, string)[4_100];
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = ($"word/media/image{index}.png", "x");
        }

        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Pack(parts));

        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
    }

    [Fact]
    public void An_entry_name_cannot_escape_the_container()
    {
        // A crafted entry name is matched by suffix against a fixed set; it is never
        // treated as a path, so traversal syntax is inert.
        var result = OfficeDocumentText.Extract(OfficeDocumentFixtures.Pack(
            ("../../evil.xml", "<w:t xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">escaped</w:t>"),
            ("word/document.xml", OfficeDocumentFixtures.DocumentXml)));

        Assert.DoesNotContain("escaped", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracted_text_is_prompt_safe_utf8()
    {
        // The caller re-encodes this, so it must survive a round trip as text.
        var body = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:t>Tiếng Việt: đồ ăn, phở, xe máy</w:t></w:body></w:document>";

        var text = OfficeDocumentText.Extract(OfficeDocumentFixtures.Docx(body)).Text;
        var roundTripped = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));

        Assert.Contains("Tiếng Việt", roundTripped, StringComparison.Ordinal);
    }
}
