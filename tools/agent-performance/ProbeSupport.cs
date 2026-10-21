using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Business.Settings;
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Prompting;
using RuBackend.Providers;
using RuBackend.Harness.Workspace;
using RuBackend.Harness.Broker;
using RuAgent.Harness.Core.Policy;
using EventId = RuAgent.Harness.Core.EventId;

sealed class Timings
{
    public Dictionary<string, double> Values { get; } = new();
    public void Add(string key, double value) => Values[key] = Values.GetValueOrDefault(key) + value;
    public async Task<T> Time<T>(string key, Func<Task<T>> action) { var s = Stopwatch.StartNew(); try { return await action(); } finally { Add(key, s.Elapsed.TotalMilliseconds); } }
}
sealed class TimedStore(IHarnessEventStore inner, Timings t) : IHarnessEventStore
{
    public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun r, CancellationToken c) => t.Time("db.create", () => inner.TryCreateRunAsync(r, c));
    public Task<AgentRun?> GetRunAsync(RunId r, CancellationToken c) => t.Time("db.run", () => inner.GetRunAsync(r, c));
    public Task<HarnessCommitResult> CommitAsync(HarnessCommit r, CancellationToken c) => t.Time("db.commit", () => inner.CommitAsync(r, c));
    public IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId r, long a, CancellationToken c) => inner.ReadEventsAsync(r, a, c);
}
sealed class TimedContext(IModelContextSource inner, Timings t) : IModelContextSource
{
    public void Reset(IModelContextSource replacement) => inner = replacement;
    public Task<ModelContextSourceSnapshot> LoadAsync(RunId r, CancellationToken c) => t.Time("context.read", () => inner.LoadAsync(r, c));
}
sealed class TimedPrompt(ISystemPromptContextProvider inner, Timings t) : ISystemPromptContextProvider
{
    public Task<SystemPromptContext> BuildAsync(AgentRun r, ModelContextSnapshot s, CancellationToken c) => t.Time("prompt.build", () => inner.BuildAsync(r, s, c));
}
sealed class TimedRunner(InProcessModelStepRunner inner, Timings t) : IProviderBackedModelStepRunner, IModelStepRequestPreparer
{
    public void Reset(InProcessModelStepRunner replacement) => inner = replacement;
    public Task<IPreparedModelStep> PrepareAsync(ModelStepRequest r, CancellationToken c) => t.Time("request.prepare", () => inner.PrepareAsync(r, c));
    public Task<ModelStepResult> RunStepAsync(ModelStepRequest r, Func<AssistantDelta, ValueTask> d, CancellationToken c) => t.Time("model.invoke", () => inner.RunStepAsync(r, d, c));
}
sealed class InstantProvider(Timings t) : HttpMessageHandler
{
    private int _calls;
    private System.Text.Json.Nodes.JsonArray? _previousMessages;
    private System.Text.Json.Nodes.JsonObject? _previousEnvelope;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken c)
    {
        int n = Interlocked.Increment(ref _calls);
        var timer = Stopwatch.StartNew();
        var body = await request.Content!.ReadAsStringAsync(c);
        t.Add("wire.bytes", Encoding.UTF8.GetByteCount(body));
        t.Add("http.handler", timer.Elapsed.TotalMilliseconds);
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
        var messages = envelope["messages"]!.AsArray();
        envelope.Remove("messages");
        if (_previousEnvelope is not null)
            t.Add("prefix.envelope.break", System.Text.Json.Nodes.JsonNode.DeepEquals(_previousEnvelope, envelope) ? 0 : 1);
        _previousEnvelope = envelope;
        if (_previousMessages is { Count: > 0 } previous)
        {
            int common = 0;
            while (common < previous.Count && common < messages.Count
                && System.Text.Json.Nodes.JsonNode.DeepEquals(previous[common], messages[common])) common++;
            t.Add("prefix.break", common == previous.Count ? 0 : 1);
            t.Add("prefix.reused_message_fraction", common / (double)previous.Count);
        }
        _previousMessages = (System.Text.Json.Nodes.JsonArray)messages.DeepClone();
        var data = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[] { new { index = 0, id = "probe_call_" + n, type = "function", function = new { name = "read", arguments = JsonSerializer.Serialize(new { path = "probe_" + n + ".txt" }) } } } }, finish_reason = "tool_calls" } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + data + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    }
}
sealed class TimedGateway(IToolGateway inner, Timings t) : IToolGateway
{
    public Task<ToolProposalDecision> EvaluateAsync(ToolProposal p, CancellationToken c) => t.Time("tool.evaluate", () => inner.EvaluateAsync(p, c));
    public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution e, CancellationToken c) => t.Time("tool.prepare", () => inner.PrepareAsync(e, c));
    public Task<ToolExecutionReceipt> ExecuteAsync(PreparedToolExecution e, Func<ToolOutputChunk, ValueTask> d, CancellationToken c) => t.Time("tool.execute", () => inner.ExecuteAsync(e, d, c));
    public Task CancelAsync(ExecutionId e, CancellationToken c) => inner.CancelAsync(e, c);
}
