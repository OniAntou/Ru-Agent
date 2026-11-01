using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class ChatUsageReaderTests
{
    internal const string Owner = "sess_usage";
    internal static string RunId(int index = 1) => "run_" + index.ToString("x32");
    internal static string Payload(int index = 1) => "payload_" + index.ToString("x32");
    internal static readonly PublicHostSession Session = new(new Uri("http://127.0.0.1:1/"), "synthetic-usage-token", null!);
    internal static readonly CatalogModelDetails Rates = new(10000, 1, 2, .5, 3);

    internal sealed class Host : HttpMessageHandler
    {
        internal List<string> Requests = [];
        internal int RunCount = 1;
        internal bool ForeignRun, ForeignEvent, Compact, Duplicate, MissingPayload, BadReport, NoRates, FailList;
        internal int ReportCount = 1;
        internal Func<int, string>? ModelForRun;
        internal object Report = new { input_tokens = 1000, output_tokens = 100, total_tokens = 1100,
            cache_read_input_tokens = 400, cache_write_input_tokens = 0, input_tokens_include_cache = true,
            model = "synthetic-model", context_tokens = new ContextTokenBreakdown(100, 200, 700) };
        internal static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        internal static string Event(string run, long sequence, string type, string? payload = null, string owner = Owner) =>
            "id: " + sequence + "\nevent: message\ndata: " + JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1,
                "evt_" + sequence.ToString("x32"), run, owner, sequence, type, "2026-11-01T00:00:00Z", "completed", "synthetic", payload)) + "\n\n";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("synthetic-usage-token", request.Headers.Authorization!.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (path == $"/agent/v1/sessions/{Owner}/runs") return Task.FromResult(FailList
                ? Json(new { error_code = "unavailable" }, HttpStatusCode.ServiceUnavailable)
                : Json(new { items = Enumerable.Range(1, RunCount).Reverse().Select(index => new { run_id = RunId(index) }) }));
            if (path.EndsWith("/events"))
            {
                Assert.Contains("live=0", request.RequestUri.Query);
                var run = path.Split('/')[4];
                var body = new StringBuilder();
                for (var index = 1; index <= ReportCount; index++)
                    body.Append(Event(run, index, "model.usage_recorded", Payload(Duplicate ? 1 : index), ForeignEvent ? "other_session" : Owner));
                if (Compact) body.Append(Event(run, ReportCount + 1, "context.compacted"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") });
            }
            if (path.Contains("/payloads/")) return Task.FromResult(MissingPayload
                ? Json(new { error_code = "payload_missing" }, HttpStatusCode.NotFound)
                : Json(new { run_id = path.Split('/')[4], payload_id = path.Split('/')[6], kind = "model_usage", encoding = "json",
                    content = BadReport ? "{\"input_tokens\":-1}" : ReportJson(path.Split('/')[4]) }));
            if (path.StartsWith("/agent/v1/runs/")) return Task.FromResult(Json(new {
                run_id = path.Split('/')[4], session_id = ForeignRun ? "other_session" : Owner, workspace_id = "ws_usage",
                status = "completed", preset = "standard", policy_snapshot_version = 1, policy_snapshot_sha256 = new string('a', 64),
                version = 2, last_sequence = 2, created_at = "2026-11-01T00:00:00Z", updated_at = "2026-11-01T00:00:01Z",
                terminal_at = "2026-11-01T00:00:01Z", provider_backend = "OpenAI", provider_model = Model(path.Split('/')[4]),
                provider_protocol = "chat_completions", provider_calling_mode = "native", context_window_tokens = 10000,
                context_window_source = "provider_catalog", context_window_version = "synthetic", output_token_reserve = 1000 }));
            if (path == "/settings") return Task.FromResult(Json(new { status = "ok", backend = "OpenAI", openai_model = "synthetic-model" }));
            return Task.FromResult(Json(new { }, HttpStatusCode.NotFound));
        }

        private string Model(string run) => ModelForRun?.Invoke(Convert.ToInt32(run[^8..], 16)) ?? "synthetic-model";
        private string ReportJson(string run)
        {
            var report = JsonSerializer.SerializeToNode(Report)!;
            if (ModelForRun is not null) report["model"] = Model(run);
            return report.ToJsonString();
        }
    }

    internal static async Task<ChatUsageReading> Read(Host host, bool context = false, CancellationToken ct = default,
        Func<string?, string, CancellationToken, Task<CatalogModelDetails?>>? rates = null)
    {
        using var http = new HttpClient(host, disposeHandler: false);
        using var agents = new AgentApiClient(Session, http); using var sse = new AgentSseClient(Session, http);
        return await new ChatUsageReader(agents, sse, rates ?? ((_, _, _) => Task.FromResult<CatalogModelDetails?>(host.NoRates ? null : Rates)))
            .ReadAsync(Owner, null, context, ct);
    }

    [Fact]
    public async Task Replay_deduplicates_payloads_and_prices_disjoint_buckets_without_mutation()
    {
        using var host = new Host { Duplicate = true, ReportCount = 2 };
        var result = await Read(host); var row = Assert.Single(result.Runs);
        Assert.Null(row.Notice); Assert.Single(row.Reports); Assert.Equal(.001m, row.Cost);
        Assert.Single(host.Requests, request => request.Contains("/payloads/"));
        Assert.All(host.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task Recent_scope_is_bounded_and_context_uses_the_newest_run_only()
    {
        using var host = new Host { RunCount = 12 };
        var result = await Read(host); Assert.Equal(8, result.Runs.Count); Assert.Equal(12, result.ListedRuns);
        Assert.Equal(RunId(5), result.Runs[0].Run.RunId);
        host.Requests.Clear(); var context = await Read(host, context: true);
        Assert.Equal(RunId(12), Assert.Single(context.Runs).Run.RunId);
        Assert.Single(host.Requests, request => request.Contains("/events"));
    }

    [Fact]
    public async Task Compaction_clears_context_but_keeps_billed_reports()
    {
        using var host = new Host { Compact = true };
        var result = await Read(host); var row = Assert.Single(result.Runs);
        Assert.Single(row.Reports); Assert.Null(row.LatestContext);
        var context = ChatUsagePresentation.Context(result, null, 999999, 0);
        Assert.Contains("Published context window: 10,000", context);
        Assert.Contains("System prompt: unknown", context);
        Assert.Contains("Context used: unknown", context);
        var onlyContext = await Read(host, context: true);
        Assert.True(Assert.Single(onlyContext.Runs).ContextInvalidated);
        Assert.Empty(onlyContext.Runs[0].Reports);
    }

    [Fact]
    public async Task Foreign_run_is_refused_before_events_or_payload_reads()
    {
        using var host = new Host { ForeignRun = true };
        var error = await Assert.ThrowsAsync<AgentApiException>(() => Read(host));
        Assert.Equal("run_session_mismatch", error.ErrorCode);
        Assert.DoesNotContain(host.Requests, request => request.Contains("/events") || request.Contains("/payloads/"));
    }

    [Theory]
    [InlineData("foreign_event")]
    [InlineData("missing_payload")]
    [InlineData("bad_report")]
    [InlineData("over_limit")]
    public async Task Incomplete_or_untrusted_replay_never_becomes_a_cheap_partial_total(string reason)
    {
        using var host = new Host { ForeignEvent = reason == "foreign_event", MissingPayload = reason == "missing_payload",
            BadReport = reason == "bad_report", ReportCount = reason == "over_limit" ? 257 : 1 };
        var result = await Read(host); var row = Assert.Single(result.Runs);
        Assert.NotNull(row.Notice); Assert.Empty(row.Reports); Assert.Null(row.Cost);
        Assert.Contains("Estimated cost: unknown", ChatUsagePresentation.Usage(result));
        if (reason is "foreign_event" or "over_limit") Assert.DoesNotContain(host.Requests, request => request.Contains("/payloads/"));
    }

    [Fact]
    public async Task Pricing_uses_the_recorded_provider_model_and_keeps_missing_rates_unknown()
    {
        using var host = new Host { NoRates = true };
        var result = await Read(host, rates: (backend, model, _) => {
            Assert.Equal("OpenAI", backend); Assert.Equal("synthetic-model", model);
            return Task.FromResult<CatalogModelDetails?>(null);
        });
        Assert.Null(Assert.Single(result.Runs).Cost);
        Assert.Contains("Priced coverage: 0/1 runs · priced subtotal unknown", ChatUsagePresentation.Usage(result));
    }

    [Fact]
    public async Task Session_list_failure_is_not_an_empty_session()
    {
        using var host = new Host { FailList = true };
        await Assert.ThrowsAsync<AgentApiException>(() => Read(host));
    }

    [Fact]
    public async Task Cancellation_stops_before_any_request()
    {
        using var host = new Host(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(host, ct: stop.Token));
        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Billed_totals_sum_steps_while_peak_and_context_are_single_request_readings()
    {
        using var host = new Host { ReportCount = 2 };
        var result = await Read(host);
        var lines = ChatUsagePresentation.Usage(result);
        Assert.Contains("Billed tokens: 2,200", lines);
        Assert.Contains("Largest reported step: 1,100", lines);
        Assert.Contains("Cache hit rate (known accounting): 40%", lines);
        var context = ChatUsagePresentation.Context(result, null, 999999, 4096);
        Assert.Contains("Prompt tokens: 1,000 (provider report)", context);
        Assert.Contains("Context used: 10%", context);
        Assert.Contains("System prompt: 100", context);
        Assert.Contains("Unsent draft: ~4,096 tokens (separate estimate)", context);
    }

    [Fact]
    public async Task Mixed_model_runs_are_priced_at_their_own_pins_instead_of_current_selection()
    {
        using var host = new Host { RunCount = 2, ModelForRun = index => "model-" + index };
        var lookedUp = new List<string>();
        var result = await Read(host, rates: (backend, model, _) => {
            Assert.Equal("OpenAI", backend); lookedUp.Add(model);
            return Task.FromResult<CatalogModelDetails?>(Rates with { InputCost = model == "model-1" ? 1 : 2 });
        });
        Assert.Equal(["model-1", "model-2"], lookedUp);
        Assert.Contains("Estimated cost: $0.002600", ChatUsagePresentation.Usage(result));
    }

    [Fact]
    public async Task Cancellation_during_price_lookup_is_propagated_instead_of_publishing_partial_cost()
    {
        using var host = new Host(); using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(host, ct: stop.Token, rates: (_, _, token) => {
            stop.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<CatalogModelDetails?>(Rates);
        }));
    }

    [Fact]
    public async Task Missing_report_is_unknown_instead_of_a_free_run()
    {
        using var host = new Host { ReportCount = 0 };
        var result = await Read(host);
        Assert.Contains("Estimated cost: unknown", ChatUsagePresentation.Usage(result));
        Assert.Contains("Coverage: 0/1 runs reported · 0 distinct step reports", ChatUsagePresentation.Usage(result));
    }

    [Fact]
    public async Task Context_reads_only_the_latest_payload_and_never_looks_up_prices()
    {
        using var host = new Host { ReportCount = 10 };
        var result = await Read(host, context: true, rates: (_, _, _) => throw new Exception("Context must not query pricing"));
        Assert.Single(Assert.Single(result.Runs).Reports);
        Assert.Single(host.Requests, request => request.Contains("/payloads/"));
        Assert.Contains(host.Requests, request => request.Contains("/payloads/" + Payload(10)));
    }

    [Theory]
    [InlineData("context", false)]
    [InlineData("context", true)]
    [InlineData("usage", false)]
    [InlineData("usage", true)]
    public async Task Line_and_json_status_reports_do_not_create_sessions_runs_or_change_settings(string section, bool json)
    {
        using var host = new Host(); using var http = new HttpClient(host);
        using var output = new StringWriter(); using var errors = new StringWriter();
        using var reader = new StringReader("/status " + section + "\n/exit\n");
        using var commands = new AgentCommands(Session, http, output, errors, reader);
        Assert.Equal(0, await commands.ChatAsync(new ChatCommand(Path.GetTempPath(), null, "standard", json, Classic: true), default));
        Assert.Contains(json ? "session_id" : "Session:", output.ToString());
        Assert.DoesNotContain("unknown_command", errors.ToString());
        Assert.All(host.Requests, request => Assert.StartsWith("GET ", request));
        if (json)
            foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                using (JsonDocument.Parse(line)) { }
    }
}
