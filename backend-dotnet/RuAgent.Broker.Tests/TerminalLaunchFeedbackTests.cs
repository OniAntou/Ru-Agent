using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalLaunchFeedbackTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Invalid_executable_reports_resolution_error_before_registering_task(bool background, bool missing)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-launch-feedback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = BrokerSession.CreateForHost(Path.Combine(root, "session"), DateTimeOffset.UtcNow.AddMinutes(5));
        var snapshot = EnvelopeTestSnapshots.FullAccessEmpty();
        var sessionId = "launch-session";
        var runId = "run_" + Guid.NewGuid().ToString("N");
        var lease = BrokerRunLease.CreateValidated(runId, sessionId,
            snapshot.Workspace.WorkspaceId, root, snapshot.Workspace.VolumeSerialNumber,
            snapshot.Workspace.RootFileId, snapshot.Workspace.DisplayIdentityHash,
            snapshot.Preset, snapshot.Version, EnvelopeTestSnapshots.HashOf(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5), snapshot);
        Assert.True(session.TryOpenRunLease(lease, out var error), error);
        await using var service = new ToolExecutionService(session, root, trustedExecutableDirectories: []);
        try
        {
            var result = await service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "missing-launch", AgentMessageTypes.ToolExecute,
                sessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "bash", ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal", ["run_id"] = runId,
                    ["arguments"] = new JsonObject
                    {
                        ["mode"] = "run",
                        ["executable"] = missing ? Path.Combine(root, "missing.exe") : "cmd",
                        ["background"] = background, ["arguments"] = new JsonArray(),
                    },
                }), default);
            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal(missing ? "executable_not_found" : "executable_not_trusted", result.ErrorCode);
            Assert.False(result.Output.ContainsKey("execution_id"));
        }
        finally
        {
            await service.RemoveSessionAsync(sessionId);
            Directory.Delete(root, recursive: true);
        }
    }
}
