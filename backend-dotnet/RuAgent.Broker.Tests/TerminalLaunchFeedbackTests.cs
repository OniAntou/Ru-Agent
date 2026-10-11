using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class TerminalLaunchFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_without_argument_vector_executes_like_an_empty_vector(bool includeArguments)
    {
        var arguments = new JsonObject
        {
            ["mode"] = "run", ["executable"] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["timeout_ms"] = 10_000,
        };
        if (includeArguments) arguments["arguments"] = new JsonArray();
        var result = await ExecuteAsync(arguments);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal(0, result.Output["exit_code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("run", "executable")]
    [InlineData("shell", "script")]
    public async Task Missing_mode_specific_field_preserves_invalid_arguments(string mode, string missingField)
    {
        var arguments = new JsonObject { ["mode"] = mode, ["arguments"] = new JsonArray() };
        Assert.False(arguments.ContainsKey(missingField));
        var result = await ExecuteAsync(arguments);
        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.False(result.Output.ContainsKey("execution_id"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Invalid_executable_reports_resolution_error_before_registering_task(bool background, bool missing)
    {
        var result = await ExecuteAsync(new JsonObject
        {
            ["mode"] = "run",
            ["executable"] = missing ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe") : "cmd",
            ["background"] = background, ["arguments"] = new JsonArray(),
        });
        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal(missing ? "executable_not_found" : "executable_not_trusted", result.ErrorCode);
        Assert.False(result.Output.ContainsKey("execution_id"));
    }

    private static async Task<ToolResult> ExecuteAsync(JsonObject arguments)
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
            return await service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "missing-launch", AgentMessageTypes.ToolExecute,
                sessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "bash", ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal", ["run_id"] = runId,
                    ["arguments"] = arguments,
                }), default);
        }
        finally
        {
            await service.RemoveSessionAsync(sessionId);
            Directory.Delete(root, recursive: true);
        }
    }
}
