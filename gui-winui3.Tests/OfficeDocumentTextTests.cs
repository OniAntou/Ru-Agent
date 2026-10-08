using System;
using System.Linq;
using System.Text;
using RuAgent.Client;
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
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("First line", result.Text, StringComparison.Ordinal);
        Assert.Contains("Second line", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraph_marks_become_line_breaks() =>
        Assert.Contains('\n', OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx()).Text);

    [Fact]
    public void A_docx_keeps_paragraphs_and_tables_in_document_order()
    {
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.DocxWithTable());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Before table", result.Text, StringComparison.Ordinal);
        Assert.Contains("A1", result.Text, StringComparison.Ordinal);
        Assert.Contains("B1", result.Text, StringComparison.Ordinal);
        Assert.Contains("A2", result.Text, StringComparison.Ordinal);
        Assert.Contains("B2", result.Text, StringComparison.Ordinal);
        Assert.Contains("A1\tB1\nA2\tB2", result.Text, StringComparison.Ordinal);
        Assert.Contains("Tiếng Việt", result.Text, StringComparison.Ordinal);
        Assert.True(
            result.Text.IndexOf("Before table", StringComparison.Ordinal)
                < result.Text.IndexOf("A1", StringComparison.Ordinal),
            "the paragraph must precede the table");
        Assert.True(
            result.Text.IndexOf("A1", StringComparison.Ordinal) < result.Text.IndexOf("A2", StringComparison.Ordinal),
            "table rows must keep document order");
    }

    [Fact]
    public void An_xlsx_keeps_inline_strings_numbers_and_sheet_order()
    {
        // The old extractor read only xl/sharedStrings.xml: inline strings and cell values
        // were silently lost. All of them must now survive.
        var result = OfficeDocumentText.Extract("a.xlsx", OfficeDocumentFixtures.Xlsx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("inline one", result.Text, StringComparison.Ordinal);
        Assert.Contains("inline two", result.Text, StringComparison.Ordinal);
        Assert.Contains("42", result.Text, StringComparison.Ordinal);
        Assert.Contains("45123", result.Text, StringComparison.Ordinal);
        Assert.Contains("shared text", result.Text, StringComparison.Ordinal);
        Assert.Contains("Sheet: First", result.Text, StringComparison.Ordinal);
        Assert.Contains("Sheet: Second", result.Text, StringComparison.Ordinal);
        Assert.True(
            result.Text.IndexOf("Sheet: First", StringComparison.Ordinal)
                < result.Text.IndexOf("Sheet: Second", StringComparison.Ordinal),
            "sheets must keep workbook order");
    }

    [Fact]
    public void An_xlsx_preserves_sparse_cell_columns()
    {
        var result = OfficeDocumentText.Extract("a.xlsx", OfficeDocumentFixtures.SparseXlsx());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Name\t\tEmail", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_string_referenced_many_times_stays_within_allocation_budget()
    {
        var bytes = OfficeDocumentFixtures.XlsxWithAmplifiedSharedString(references: 100, valueLength: 100_000);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = OfficeDocumentText.Extract("a.xlsx", bytes);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
        Assert.True(allocated < 32_000_000, $"Extraction allocated {allocated:N0} bytes before refusing the workbook.");
    }

    [Fact]
    public void A_nested_docx_table_past_the_depth_bound_is_refused()
    {
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.DocxWithNestedTables(levels: 40));

        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_oversized_pptx_paragraph_or_table_stays_within_allocation_budget(bool useTable)
    {
        var bytes = OfficeDocumentFixtures.PptxWithLargeText(useTable, runCount: 8, runLength: 1_000_000);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = OfficeDocumentText.Extract("large.pptx", bytes);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
        Assert.True(allocated < 32_000_000, $"Extraction allocated {allocated:N0} bytes before refusing the slide.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_oversized_xlsx_inline_or_shared_rich_string_stays_within_allocation_budget(bool useSharedString)
    {
        var bytes = OfficeDocumentFixtures.XlsxWithLargeRichString(useSharedString, runCount: 100, runLength: 100_000);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = OfficeDocumentText.Extract("large.xlsx", bytes);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
        Assert.True(allocated < 32_000_000, $"Extraction allocated {allocated:N0} bytes before refusing the cell.");
    }

    [Fact]
    public void A_pptx_yields_every_slide_with_a_label()
    {
        var result = OfficeDocumentText.Extract("a.pptx", OfficeDocumentFixtures.PptxTwoSlides());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("One", result.Text, StringComparison.Ordinal);
        Assert.Contains("Two", result.Text, StringComparison.Ordinal);
        Assert.Contains("Slide 1", result.Text, StringComparison.Ordinal);
        Assert.Contains("Slide 2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pptx_follows_presentation_order_not_zip_entry_order()
    {
        // The old extractor iterated zip entries, so a container that stores slide2 before
        // slide1 reordered the deck. Presentation order is the presentation part's list.
        var result = OfficeDocumentText.Extract("a.pptx", OfficeDocumentFixtures.PptxWithReorderedZipEntries());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.True(
            result.Text.IndexOf("One", StringComparison.Ordinal) < result.Text.IndexOf("Two", StringComparison.Ordinal),
            "slide1 must precede slide2");
        Assert.True(
            result.Text.IndexOf("Slide 1", StringComparison.Ordinal)
                < result.Text.IndexOf("Slide 2", StringComparison.Ordinal),
            "labels must follow presentation order");
    }

    [Fact]
    public void A_pptx_reads_table_cells()
    {
        var result = OfficeDocumentText.Extract("a.pptx", OfficeDocumentFixtures.PptxWithTable());

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("T1", result.Text, StringComparison.Ordinal);
        Assert.Contains("T2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Slide_numbers_order_numerically_not_lexically()
    {
        // slide2 must not be read before slide10: the deck is read in presentation order,
        // and the label comes from the position, not from a lexical part name.
        var slides = Enumerable.Range(1, 11).Select(index => $"Body{index}").ToArray();

        var result = OfficeDocumentText.Extract("a.pptx", OfficeDocumentFixtures.Pptx(slides));

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Slide 10", result.Text, StringComparison.Ordinal);
        Assert.Contains("Slide 11", result.Text, StringComparison.Ordinal);
        Assert.True(
            result.Text.IndexOf("Body2", StringComparison.Ordinal) < result.Text.IndexOf("Body10", StringComparison.Ordinal),
            "slide2 must precede slide10");
    }

    [Fact]
    public void A_docx_reads_text_inside_content_controls()
    {
        // A content control is a wrapper, not a text part: its paragraphs must still be
        // read in document order.
        var body = """
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:sdt><w:sdtContent><w:p><w:r><w:t>Inside control</w:t></w:r></w:p></w:sdtContent></w:sdt>
    <w:p><w:r><w:t>After control</w:t></w:r></w:p>
  </w:body>
</w:document>
""";

        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx(body));

        Assert.Equal(OfficeDocumentText.Result.Ok, result.Result);
        Assert.Contains("Inside control", result.Text, StringComparison.Ordinal);
        Assert.True(
            result.Text.IndexOf("Inside control", StringComparison.Ordinal)
                < result.Text.IndexOf("After control", StringComparison.Ordinal),
            "the control body must keep document order");
    }

    [Fact]
    public void A_renamed_text_file_fails_closed_rather_than_being_read_as_text()
    {
        // The dangerous case: treating arbitrary bytes as a prompt would hand the model
        // binary noise while claiming the file was read successfully.
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.NotAZip());

        Assert.Equal(OfficeDocumentText.Result.Malformed, result.Result);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public void An_empty_input_is_malformed() =>
        Assert.Equal(
            OfficeDocumentText.Result.Malformed,
            OfficeDocumentText.Extract("a.docx", ReadOnlySpan<byte>.Empty).Result);

    [Fact]
    public void A_container_with_no_text_reports_no_content()
    {
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.EmptyDocumentPart());

        Assert.Equal(OfficeDocumentText.Result.NoContent, result.Result);
    }

    [Fact]
    public void A_part_carrying_a_dtd_is_refused_not_resolved()
    {
        // XXE: an external entity must never be fetched, and its expansion must never
        // reach the prompt.
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.DocumentWithDtd());

        Assert.NotEqual(OfficeDocumentText.Result.Ok, result.Result);
        Assert.DoesNotContain("root:", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_xml_inside_a_valid_container_yields_no_text()
    {
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx(
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><unclosed>"));

        Assert.NotEqual(OfficeDocumentText.Result.Ok, result.Result);
    }

    [Fact]
    public void Styles_and_other_unread_parts_are_skipped()
    {
        // The extended-properties part carries the text "styles"; it must not contribute:
        // the model wants the words, not the application metadata.
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx());

        Assert.DoesNotContain("styles", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_large_document_is_refused_instead_of_unbounded()
    {
        var big = new string('x', OfficeDocumentText.MaxTextChars + 1_000);
        var body = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + $"<w:body><w:p><w:r><w:t>{big}</w:t></w:r></w:p></w:body></w:document>";

        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx(body));

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

        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Pack(parts));

        Assert.Equal(OfficeDocumentText.Result.TooLarge, result.Result);
    }

    [Fact]
    public void An_entry_name_cannot_escape_the_container()
    {
        // A crafted entry name is never used as a path; the SDK reads package parts by
        // relationship, so traversal syntax is inert.
        var result = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Pack(
            ("../../evil.xml", "<w:t xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">escaped</w:t>"),
            ("word/document.xml", OfficeDocumentFixtures.DocumentXml)));

        Assert.DoesNotContain("escaped", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracted_text_is_prompt_safe_utf8()
    {
        // The caller re-encodes this, so it must survive a round trip as text.
        var body = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Tiếng Việt: đồ ăn, phở, xe máy</w:t></w:r></w:p></w:body></w:document>";

        var text = OfficeDocumentText.Extract("a.docx", OfficeDocumentFixtures.Docx(body)).Text;
        var roundTripped = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));

        Assert.Contains("Tiếng Việt", roundTripped, StringComparison.Ordinal);
    }
}
