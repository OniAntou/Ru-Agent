using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

/// <summary>
/// Focused coverage for the in-process factory surface the shared conformance
/// suite does not reach: the approval bridge (Host approval id back to the
/// runtime pending approval), the cached-token resume, approval revoke, and the
/// session tasks/grants factory paths. Read-only tools only; no processes.
/// </summary>
public sealed class InProcessToolGatewayApprovalTests : IDisposable
{
    private readonly InProcessExecutionFixture _fixture = new();
    private readonly string _externalPath;
    private readonly RunId _runId = new("run_" + new string('a', 32));

    public InProcessToolGatewayApprovalTests()
    {
        _externalPath = Path.Combine(
            Path.GetTempPath(),
            "ru-inprocess-external-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(_externalPath, "external-content");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_externalPath);
        }
        catch (IOException)
        {
        }

        _fixture.Dispose();
    }

    [Fact]
    public async Task Ask_session_directory_grant_resumes_executes_and_replays_through_the_grant()
    {
        var gateway = await CreateGateway();
        var ask = await gateway.EvaluateAsync(ReadProposal(), CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, ask.Decision);
        Assert.Equal("external_directory_required", ask.SafeCode);

        var (authorized, token) = await _fixture.Factory.RespondApprovalAsync(
            _runId.Value,
            ask.ApprovalId!.Value.Value,
            "allow_session_directory",
            CancellationToken.None);
        Assert.True(authorized);
        Assert.False(string.IsNullOrWhiteSpace(token));

        // The resolved approval is cached under the original intent key: the
        // resumed proposal allows without a second ask and carries the exact
        // authorization token the authority issued.
        var resumed = ReadProposal();
        var allow = await gateway.EvaluateAsync(resumed, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, allow.Decision);
        Assert.Equal("auth:" + token, allow.CanonicalIntentToken);

        var first = await ExecutePreparedAsync(gateway, resumed, allow);
        Assert.Equal(ToolExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal("external-content", ParseModelContent(first)["content"]!.GetValue<string>());

        // The authority consumes its authorization token on first use, so the
        // cached copy is single-use too: a repeated identical proposal must
        // re-evaluate through the session directory grant instead of replaying
        // the consumed token into decision_stale.
        var secondProposal = ReadProposal();
        var secondAllow = await gateway.EvaluateAsync(secondProposal, CancellationToken.None);
        Assert.Equal(PolicyDecision.Allow, secondAllow.Decision);
        var second = await ExecutePreparedAsync(gateway, secondProposal, secondAllow);
        Assert.Equal(ToolExecutionOutcome.Succeeded, second.Outcome);
    }

    [Fact]
    public async Task RevokeApproval_denies_pending_and_unknown_answers_false()
    {
        var gateway = await CreateGateway();
        var ask = await gateway.EvaluateAsync(ReadProposal(), CancellationToken.None);
        Assert.Equal(PolicyDecision.Ask, ask.Decision);

        Assert.True(await gateway.RevokeApprovalAsync(ask.ApprovalId!.Value, CancellationToken.None));
        Assert.False(await gateway.RevokeApprovalAsync(ask.ApprovalId!.Value, CancellationToken.None));

        var (authorized, _) = await _fixture.Factory.RespondApprovalAsync(
            _runId.Value,
            ask.ApprovalId!.Value.Value,
            "allow_once",
            CancellationToken.None);
        Assert.False(authorized);
    }

    [Fact]
    public async Task Factory_lists_session_tasks_and_revokes_grants()
    {
        await CreateGateway();

        var tasks = await _fixture.Factory.ListSessionTasksAsync(
            _runId.Value,
            killExecutionId: null,
            CancellationToken.None);
        Assert.Equal("succeeded", tasks["status"]!.GetValue<string>());

        Assert.True(await _fixture.Factory.RevokeSessionGrantsAsync(
            "session-inprocess-approval",
            CancellationToken.None));

        var unknown = await _fixture.Factory.ListSessionTasksAsync(
            "run_" + new string('b', 32),
            killExecutionId: null,
            CancellationToken.None);
        Assert.Equal("failed", unknown["status"]!.GetValue<string>());
        Assert.Equal("broker_gateway_unavailable", unknown["error_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RevokeSessionGrants_without_a_live_gateway_is_true()
    {
        using var fixture = new InProcessExecutionFixture();
        Assert.True(await fixture.Factory.RevokeSessionGrantsAsync(
            "session-inprocess-approval",
            CancellationToken.None));
    }

    [Fact]
    public async Task RespondApprovalAsync_unknown_approval_answers_false()
    {
        await CreateGateway();

        var (authorized, token) = await _fixture.Factory.RespondApprovalAsync(
            _runId.Value,
            "apr_" + new string('9', 32),
            "allow_once",
            CancellationToken.None);

        Assert.False(authorized);
        Assert.Null(token);
    }

    [Fact]
    public async Task Session_directory_grant_resumes_external_edit_and_write()
    {
        var externalDir = Path.Combine(
            Path.GetTempPath(),
            "ru-inprocess-grant-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalDir);
        var editPath = Path.Combine(externalDir, "editable.txt");
        File.WriteAllText(editPath, "factory-edit-pre");
        var notePath = Path.Combine(externalDir, "note.md");
        try
        {
            var gateway = await CreateGateway();
            var editProposal = new ToolProposal(
                _runId,
                new ModelStepId("step_" + new string('0', 32)),
                new ToolCallId("call_" + new string('1', 32)),
                "provider_call_1",
                modelOrder: 1,
                "edit",
                "1.0.0",
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["path"] = editPath,
                    ["old_string"] = "factory-edit-pre",
                    ["new_string"] = "factory-edit-post",
                }));
            var editAsk = await gateway.EvaluateAsync(editProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, editAsk.Decision);
            Assert.Equal("external_directory_required", editAsk.SafeCode);

            var writeProposal = new ToolProposal(
                _runId,
                new ModelStepId("step_" + new string('0', 32)),
                new ToolCallId("call_" + new string('2', 32)),
                "provider_call_1",
                modelOrder: 1,
                "write",
                "1.0.0",
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["op"] = "write",
                    ["path"] = notePath,
                    ["content"] = "external-note-content",
                    ["overwrite"] = false,
                }));
            var writeAsk = await gateway.EvaluateAsync(writeProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, writeAsk.Decision);

            var (editAuthorized, _) = await _fixture.Factory.RespondApprovalAsync(
                _runId.Value,
                editAsk.ApprovalId!.Value.Value,
                "allow_session_directory",
                CancellationToken.None);
            Assert.True(editAuthorized);

            var editResumed = new ToolProposal(
                _runId,
                new ModelStepId("step_" + new string('0', 32)),
                new ToolCallId("call_" + new string('1', 32)),
                "provider_call_1",
                modelOrder: 1,
                "edit",
                "1.0.0",
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["path"] = editPath,
                    ["old_string"] = "factory-edit-pre",
                    ["new_string"] = "factory-edit-post",
                }));
            var editAllow = await gateway.EvaluateAsync(editResumed, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, editAllow.Decision);
            var editReceipt = await ExecutePreparedAsync(gateway, editResumed, editAllow);
            Assert.Equal(ToolExecutionOutcome.Succeeded, editReceipt.Outcome);
            Assert.Equal("factory-edit-post", await File.ReadAllTextAsync(editPath));

            // The same session grant authorizes the write without a second ask.
            var writeResumed = new ToolProposal(
                _runId,
                new ModelStepId("step_" + new string('0', 32)),
                new ToolCallId("call_" + new string('2', 32)),
                "provider_call_1",
                modelOrder: 1,
                "write",
                "1.0.0",
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["op"] = "write",
                    ["path"] = notePath,
                    ["content"] = "external-note-content",
                    ["overwrite"] = false,
                }));
            var writeAllow = await gateway.EvaluateAsync(writeResumed, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, writeAllow.Decision);
            var writeReceipt = await ExecutePreparedAsync(gateway, writeResumed, writeAllow);
            Assert.Equal(ToolExecutionOutcome.Succeeded, writeReceipt.Outcome);
            Assert.Contains("external-note-content", await File.ReadAllTextAsync(notePath));
        }
        finally
        {
            try
            {
                Directory.Delete(externalDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<ToolExecutionReceipt> ExecutePreparedAsync(
        global::RuBackend.Harness.Broker.InProcessToolGateway gateway,
        ToolProposal proposal,
        ToolProposalDecision decision)
    {
        var prepared = await gateway.PrepareAsync(
            new ApprovedToolExecution(
                proposal,
                new ExecutionId("exec_" + Guid.NewGuid().ToString("N")),
                decision.CanonicalIntentToken!,
                decision.GatewayGeneration),
            CancellationToken.None);
        return await gateway.ExecuteAsync(prepared, _ => ValueTask.CompletedTask, CancellationToken.None);
    }

    private async Task<global::RuBackend.Harness.Broker.InProcessToolGateway> CreateGateway() =>
        Assert.IsType<global::RuBackend.Harness.Broker.InProcessToolGateway>(
            await _fixture.Factory.CreateAsync(
                _fixture.WorkspaceRoot,
                Snapshot(),
                _runId,
                agentSessionId: "session-inprocess-approval",
                CancellationToken.None));

    private ToolProposal ReadProposal() =>
        new(
            _runId,
            new ModelStepId("step_" + new string('0', 32)),
            new ToolCallId("call_" + new string('0', 32)),
            "provider_call_1",
            modelOrder: 1,
            "read",
            "1.0.0",
            JsonSerializer.SerializeToElement(new JsonObject { ["path"] = _externalPath }));

    private static JsonNode ParseModelContent(ToolExecutionReceipt receipt) =>
        JsonNode.Parse(Encoding.UTF8.GetString(receipt.ModelContent.Span))
        ?? throw new InvalidOperationException("receipt model content is empty");

    private static PolicySnapshot Snapshot() =>
        PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_" + new string('1', 32)),
                "VOL-INPROCESS",
                "fid-inprocess",
                new string('a', 64)),
            [],
            DateTimeOffset.Parse(
                "2026-08-24T12:00:00.0000000+00:00",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
}
