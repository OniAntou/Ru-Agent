using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Help;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class HelpDocsTests
{
    private static readonly string[] CurrentToolIds =
    [
        "read", "write", "edit", "bash",
    ];

    private static readonly string[] CurrentOps =
    [
        "op=read", "op=write", "op=web_search", "mode=run", "mode=shell",
    ];

    private static readonly string[] RetiredToolIds =
    [
        "system.time", "system.status", "app.list", "notes.create", "system.volume",
    ];

    [Fact]
    public void Default_catalog_loads_only_the_allowlisted_embedded_documents()
    {
        var catalog = BundledHelpDocsCatalog.LoadDefault();

        Assert.NotEmpty(catalog.Sections);
        Assert.All(catalog.Sections, section => Assert.Contains(
            section.DocumentId,
            BundledHelpDocsCatalog.DefaultSources.Select(source => source.DocumentId)));
        Assert.Contains(catalog.Sections, section => section.DocumentId == "agent-tools");
    }

    [Fact]
    public void Search_normalizes_unicode_and_ranks_heading_phrase_before_body_token()
    {
        var catalog = BundledHelpDocsCatalog.FromText(
        [
            (BundledHelpDocsCatalog.DefaultSources[0], "# Ru\n\n## Overview\n\nGeneral text."),
            (BundledHelpDocsCatalog.DefaultSources[1], "# Architecture\n\n## Approvals\n\nApprovals work here."),
            (BundledHelpDocsCatalog.DefaultSources[2], "# Runtime\n\n## Details\n\nThe approval boundary is described."),
            (BundledHelpDocsCatalog.DefaultSources[3], "# Tools\n\n## Other\n\nNo match."),
            (BundledHelpDocsCatalog.DefaultSources[4], "# Providers\n\n## Café\n\nCafé providers."),
        ]);

        var search = new HelpDocsSearch(catalog);
        var results = search.Search("approvals work", 3, out var more);

        Assert.False(more);
        Assert.Equal("architecture", results[0].DocumentId);
        Assert.Equal("Approvals", results[0].Section);
        Assert.Contains("Approvals work", results[0].Excerpt);
    }

    [Fact]
    public void Tool_applies_defaults_bounds_and_returns_stable_json()
    {
        var tool = new HelpDocsTool(BundledHelpDocsCatalog.LoadDefault());

        var output = tool.Execute(new JsonObject { ["op"] = "help", ["query"] = "approvals" });

        Assert.Equal("approvals", output["query"]!.GetValue<string>());
        var results = output["results"]!.AsArray();
        Assert.InRange(results.Count, 0, 3);
        Assert.True(output["truncated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("session folder grant", "not shared")]
    [InlineData("terminal unknown executable", "approval")]
    [InlineData("provider model reasoning refresh", "reasoning")]
    [InlineData("provider fallback", "no fallback")]
    public void Default_help_answers_current_operating_questions(string query, string expected)
    {
        var output = HelpDocsTool.CreateDefault().Execute(new JsonObject
        {
            ["op"] = "help",
            ["query"] = query,
            ["max_results"] = 5,
        });

        var excerpts = string.Join("\n", output["results"]!.AsArray()
            .Select(item => item!["excerpt"]!.GetValue<string>()));
        Assert.Contains(expected, excerpts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_help_exposes_only_the_current_tool_inventory()
    {
        var output = HelpDocsTool.CreateDefault().Execute(new JsonObject
        {
            ["op"] = "help",
            ["query"] = "current capability matrix",
            ["max_results"] = 5,
        });

        var excerpts = string.Join("\n", output["results"]!.AsArray()
            .Select(item => item!["excerpt"]!.GetValue<string>()));
        Assert.All(CurrentToolIds, toolId => Assert.Contains(toolId, excerpts, StringComparison.Ordinal));
        Assert.All(CurrentOps, op => Assert.Contains(op, excerpts, StringComparison.Ordinal));
        Assert.All(RetiredToolIds, toolId => Assert.DoesNotContain(toolId, excerpts, StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_rejects_blank_or_extra_arguments()
    {
        var tool = new HelpDocsTool(BundledHelpDocsCatalog.LoadDefault());

        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["op"] = "help", ["query"] = "   " }));
        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["op"] = "read", ["query"] = "tools" }));
        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["query"] = "tools" }));
    }

    [Fact]
    public void Invalid_packaging_fails_closed()
    {
        var sources = BundledHelpDocsCatalog.DefaultSources.ToArray();
        sources[0] = sources[1];
        var documents = sources.Select(source => (source, "# Title\n\n## Section\n\nText")).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => BundledHelpDocsCatalog.FromText(documents));

        Assert.StartsWith("help_docs_catalog_invalid:", error.Message, StringComparison.Ordinal);
    }
}
