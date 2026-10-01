using Markdig;
using Markdig.Syntax;

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
}
