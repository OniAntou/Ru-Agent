using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RuApp.Controls;

/// <summary>
/// The one Markdown pipeline for chat rendering: full CommonMark plus the
/// tables, task lists, strikethrough and autolinks agents actually emit.
/// Tests pin this shape; the control below only walks what it builds.
/// </summary>
public static class MarkdownEngine
{
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static MarkdownDocument Parse(string markdown) =>
        Markdown.Parse(markdown ?? string.Empty, Pipeline);

    /// <summary>
    /// True for blocks that hold link targets, not content. The auto-identifier
    /// extension files one definition per heading at the end of the document; drawn
    /// as literal leaves they became one empty line each, so a reply with fifteen
    /// headings ended in a blank several hundred pixels tall.
    /// </summary>
    public static bool IsLinkTargetsOnly(Block block) =>
        block is LinkReferenceDefinitionGroup or LinkReferenceDefinition;

    // Tables currently display plain cell text. Walk containers too so emphasis
    // and link labels survive both rendering and the copy-as-TSV action.
    public static string TableCellText(Markdig.Extensions.Tables.TableCell cell)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var descendant in cell.Descendants())
        {
            if (descendant is ParagraphBlock cellPara && cellPara.Inline is not null)
            {
                AppendCellInlines(builder, cellPara.Inline);
            }
        }

        return builder.ToString();
    }

    private static void AppendCellInlines(System.Text.StringBuilder builder, ContainerInline parent)
    {
        foreach (var inline in parent)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
                case AutolinkInline link:
                    builder.Append(link.Url);
                    break;
                case ContainerInline container:
                    AppendCellInlines(builder, container);
                    break;
            }
        }
    }
}
