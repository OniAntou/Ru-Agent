using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

public sealed class BrowserUseServiceTests
{
    [Fact]
    public async Task Actions_other_than_open_need_an_opened_session()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);

        var snapshot = await ExecuteAsync(service, "snapshot");
        Assert.Equal("browser_not_open", snapshot.ErrorCode);

        var opened = await ExecuteAsync(service, "open");
        Assert.Null(opened.ErrorCode);
        Assert.True(opened.Output["opened"]!.GetValue<bool>());
        Assert.True(engine.Opened);
        Assert.True(engine.LastHeadless);
    }

    [Fact]
    public async Task Snapshot_click_type_fill_eval_and_close_reach_the_page()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var snapshot = await ExecuteAsync(service, "snapshot");
        Assert.Null(snapshot.ErrorCode);
        var element = Assert.Single(snapshot.Output["elements"]!.AsArray());
        Assert.Equal("e1", element!["ref"]!.GetValue<string>());
        Assert.Equal("Go", element["name"]!.GetValue<string>());

        var click = await ExecuteAsync(service, "click", ("ref", "e1"));
        Assert.Null(click.ErrorCode);
        Assert.Equal("e1", engine.Clicked);

        var type = await ExecuteAsync(service, "type", ("ref", "e1"), ("text", "Ada"));
        Assert.Null(type.ErrorCode);
        Assert.Equal("Ada", engine.Typed);
        Assert.False(engine.TypedReplace);

        var fill = await ExecuteAsync(service, "fill", ("ref", "e1"), ("text", "Grace"));
        Assert.Null(fill.ErrorCode);
        Assert.Equal("Grace", engine.Filled);

        var press = await ExecuteAsync(service, "press", ("key", "Enter"));
        Assert.Null(press.ErrorCode);
        Assert.Equal("Enter", engine.Pressed);

        var evaluate = await ExecuteAsync(service, "eval", ("expression", "1 + 1"));
        Assert.Null(evaluate.ErrorCode);
        Assert.Equal("ok", evaluate.Output["result"]!.GetValue<string>());

        var closed = await ExecuteAsync(service, "close");
        Assert.Null(closed.ErrorCode);
        Assert.True(closed.Output["closed"]!.GetValue<bool>());
        Assert.True(engine.Closed);

        var again = await ExecuteAsync(service, "close");
        Assert.Null(again.ErrorCode);
        Assert.False(again.Output["closed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Invalid_url_bounds_and_missing_arguments_fail_by_name()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        Assert.Equal("browser_invalid_url", (await ExecuteAsync(service, "navigate", ("url", "file:///C:/secret.txt"))).ErrorCode);
        Assert.Equal("browser_invalid_url", (await ExecuteAsync(service, "navigate", ("url", "not a url"))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "click")).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "type", ("ref", "e1"), ("text", ""))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "wait", ("ms", 5))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "eval")).ErrorCode);
        Assert.Equal("browser_action_invalid", (await ExecuteAsync(service, "fly")).ErrorCode);
    }

    [Fact]
    public async Task Engine_failures_keep_their_named_codes()
    {
        var engine = new FakeEngine
        {
            Failure = new BrowserEngineException("browser_timeout", "The page did not answer."),
        };
        await using var service = new BrowserUseService(engine);

        var open = await ExecuteAsync(service, "open");
        Assert.Equal("browser_timeout", open.ErrorCode);
        Assert.Equal("The page did not answer.", open.SafeSummary);

        var unavailable = new FakeEngine { IsAvailable = false };
        await using var service2 = new BrowserUseService(unavailable);
        var missing = await ExecuteAsync(service2, "open");
        Assert.Equal("browser_unavailable", missing.ErrorCode);
    }

    [Fact]
    public async Task Removing_a_session_closes_its_page_and_dispose_closes_the_engine()
    {
        var engine = new FakeEngine();
        var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);
        service.RemoveSession("session");
        await WaitUntilAsync(() => engine.Closed);
        Assert.True(engine.Closed);

        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);
        await service.DisposeAsync();
        Assert.True(engine.Disposed);
    }

    [Fact]
    public async Task Batch_runs_several_actions_in_one_call_and_stops_on_failure()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var batch = await ExecuteAsync(
            service,
            "batch",
            ("steps", new JsonArray(
                Step("snapshot"),
                Step("click", ("ref", "e1")),
                Step("type", ("ref", "e1"), ("text", "Ada")),
                Step("eval", ("expression", "1+1")))));

        Assert.Null(batch.ErrorCode);
        Assert.Equal(4, batch.Output["step_count"]!.GetValue<int>());
        var steps = batch.Output["steps"]!.AsArray();
        Assert.Equal("snapshot", steps[0]!["action"]!.GetValue<string>());
        Assert.Equal("click", steps[1]!["action"]!.GetValue<string>());
        Assert.Equal("e1", engine.Clicked);
        Assert.Equal("Ada", engine.Typed);

        var failed = await ExecuteAsync(
            service,
            "batch",
            ("steps", new JsonArray(Step("snapshot"), Step("click"))));
        Assert.Equal("browser_arguments_invalid", failed.ErrorCode);
        Assert.Contains("Step 2 (click)", failed.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_rejects_nested_open_close_batch_empty_and_oversize()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);

        var beforeOpen = await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step("snapshot"))));
        Assert.Equal("browser_not_open", beforeOpen.ErrorCode);

        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray()))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step("open"))))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step("close"))))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step("batch"))))).ErrorCode);

        var tooMany = Enumerable.Range(0, RuAgent.Protocol.BrowserLimits.MaxBatchSteps + 1).Select(_ => Step("snapshot")).ToArray();
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray(tooMany.Cast<JsonNode?>().ToArray())))).ErrorCode);
    }

    private static JsonObject Step(string action, params (string Name, JsonNode? Value)[] arguments)
    {
        var step = new JsonObject { ["action"] = action };
        foreach (var (name, value) in arguments) step[name] = value?.DeepClone();
        return step;
    }

    private static async Task<ToolExecutionOutcome> ExecuteAsync(
        BrowserUseService service,
        string action,
        params (string Name, JsonNode? Value)[] arguments)
    {
        var payload = new JsonObject { ["action"] = action };
        foreach (var (name, value) in arguments)
        {
            payload[name] = value?.DeepClone();
        }

        return await service.ExecuteAsync("run", "session", payload, CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        Assert.True(condition(), "The condition did not become true in time.");
    }

    private sealed class FakeEngine : IBrowserEngine
    {
        public bool IsAvailable { get; init; } = true;

        public BrowserEngineException? Failure { get; init; }

        public bool Opened { get; private set; }

        public bool Closed { get; private set; }

        public bool Disposed { get; private set; }

        public bool LastHeadless { get; private set; }

        public string? Clicked { get; private set; }

        public string? Typed { get; private set; }

        public bool TypedReplace { get; private set; }

        public string? Filled { get; private set; }

        public string? Pressed { get; private set; }

        public Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Opened = true;
            LastHeadless = headless;
            return Task.FromResult(new BrowserPageHandle("target", "session"));
        }

        public Task NavigateAsync(BrowserPageHandle page, string url, CancellationToken cancellationToken)
        {
            _ = page;
            _ = url;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }

        public Task<BrowserSnapshot> SnapshotAsync(
            BrowserPageHandle page,
            int maxElements,
            int maxTextChars,
            CancellationToken cancellationToken)
        {
            _ = page;
            _ = maxElements;
            _ = maxTextChars;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new BrowserSnapshot(
                "http://127.0.0.1/",
                "Test",
                [new BrowserSnapshotElement("e1", "button", "Go", string.Empty, false, false)],
                "hello",
                TextTruncated: false));
        }

        public Task ClickAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken)
        {
            _ = page;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Clicked = elementRef;
            return Task.CompletedTask;
        }

        public Task TypeAsync(
            BrowserPageHandle page,
            string? elementRef,
            string text,
            bool replace,
            CancellationToken cancellationToken)
        {
            _ = page;
            _ = elementRef;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Typed = text;
            TypedReplace = replace;
            return Task.CompletedTask;
        }

        public Task FillAsync(BrowserPageHandle page, string elementRef, string text, CancellationToken cancellationToken)
        {
            _ = page;
            _ = elementRef;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Filled = text;
            return Task.CompletedTask;
        }

        public Task PressAsync(BrowserPageHandle page, string key, CancellationToken cancellationToken)
        {
            _ = page;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Pressed = key;
            return Task.CompletedTask;
        }

        public Task<string> EvaluateAsync(BrowserPageHandle page, string expression, CancellationToken cancellationToken)
        {
            _ = page;
            _ = expression;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            return Task.FromResult("\"ok\"");
        }

        public Task<string> TitleAsync(BrowserPageHandle page, CancellationToken cancellationToken)
        {
            _ = page;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            return Task.FromResult("Test");
        }

        public Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(
            BrowserPageHandle page,
            int maxEntries,
            CancellationToken cancellationToken)
        {
            _ = page;
            _ = maxEntries;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<BrowserConsoleEntry>>([]);
        }

        public Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(
            BrowserPageHandle page,
            int maxEntries,
            CancellationToken cancellationToken)
        {
            _ = page;
            _ = maxEntries;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<BrowserNetworkEntry>>([]);
        }

        public Task ClosePageAsync(BrowserPageHandle page, CancellationToken cancellationToken)
        {
            _ = page;
            _ = cancellationToken;
            Closed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
