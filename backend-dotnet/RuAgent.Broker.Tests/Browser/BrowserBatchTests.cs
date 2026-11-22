using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

/// <summary>
/// The <c>batch</c> action against a real browser: several page actions in one tool call
/// (ADR 0055 amendment 2026-11-23), so an Agent turn does not pay one model step per action.
/// </summary>
public sealed class BrowserBatchTests
{
    [AnyBrowserFact]
    public async Task Batch_runs_dom_actions_in_one_call_on_a_real_browser()
    {
        await using var service = new BrowserUseService(new AutoBrowserEngine());
        Assert.True(service.IsAvailable);
        var open = await service.ExecuteAsync("run", "session", new JsonObject { ["action"] = "open" }, CancellationToken.None);
        Assert.Null(open.ErrorCode);
        try
        {
            var batch = await service.ExecuteAsync(
                "run",
                "session",
                new JsonObject
                {
                    ["action"] = "batch",
                    ["steps"] = new JsonArray(
                        Step("eval", ("expression", """
                            document.body.innerHTML = '<button id="inc" onclick="document.getElementById(&quot;count&quot;).textContent = String(Number(document.getElementById(&quot;count&quot;).textContent) + 1)">Increment</button><div id="count">0</div><input id="name" placeholder="Your name">';
                            true
                            """)),
                        Step("snapshot"),
                        Step("click", ("ref", "e1")),
                        Step("type", ("ref", "e2"), ("text", "Ada")),
                        Step("eval", ("expression", "({count: document.getElementById('count').textContent, typed: document.getElementById('name').value})"))),
                },
                CancellationToken.None);

            Assert.Null(batch.ErrorCode);
            Assert.Equal(5, batch.Output["step_count"]!.GetValue<int>());
            var steps = batch.Output["steps"]!.AsArray();
            Assert.Equal("snapshot", steps[1]!["action"]!.GetValue<string>());
            var snapshotElements = steps[1]!["elements"]!.AsArray();
            Assert.Contains(snapshotElements, element => element!["name"]!.GetValue<string>() == "Increment");
            var evaluated = steps[4]!["result"]!;
            Assert.Equal("1", evaluated["count"]!.GetValue<string>());
            Assert.Equal("Ada", evaluated["typed"]!.GetValue<string>());

            var failed = await service.ExecuteAsync(
                "run",
                "session",
                new JsonObject
                {
                    ["action"] = "batch",
                    ["steps"] = new JsonArray(Step("snapshot"), Step("click", ("ref", "e999"))),
                },
                CancellationToken.None);
            Assert.Equal("browser_element_not_found", failed.ErrorCode);
            Assert.Contains("Step 2 (click)", failed.SafeSummary, StringComparison.Ordinal);
        }
        finally
        {
            await service.ExecuteAsync("run", "session", new JsonObject { ["action"] = "close" }, CancellationToken.None);
        }
    }

    private static JsonObject Step(string action, params (string Name, JsonNode? Value)[] arguments)
    {
        var step = new JsonObject { ["action"] = action };
        foreach (var (name, value) in arguments) step[name] = value?.DeepClone();
        return step;
    }
}
