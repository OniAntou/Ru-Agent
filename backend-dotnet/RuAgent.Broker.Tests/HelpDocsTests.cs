using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Help;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class HelpDocsTests
{
    private static readonly string[] CurrentToolIds =
    [
        "read", "list", "help", "grep", "glob",
        "write", "edit", "bash", "web_search", "web_fetch",
    ];

    // read and list carry no discriminator since the split; only write and bash do.
    private static readonly string[] CurrentOps =
    [
        "op=write", "mode=run", "mode=shell",
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
    public void Heading_phrase_excerpt_keeps_section_opening_when_query_token_is_late()
    {
        const string opening = "read list help write";
        var documents = BundledHelpDocsCatalog.DefaultSources
            .Select((source, index) => (source, index == 0
                ? $"# Tools\n\n## Current capability matrix\n\n{opening} {new string('x', 2_100)} capability matrix {new string('y', 2_100)}\nlate line"
                : $"# {source.DocumentId}\n\n## Other\n\nUnrelated text."))
            .ToArray();
        var search = new HelpDocsSearch(BundledHelpDocsCatalog.FromText(documents));

        var result = Assert.Single(search.Search("current capability matrix", 1, out var more));

        Assert.False(more);
        Assert.True(result.WasExcerptTruncated);
        Assert.StartsWith(opening, result.Excerpt, StringComparison.Ordinal);
        Assert.True(result.Excerpt.Length <= 2_000);
    }

    [Fact]
    public void Excerpt_limit_does_not_split_surrogate_pairs()
    {
        const string heading = "Current capability matrix";
        const string opening = "read list help write ";
        var body = opening + new string('x', 1_998 - opening.Length) + "😀" + new string('y', 100);
        var documents = BundledHelpDocsCatalog.DefaultSources
            .Select((source, index) => (source, index == 0
                ? $"# Tools\n\n## {heading}\n\n{body}"
                : $"# {source.DocumentId}\n\n## Other\n\nUnrelated text."))
            .ToArray();
        var search = new HelpDocsSearch(BundledHelpDocsCatalog.FromText(documents));

        var result = Assert.Single(search.Search("current capability matrix", 1, out _));

        Assert.True(result.Excerpt.Length <= 2_000);
        Assert.True(HasWellFormedSurrogates(result.Excerpt));
    }

    [Fact]
    public void Tool_applies_defaults_bounds_and_returns_stable_json()
    {
        var tool = new HelpDocsTool(BundledHelpDocsCatalog.LoadDefault());

        var output = tool.Execute(new JsonObject { ["query"] = "approvals" });

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
    public void Tool_rejects_blank_query_and_out_of_range_max_results()
    {
        var tool = new HelpDocsTool(BundledHelpDocsCatalog.LoadDefault());

        // help no longer has an op discriminator; the strict schema rejects undeclared
        // properties before this tool is reached, so only query and max_results
        // validity are its job.
        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["query"] = "   " }));
        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["query"] = "tools", ["max_results"] = 0 }));
        Assert.Throws<ArgumentException>(() => tool.Execute(new JsonObject { ["query"] = "tools", ["max_results"] = 6 }));

        // A query with no max_results is valid and defaults to three results.
        var output = tool.Execute(new JsonObject { ["query"] = "tools" });
        Assert.True(output["results"] is JsonArray results && results.Count <= 3);
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

    private static bool HasWellFormedSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
