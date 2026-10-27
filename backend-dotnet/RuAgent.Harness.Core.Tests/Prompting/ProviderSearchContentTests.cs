using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

/// <summary>Replay rules for provider-hosted web search content (ADR 0033).</summary>
public sealed class ProviderSearchContentTests
{
    [Fact]
    public void A_continuation_keeps_search_blocks_citations_and_unknown_fields_unchanged()
    {
        var result = SearchResult("srvtoolu_1");
        result["caller"] = new JsonObject { ["type"] = "direct" };
        var continuation = new ProviderContinuation("anthropic", "anthropic_messages", "claude-sonnet-5-5", 1,
            [ServerToolUse("srvtoolu_1"), result, CitedText(), ToolUse()]);

        Assert.Equal("direct", continuation.OutputItems[1]["caller"]!["type"]!.GetValue<string>());
        Assert.Equal("Eo8", continuation.OutputItems[2]["citations"]![0]!["encrypted_index"]!.GetValue<string>());
    }

    [Fact]
    public void Malformed_search_content_is_rejected()
    {
        var unencrypted = SearchResult("srvtoolu_1");
        unencrypted["content"]![0]!.AsObject().Remove("encrypted_content");
        Assert.Throws<ArgumentException>(() => new ProviderContinuation("anthropic", "anthropic_messages", "m", 1,
            [ServerToolUse("srvtoolu_1"), unencrypted, ToolUse()]));
        var otherTool = ServerToolUse("srvtoolu_1");
        otherTool["name"] = "code_execution";
        Assert.Throws<ArgumentException>(() => new ProviderContinuation("anthropic", "anthropic_messages", "m", 1, [otherTool, ToolUse()]));
        var badCitation = CitedText();
        badCitation["citations"]![0]!.AsObject().Remove("encrypted_index");
        Assert.Throws<ArgumentException>(() => new ProviderContinuation("anthropic", "anthropic_messages", "m", 1, [badCitation, ToolUse()]));
    }

    [Fact]
    public void A_search_call_without_its_result_is_dropped_on_replay()
    {
        IReadOnlyList<JsonObject> blocks = [ServerToolUse("srvtoolu_done"), SearchResult("srvtoolu_done"), ServerToolUse("srvtoolu_deferred"), ToolUse()];
        var kept = ProviderSearchContent.DropUnansweredSearchCalls(blocks);
        Assert.Equal(new[] { "srvtoolu_done", "srvtoolu_done" },
            kept.Where(ProviderSearchContent.IsAnthropicSearchBlock).Select(block => (block["id"] ?? block["tool_use_id"])!.GetValue<string>()));
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void Stripping_removes_every_trace_of_a_search()
    {
        var stripped = ProviderSearchContent.StripAnthropic([ServerToolUse("srvtoolu_1"), SearchResult("srvtoolu_1"), CitedText(), ToolUse()]);
        Assert.Equal(new[] { "text", "tool_use" }, stripped.Select(block => block["type"]!.GetValue<string>()));
        Assert.False(stripped[0].ContainsKey("citations"));

        var responses = ProviderSearchContent.StripResponses(
        [
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1" },
            new JsonObject { ["type"] = "web_search_call", ["id"] = "ws_1" },
            new JsonObject { ["type"] = "function_call", ["id"] = "fc_1" },
        ]);
        Assert.Equal("function_call", Assert.Single(responses)["type"]!.GetValue<string>());
        IReadOnlyList<JsonObject> plain = [new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1" }, new JsonObject { ["type"] = "function_call", ["id"] = "fc_1" }];
        Assert.Same(plain, ProviderSearchContent.StripResponses(plain));
    }

    [Fact]
    public void Sources_are_listed_once_in_first_cited_order_as_safe_links()
    {
        var sources = ProviderSearchContent.CitedSources(
        [
            Citation("https://b.example/", "B [beta]"),
            Citation("https://a.example/(x)", "  A  "),
            Citation("https://b.example/", "B again"),
            Citation("javascript:alert(1)", "bad"),
        ]);
        Assert.Equal("\n\nSources:\n- [B (beta)](https://b.example/)\n- [A](https://a.example/(x%29)",
            ProviderSearchContent.FormatSources(sources));
        Assert.Equal(string.Empty, ProviderSearchContent.FormatSources([]));
    }

    private static JsonObject ServerToolUse(string id) =>
        new() { ["type"] = "server_tool_use", ["id"] = id, ["name"] = "web_search", ["input"] = new JsonObject { ["query"] = "q" } };

    private static JsonObject SearchResult(string id) => new()
    {
        ["type"] = "web_search_tool_result", ["tool_use_id"] = id,
        ["content"] = new JsonArray(new JsonObject { ["type"] = "web_search_result", ["url"] = "https://example.com/", ["title"] = "Example", ["encrypted_content"] = "Eqg" }),
    };

    private static JsonObject CitedText() => new()
    {
        ["type"] = "text", ["text"] = "Found.", ["citations"] = new JsonArray(Citation("https://example.com/", "Example")),
    };

    private static JsonObject Citation(string url, string title) => new()
    {
        ["type"] = "web_search_result_location", ["url"] = url, ["title"] = title, ["encrypted_index"] = "Eo8", ["cited_text"] = "x",
    };

    private static JsonObject ToolUse() =>
        new() { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "read", ["input"] = new JsonObject() };
}
