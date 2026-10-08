using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace RuAgent.Client;

public static partial class OfficeDocumentText
{
    private static SlidePart? SlidePart(PresentationPart presentationPart, P.SlideId slideId)
    {
        var relationshipId = slideId.RelationshipId?.Value;
        return string.IsNullOrEmpty(relationshipId)
            ? null
            : presentationPart.Parts
                .FirstOrDefault(pair => pair.RelationshipId == relationshipId)
                .OpenXmlPart as SlidePart;
    }

    private static bool AppendSlideText(StringBuilder text, P.Slide? slide, int remaining)
    {
        if (slide is null) return true;

        foreach (var element in slide.Descendants())
        {
            if (element is P.Shape shape)
            {
                if (!AppendTextBody(text, shape.TextBody, ref remaining)) return false;
            }
            else if (element is A.Table table && !AppendTable(text, table, ref remaining))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AppendTextBody(StringBuilder text, P.TextBody? textBody, ref int remaining)
    {
        if (textBody is null) return true;

        foreach (var paragraph in textBody.Elements<A.Paragraph>())
        {
            if (!AppendDrawingText(text, paragraph, ref remaining) || !TryAppendWithin(text, '\n', ref remaining))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AppendDrawingText(StringBuilder text, A.Paragraph paragraph, ref int remaining)
    {
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case A.Text value:
                    if (!TryAppendWithin(text, value.Text, ref remaining)) return false;
                    break;
                case A.Break:
                    if (!TryAppendWithin(text, '\n', ref remaining)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool AppendTable(StringBuilder text, A.Table table, ref int remaining)
    {
        foreach (var row in table.Elements<A.TableRow>())
        {
            var firstCell = true;
            foreach (var cell in row.Elements<A.TableCell>())
            {
                if (!firstCell && !TryAppendWithin(text, '\t', ref remaining)) return false;
                foreach (var body in cell.Elements<A.TextBody>())
                {
                    var firstParagraph = true;
                    foreach (var paragraph in body.Elements<A.Paragraph>())
                    {
                        if (!firstParagraph && !TryAppendWithin(text, ' ', ref remaining)) return false;
                        if (!AppendDrawingText(text, paragraph, ref remaining)) return false;
                        firstParagraph = false;
                    }
                }

                firstCell = false;
            }

            if (!TryAppendWithin(text, '\n', ref remaining)) return false;
        }

        return true;
    }

    private static bool TryAppendWithin(StringBuilder text, string value, ref int remaining)
    {
        if (value.Length > remaining) return false;
        text.Append(value);
        remaining -= value.Length;
        return true;
    }

    private static bool TryAppendWithin(StringBuilder text, char value, ref int remaining)
    {
        if (remaining == 0) return false;
        text.Append(value);
        remaining--;
        return true;
    }
}
