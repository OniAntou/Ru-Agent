using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace RuApp.Tests;

/// <summary>
/// Builds real OOXML containers in memory, so the extractor is tested against the actual
/// zip-of-XML shape it will meet rather than a stand-in.
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

    internal const string SlideXml =
        """
<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
  <p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>SLIDE</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>
</p:sld>
""";

    internal static byte[] Docx(string body = DocumentXml) => Pack(
        ("[Content_Types].xml", "<Types/>"),
        ("word/document.xml", body),
        ("word/styles.xml", "<styles/>"));

    internal static byte[] Pptx(params (string Name, string Body)[] slides) => Pack(
        new[]
        {
            ("[Content_Types].xml", "<Types/>"),
            ("ppt/presentation.xml", "<presentation/>"),
        }.Concat(slides).ToArray());

    /// <summary>Two slides, so ordering and slide labels are observable.</summary>
    internal static byte[] PptxTwoSlides() => Pptx(
        ("ppt/slides/slide1.xml", SlideXml.Replace("SLIDE", "One")),
        ("ppt/slides/slide2.xml", SlideXml.Replace("SLIDE", "Two")));

    /// <summary>A renamed text file: the case that must fail closed, not be read as text.</summary>
    internal static byte[] NotAZip() => Encoding.UTF8.GetBytes("plain text pretending to be a docx");

    /// <summary>A container whose only text part is empty.</summary>
    internal static byte[] EmptyDocumentPart() => Pack(("word/document.xml", "<w:document/>"));

    /// <summary>A part carrying a DTD, which must be refused rather than resolved.</summary>
    internal static byte[] DocumentWithDtd() => Pack(
        ("word/document.xml",
            "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>"
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + "<w:body><w:t>&x;</w:t></w:body></w:document>"));

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
}
