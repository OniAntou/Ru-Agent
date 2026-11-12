using System;
using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class MarkdownLinkResolverTests
{
    private static string Workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-markdown-links", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Relative_and_workspace_rooted_links_resolve_inside_the_workspace()
    {
        var workspace = Workspace();
        try
        {
            Assert.Equal(
                Path.Combine(workspace, "src", "Program.cs"),
                MarkdownLinkResolver.ResolveLocalFile(workspace, "src/Program.cs"));
            Assert.Equal(
                Path.Combine(workspace, "src", "Program.cs"),
                MarkdownLinkResolver.ResolveLocalFile(workspace, "/src/Program.cs"));
            Assert.Equal(
                Path.Combine(workspace, "docs", "a #1.md"),
                MarkdownLinkResolver.ResolveLocalFile(workspace, "docs/a%20%231.md#L12"));
            Assert.Equal(
                Path.Combine(workspace, "src", "Program.cs"),
                MarkdownLinkResolver.ResolveLocalFile(workspace, new Uri(Path.Combine(workspace, "src", "Program.cs")).AbsoluteUri));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Links_that_leave_the_workspace_or_name_another_scheme_are_rejected()
    {
        var workspace = Workspace();
        try
        {
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile(workspace, "../outside.cs"));
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile(workspace, @"C:\Windows\win.ini"));
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile(workspace, "https://example.com/a.cs"));
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile(workspace, "mailto:user@example.com"));
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile(workspace, "#L12"));
            Assert.Null(MarkdownLinkResolver.ResolveLocalFile("", "src/a.cs"));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Local_candidates_exclude_web_and_mail_links()
    {
        Assert.True(MarkdownLinkResolver.MayBeLocalFile("src/a.cs"));
        Assert.True(MarkdownLinkResolver.MayBeLocalFile("/src/a.cs"));
        Assert.True(MarkdownLinkResolver.MayBeLocalFile(@"C:\repo\a.cs"));
        Assert.True(MarkdownLinkResolver.MayBeLocalFile(new Uri(Path.GetTempPath() + "a.cs").AbsoluteUri));
        Assert.False(MarkdownLinkResolver.MayBeLocalFile("https://example.com"));
        Assert.False(MarkdownLinkResolver.MayBeLocalFile("mailto:user@example.com"));
        Assert.False(MarkdownLinkResolver.MayBeLocalFile("#L12"));
    }
}
