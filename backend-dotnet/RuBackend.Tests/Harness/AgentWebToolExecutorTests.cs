extern alias Broker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using Xunit;
using WebOptions = Broker::RuAgent.Broker.Configuration.WebOptions;

namespace RuBackend.Tests.Harness;

public sealed class AgentWebToolExecutorTests
{
    [Fact]
    public async Task Search_returns_bounded_citations_and_does_not_call_more_than_once()
    {
        var handler = new RecordingHandler(_ => HtmlResponse(string.Join(
            string.Empty,
            Enumerable.Range(1, 12).Select(index =>
                $"<a href=\"https://8.8.4.4/result-{index}\">Result {index}</a>"))));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        var output = await executor.ExecuteAsync(
            "web.search",
            new JsonObject { ["query"] = "ru" },
            CancellationToken.None);

        Assert.True(output["untrusted"]!.GetValue<bool>());
        Assert.True(output["citations"]!.AsArray().Count <= 10);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Fetch_returns_untrusted_sanitized_content()
    {
        var handler = new RecordingHandler(_ => HtmlResponse(
            "<html><title>Example</title><script>ignore-this-script</script><body>safe text</body></html>"));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        var output = await executor.ExecuteAsync(
            "web.fetch",
            new JsonObject { ["url"] = "https://8.8.8.8/page" },
            CancellationToken.None);

        var content = output["content"]!.GetValue<string>();
        Assert.True(output["untrusted"]!.GetValue<bool>());
        Assert.Contains("[untrusted_web_reference]", content, StringComparison.Ordinal);
        Assert.Contains("safe text", content, StringComparison.Ordinal);
        Assert.DoesNotContain("ignore-this-script", content, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Invalid_tool_and_arguments_are_rejected_before_http()
    {
        var handler = new RecordingHandler(_ => HtmlResponse("<p>unused</p>"));
        using var http = new HttpClient(handler);
        var executor = CreateExecutor(http);

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(
            "web.unknown",
            new JsonObject(),
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(
            "web.search",
            new JsonObject(),
            CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Gateway_runs_web_search_through_read_only_lifecycle()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-web-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var handler = new RecordingHandler(_ => HtmlResponse(
            "<a href=\"https://8.8.4.4/result\">Result</a>"));
        using var http = new HttpClient(handler);
        try
        {
            var gateway = new HarnessToolGateway(
                tempRoot,
                ["web.search"],
                presetProvider: null,
                idGenerator: null,
                webExecutor: CreateExecutor(http));
            using var document = JsonDocument.Parse("{\"query\":\"ru\"}");
            var proposal = new ToolProposal(
                new RunId("run_11111111111111111111111111111111"),
                new ModelStepId("step_11111111111111111111111111111111"),
                new ToolCallId("call_11111111111111111111111111111111"),
                1,
                "web.search",
                "1.0.0",
                document.RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            Assert.Equal(ToolAccessKind.ReadOnly, decision.AccessKind);

            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    decision.CanonicalIntentToken!),
                CancellationToken.None);
            var chunks = new List<ToolOutputChunk>();
            var receipt = await gateway.ExecuteAsync(
                prepared,
                chunk =>
                {
                    chunks.Add(chunk);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
            Assert.False(receipt.SideEffectsPossible);
            Assert.NotEmpty(chunks);
            using var result = JsonDocument.Parse(receipt.ModelContent.ToArray());
            Assert.True(result.RootElement.GetProperty("untrusted").GetBoolean());
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_rejects_private_fetch_before_http()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-web-gateway-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var handler = new RecordingHandler(_ => HtmlResponse("<p>must not be reached</p>"));
        using var http = new HttpClient(handler);
        try
        {
            var gateway = new HarnessToolGateway(
                tempRoot,
                ["web.fetch"],
                presetProvider: null,
                idGenerator: null,
                webExecutor: CreateExecutor(http));
            using var document = JsonDocument.Parse("{\"url\":\"http://127.0.0.1/private\"}");
            var proposal = new ToolProposal(
                new RunId("run_22222222222222222222222222222222"),
                new ModelStepId("step_22222222222222222222222222222222"),
                new ToolCallId("call_22222222222222222222222222222222"),
                1,
                "web.fetch",
                "1.0.0",
                document.RootElement);

            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_22222222222222222222222222222222"),
                    decision.CanonicalIntentToken!),
                CancellationToken.None);
            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);

            Assert.Equal(ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.Equal("web_private_host", receipt.Code);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_returns_cancelled_receipt_when_caller_cancels_active_web_request()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ru-web-gateway-cancelled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CancellationHandler(requestStarted);
        using var http = new HttpClient(handler);
        try
        {
            var gateway = new HarnessToolGateway(
                tempRoot,
                ["web.search"],
                presetProvider: null,
                idGenerator: null,
                webExecutor: CreateExecutor(http));
            using var document = JsonDocument.Parse("{\"query\":\"ru\"}");
            var proposal = new ToolProposal(
                new RunId("run_33333333333333333333333333333333"),
                new ModelStepId("step_33333333333333333333333333333333"),
                new ToolCallId("call_33333333333333333333333333333333"),
                1,
                "web.search",
                "1.0.0",
                document.RootElement);
            var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    proposal,
                    new ExecutionId("exec_33333333333333333333333333333333"),
                    decision.CanonicalIntentToken!),
                CancellationToken.None);

            using var cancellation = new CancellationTokenSource();
            var executionTask = gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                cancellation.Token);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            var receipt = await executionTask;

            Assert.Equal(ToolExecutionOutcome.Cancelled, receipt.Outcome);
            Assert.Equal("process_cancelled", receipt.Code);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static AgentWebToolExecutor CreateExecutor(HttpClient http) =>
        new(
            new WebOptions(
                enabled: true,
                searchEndpoint: new Uri("https://8.8.8.8/html/")),
            http);

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class CancellationHandler(TaskCompletionSource<bool> requestStarted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation handler completed unexpectedly.");
        }
    }
}
