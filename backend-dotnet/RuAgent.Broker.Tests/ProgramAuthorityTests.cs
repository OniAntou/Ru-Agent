using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProgramAuthorityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-program-" + Guid.NewGuid().ToString("N"));
    private readonly ToolExecutionService _tools;
    private readonly BrokerExecutionRuntime _runtime;
    private readonly ProgramRunnerTests.MemoryJournal _journal = new();
    public ProgramAuthorityTests()
    {
        Directory.CreateDirectory(_root);
        var session = BrokerSession.CreateForHost(_root, DateTimeOffset.MaxValue);
        _tools = new ToolExecutionService(session, _root);
        _runtime = new BrokerExecutionRuntime(session, _tools, programJournal: _journal);
    }

    [Fact]
    public async Task NestedToolsUseRealBrokerAndReplayDoesNotWriteAgain()
    {
        await OpenAsync(EnvelopeTestSnapshots.StandardEmpty());
        const string code = "tools.write_file({path:'new.txt',content:'hello'}); return tools.read_file({path:'new.txt'}).output.content;";
        var result = await ExecuteAsync(code, "call_one");
        Assert.Equal("succeeded", result.Receipt!.Status);
        Assert.Equal("hello", result.Receipt.Output["result"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(_root, "new.txt"), "changed outside program");
        Assert.Equal("succeeded", (await ExecuteAsync(code, "call_one")).Receipt!.Status);
        Assert.Equal("changed outside program", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public async Task PlanCannotWriteEvenInsideProgram()
    {
        await OpenAsync(EnvelopeTestSnapshots.Snapshot(1, "plan", []));
        var result = await ExecuteAsync("tools.write_file({path:'denied.txt',content:'x'}); return 'done';", "call_denied");
        Assert.Equal("failed", result.Receipt!.Status);
        Assert.False(File.Exists(Path.Combine(_root, "denied.txt")));
    }

    [Fact]
    public async Task JournalFailureAfterMutationRemainsUnknownThroughRuntimeReply()
    {
        await OpenAsync(EnvelopeTestSnapshots.StandardEmpty());
        _journal.FailReceipt = true;
        const string code = "tools.write_file({path:'uncertain.txt',content:'first'}); return 1;";
        var failed = await ExecuteAsync(code, "call_crash");
        Assert.Equal("program_execution_unknown", failed.Receipt!.ErrorCode);
        Assert.True(failed.Receipt.Output["effects_unknown"]!.GetValue<bool>());
        File.WriteAllText(Path.Combine(_root, "uncertain.txt"), "observed afterwards");
        _journal.FailReceipt = false;
        var replay = await ExecuteAsync(code, "call_crash");
        Assert.Equal("program_execution_unknown", replay.Receipt!.ErrorCode);
        Assert.Equal("observed afterwards", File.ReadAllText(Path.Combine(_root, "uncertain.txt")));
    }

    [Fact]
    public async Task ExplicitNestedAskStopsWithoutGrantingPermission()
    {
        await OpenAsync(EnvelopeTestSnapshots.StandardWithRules(PolicyEvalSnapshots.Rule("ask-writes", "ask", PolicyEvalSnapshots.Matcher(toolId: "write_file"))));
        var result = await ExecuteAsync("tools.write_file({path:'denied.txt',content:'x'});", "call_ask");
        Assert.Equal("program_requires_direct_call", result.Receipt!.ErrorCode);
        Assert.Equal("write_file", result.Receipt.Output["blocked_call"]!["tool_id"]!.GetValue<string>());
        Assert.Equal("denied.txt", result.Receipt.Output["blocked_call"]!["arguments"]!["path"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_root, "denied.txt")));
    }

    [Fact]
    public async Task AuthorizationForOneInvocationCannotExecuteAnother()
    {
        await OpenAsync(EnvelopeTestSnapshots.StandardEmpty());
        var request = Request(AgentMessageTypes.ToolEvaluate, "return 1;", "call_a");
        var decision = await _runtime.EvaluateAsync(request, default);
        var changed = Request(AgentMessageTypes.ToolExecute, "return 1;", "call_b");
        changed.Payload["authorization_token"] = decision.Decision!.AuthorizationToken;
        Assert.Equal("decision_stale", (await _runtime.ExecuteAuthorizedAsync(changed, default)).ErrorCode);
    }

    private async Task OpenAsync(RuAgent.Protocol.Harness.BrokerPolicySnapshotWire snapshot)
    {
        var opened = await _runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen,
            "session", null, 10_000, EnvelopeTestSnapshots.RunOpenPayload("run", "session", _root, snapshot)), default);
        Assert.Null(opened.Error);
    }
    private AgentEnvelope Request(string type, string code, string call) => new(2, Guid.NewGuid().ToString("N"), type,
        "session", null, 30_000, new JsonObject { ["run_id"] = "run", ["tool_id"] = "run_code", ["tool_version"] = "1.0.0",
            ["tool_call_id"] = call, ["arguments"] = new JsonObject { ["code"] = code } });
    private async Task<ToolExecutionReply> ExecuteAsync(string code, string call)
    {
        var request = Request(AgentMessageTypes.ToolEvaluate, code, call);
        var decision = await _runtime.EvaluateAsync(request, default);
        Assert.Equal("allow", decision.Decision!.Decision);
        request = request with { MessageType = AgentMessageTypes.ToolExecute };
        request.Payload["authorization_token"] = decision.Decision.AuthorizationToken;
        return await _runtime.ExecuteAuthorizedAsync(request, default);
    }
    public void Dispose()
    {
        _runtime.Dispose();
        _tools.Dispose();
        Directory.Delete(_root, true);
    }
}
