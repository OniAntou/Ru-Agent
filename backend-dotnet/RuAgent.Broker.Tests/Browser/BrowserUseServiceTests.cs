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

    [Fact]
    public async Task Screenshot_needs_image_input_and_returns_a_png_with_its_size()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var textOnly = await ExecuteAsync(service, "screenshot");
        Assert.Equal("image_unsupported_by_model", textOnly.ErrorCode);

        var shot = await service.ExecuteAsync(
            "run",
            "session",
            new JsonObject { ["action"] = "screenshot", ["ref"] = "e1" },
            CancellationToken.None,
            new BrowserCallOptions(ImageInputAccepted: true));
        Assert.Null(shot.ErrorCode);
        Assert.NotNull(shot.Image);
        Assert.Equal("image/png", shot.Image!.MediaType);
        Assert.Equal(200, shot.Output["width"]!.GetValue<int>());
        Assert.Equal(100, shot.Output["height"]!.GetValue<int>());
        Assert.Equal("e1", shot.Output["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scroll_hover_select_and_find_reach_the_page_with_bounded_arguments()
    {
        var engine = new FakeEngine
        {
            Evaluate = script => script.Contains("HTMLSelectElement", StringComparison.Ordinal)
                ? """{"ok":true,"value":"vn","label":"Viet Nam"}"""
                : script.Contains("matches: results", StringComparison.Ordinal)
                    ? """{"matches":[{"ref":"e9","role":"button","name":"Save"}],"total":3}"""
                    : """{"x":0,"y":500,"at_end":false}""",
        };
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var scroll = await ExecuteAsync(service, "scroll", ("direction", "down"), ("amount", 5));
        Assert.Null(scroll.ErrorCode);
        Assert.Equal(500, scroll.Output["y"]!.GetValue<int>());
        Assert.Contains("window.scrollBy(0,500)", engine.Scripts[^1], StringComparison.Ordinal);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "scroll", ("direction", "diagonal"))).ErrorCode);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "scroll", ("amount", 99))).ErrorCode);

        Assert.Null((await ExecuteAsync(service, "hover", ("ref", "e2"))).ErrorCode);
        Assert.Equal("e2", engine.Hovered);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "hover")).ErrorCode);

        var select = await ExecuteAsync(service, "select", ("ref", "e3"), ("value", "Viet Nam"));
        Assert.Null(select.ErrorCode);
        Assert.Equal("vn", select.Output["value"]!.GetValue<string>());
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "select", ("ref", "e3"))).ErrorCode);

        var find = await ExecuteAsync(service, "find", ("query", "save"));
        Assert.Null(find.ErrorCode);
        Assert.Equal("e9", Assert.Single(find.Output["matches"]!.AsArray())!["ref"]!.GetValue<string>());
        Assert.Equal(3, find.Output["truncated_from"]!.GetValue<int>());
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "find", ("query", ""))).ErrorCode);
    }

    [Fact]
    public async Task History_moves_wait_until_the_page_settles()
    {
        var engine = new FakeEngine
        {
            Evaluate = script => script.Contains("history.back()", StringComparison.Ordinal)
                ? """{"token":"t1","href":"http://127.0.0.1/b"}"""
                : """{"done":true,"url":"http://127.0.0.1/a","title":"A"}""",
        };
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var back = await ExecuteAsync(service, "back");
        Assert.Null(back.ErrorCode);
        Assert.Equal("http://127.0.0.1/a", back.Output["url"]!.GetValue<string>());
        Assert.Equal("A", back.Output["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dialog_sets_the_next_answer_and_answered_dialogs_are_reported()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var dialog = await ExecuteAsync(service, "dialog", ("accept", true), ("text", "Ada"));
        Assert.Null(dialog.ErrorCode);
        Assert.Equal((true, "Ada"), engine.NextDialog);
        Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "dialog")).ErrorCode);

        engine.PendingDialogs.Add(new BrowserDialog("confirm", "Delete?", Accepted: false));
        var click = await ExecuteAsync(service, "click", ("ref", "e1"));
        var reported = Assert.Single(click.Output["dialogs"]!.AsArray())!;
        Assert.Equal("confirm", reported["kind"]!.GetValue<string>());
        Assert.False(reported["accepted"]!.GetValue<bool>());
        Assert.Null((await ExecuteAsync(service, "snapshot")).Output["dialogs"]);
    }

    [Fact]
    public async Task Popups_become_session_tabs_and_tab_actions_choose_the_active_page()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        engine.PendingPopups.Add(new BrowserPageHandle("popup", "popup-session"));
        var click = await ExecuteAsync(service, "click", ("ref", "e1"));
        Assert.Equal(2, Assert.Single(click.Output["new_tabs"]!.AsArray())!["tab"]!.GetValue<int>());

        var newTab = await ExecuteAsync(service, "new_tab", ("url", "https://example.com/"));
        Assert.Null(newTab.ErrorCode);
        Assert.Equal(3, newTab.Output["tab"]!.GetValue<int>());

        var tabs = await ExecuteAsync(service, "tabs");
        Assert.Equal(3, tabs.Output["tabs"]!.AsArray().Count);
        Assert.True(tabs.Output["tabs"]![2]!["active"]!.GetValue<bool>());

        Assert.Null((await ExecuteAsync(service, "switch_tab", ("tab", 2))).ErrorCode);
        Assert.Equal("browser_tab_invalid", (await ExecuteAsync(service, "switch_tab", ("tab", 7))).ErrorCode);

        var closed = await ExecuteAsync(service, "close_tab", ("tab", 3));
        Assert.Null(closed.ErrorCode);
        Assert.Equal(2, closed.Output["tab"]!.GetValue<int>());
        Assert.Null((await ExecuteAsync(service, "close_tab")).ErrorCode);
        Assert.Equal("browser_tab_invalid", (await ExecuteAsync(service, "close_tab")).ErrorCode);

        for (var index = 1; index < RuAgent.Protocol.BrowserLimits.MaxTabs; index++)
        {
            Assert.Null((await ExecuteAsync(service, "new_tab")).ErrorCode);
        }

        Assert.Equal("browser_tab_limit", (await ExecuteAsync(service, "new_tab")).ErrorCode);
        engine.PendingPopups.Add(new BrowserPageHandle("extra", "extra-session"));
        var dropped = await ExecuteAsync(service, "snapshot");
        Assert.True(Assert.Single(dropped.Output["new_tabs"]!.AsArray())!["closed"]!.GetValue<bool>());

        var before = engine.PagesClosed;
        Assert.Null((await ExecuteAsync(service, "close")).ErrorCode);
        Assert.Equal(before + RuAgent.Protocol.BrowserLimits.MaxTabs, engine.PagesClosed);
    }

    [Fact]
    public async Task Upload_uses_only_the_paths_the_host_resolved()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        var missing = await ExecuteAsync(service, "upload", ("ref", "e1"), ("paths", new JsonArray("a.txt")));
        Assert.Equal("browser_upload_paths_required", missing.ErrorCode);
        Assert.Null(engine.UploadedPaths);

        var uploaded = await service.ExecuteAsync(
            "run",
            "session",
            new JsonObject { ["action"] = "upload", ["ref"] = "e1", ["paths"] = new JsonArray("a.txt") },
            CancellationToken.None,
            new BrowserCallOptions(UploadPaths: [@"C:\work\a.txt"]));
        Assert.Null(uploaded.ErrorCode);
        Assert.Equal([@"C:\work\a.txt"], engine.UploadedPaths);
    }

    [Fact]
    public async Task A_run_that_pinned_the_persistent_profile_opens_through_its_engine()
    {
        var ephemeral = new FakeEngine();
        var persistent = new FakeEngine();
        await using var service = new BrowserUseService(ephemeral, persistent);

        var open = await service.ExecuteAsync(
            "run",
            "session",
            new JsonObject { ["action"] = "open" },
            CancellationToken.None,
            new BrowserCallOptions(PersistentProfile: true));
        Assert.Null(open.ErrorCode);
        Assert.Equal("persistent", open.Output["profile"]!.GetValue<string>());
        Assert.True(persistent.Opened);
        Assert.False(ephemeral.Opened);

        Assert.Null((await service.ExecuteAsync("run", "other", new JsonObject { ["action"] = "open" }, CancellationToken.None)).ErrorCode);
        Assert.True(ephemeral.Opened);
    }

    [Fact]
    public async Task Batch_rejects_screenshot_tab_and_upload_steps()
    {
        var engine = new FakeEngine();
        await using var service = new BrowserUseService(engine);
        Assert.Null((await ExecuteAsync(service, "open")).ErrorCode);

        foreach (var action in new[] { "screenshot", "tabs", "new_tab", "switch_tab", "close_tab", "upload" })
        {
            Assert.Equal("browser_arguments_invalid", (await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step(action))))).ErrorCode);
        }

        var batch = await ExecuteAsync(service, "batch", ("steps", new JsonArray(Step("hover", ("ref", "e4")), Step("dialog", ("accept", false)))));
        Assert.Null(batch.ErrorCode);
        Assert.Equal("e4", engine.Hovered);
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

        public string? Hovered { get; private set; }

        public int PagesOpened { get; private set; }

        public int PagesClosed { get; private set; }

        public (bool Accept, string? Text)? NextDialog { get; private set; }

        public List<BrowserDialog> PendingDialogs { get; } = [];

        public List<BrowserPageHandle> PendingPopups { get; } = [];

        public IReadOnlyList<string>? UploadedPaths { get; private set; }

        public List<string> Scripts { get; } = [];

        /// <summary>Answers page scripts; null falls back to the JSON string "ok".</summary>
        public Func<string, string?>? Evaluate { get; init; }

        public Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Opened = true;
            LastHeadless = headless;
            PagesOpened++;
            return Task.FromResult(new BrowserPageHandle("target" + PagesOpened, "session" + PagesOpened));
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
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            Scripts.Add(expression);
            return Task.FromResult(Evaluate?.Invoke(expression) ?? "\"ok\"");
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
            PagesClosed++;
            return Task.CompletedTask;
        }

        public Task<byte[]> ScreenshotAsync(BrowserPageHandle page, string? elementRef, CancellationToken cancellationToken)
        {
            _ = page;
            _ = elementRef;
            _ = cancellationToken;
            if (Failure is not null) throw Failure;
            var png = new byte[32];
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }.CopyTo(png, 0);
            png[19] = 200;
            png[23] = 100;
            return Task.FromResult(png);
        }

        public Task HoverAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken)
        {
            _ = page;
            _ = cancellationToken;
            Hovered = elementRef;
            return Task.CompletedTask;
        }

        public void SetNextDialogAnswer(BrowserPageHandle page, bool accept, string? promptText)
        {
            _ = page;
            NextDialog = (accept, promptText);
        }

        public IReadOnlyList<BrowserDialog> TakeDialogs(BrowserPageHandle page)
        {
            _ = page;
            var taken = PendingDialogs.ToArray();
            PendingDialogs.Clear();
            return taken;
        }

        public IReadOnlyList<BrowserPageHandle> TakeOpenedPages(BrowserPageHandle page)
        {
            _ = page;
            var taken = PendingPopups.ToArray();
            PendingPopups.Clear();
            return taken;
        }

        public Task SetFilesAsync(BrowserPageHandle page, string elementRef, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            _ = page;
            _ = elementRef;
            _ = cancellationToken;
            UploadedPaths = paths;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
