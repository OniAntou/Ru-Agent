extern alias Broker;

using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;
using RuBackend.Harness.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class StructuredToolOutputProjectionBrokerTests
{
    [Fact]
    public async Task Terminal_marker_inside_projected_broker_page_is_recovered_without_rerunning_command()
    {
        using (var fixture = BrokerFixture.Create())
        {
            var service = fixture.Service;
            var sessionId = fixture.SessionId;
            var executionId = "req-spool-" + Guid.NewGuid().ToString("N");
            var execution = await service.ExecuteAuthenticatedV2Async(
                TerminalRequest(sessionId, executionId, new JsonObject
                {
                    ["mode"] = "run",
                    ["executable"] = "cmd.exe",
                    ["arguments"] = new JsonArray("/c", "(echo once>>ran.txt) & (for /L %i in (1,1,400) do @echo 012345678901234567890123456789) & @echo CURSOR-MIDDLE-MARKER & (for /L %i in (1,1,1000) do @echo 012345678901234567890123456789)"),
                }),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Succeeded, execution.Status);
            Assert.True(execution.Output["stdout_dropped_bytes"]!.GetValue<long>() > 0, execution.Output.ToJsonString());
            Assert.Equal(executionId, execution.Output["spool_id"]!.GetValue<string>());

            const int brokerPageLimit = 14_000;
            var firstRequestId = "req-page-" + Guid.NewGuid().ToString("N");
            var firstPage = await ReadOutput(service, sessionId, firstRequestId, executionId, 0, brokerPageLimit);
            var source = firstPage.Output.ToJsonString();
            Assert.Contains("CURSOR-MIDDLE-MARKER", firstPage.Output["page"]!.GetValue<string>(), StringComparison.Ordinal);
            var conversation = ProjectReadOutput(executionId, firstRequestId, source);
            using var projected = JsonDocument.Parse(conversation.Content);
            var cursor = projected.RootElement.GetProperty("projection_next_offset").GetInt32();
            Assert.True(cursor > 0);
            Assert.True(cursor < brokerPageLimit);
            Assert.DoesNotContain("CURSOR-MIDDLE-MARKER", conversation.Content, StringComparison.Ordinal);
            Assert.Equal(source, firstPage.Output.ToJsonString());

            var recovered = await ReadOutput(
                service,
                sessionId,
                "req-page-" + Guid.NewGuid().ToString("N"),
                executionId,
                cursor,
                brokerPageLimit);

            Assert.Equal(ToolResultStatuses.Succeeded, recovered.Status);
            Assert.Equal(executionId, recovered.Output["spool_id"]!.GetValue<string>());
            Assert.Equal(cursor, recovered.Output["offset"]!.GetValue<int>());
            Assert.Contains("CURSOR-MIDDLE-MARKER", recovered.Output["page"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("once" + Environment.NewLine, File.ReadAllText(Path.Combine(fixture.Workspace, "ran.txt")));
        }
    }

    [Fact]
    public async Task Read_cursor_recovers_omitted_utf8_marker_from_the_same_broker_page()
    {
        using var fixture = BrokerFixture.Create();
        const string hiddenMarker = "READ-UTF8-PROJECTION-MARKER";
        var content = "\uFEFF" + string.Concat(Enumerable.Repeat("Tiếng🙂", 1_300)) + hiddenMarker + " tail";
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "large.txt"), content, new System.Text.UTF8Encoding(false));
        var originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);
        const string providerCallId = "provider_read_cursor";
        var first = await Execute(fixture, "read", "req-read-first", new JsonObject
        {
            ["path"] = "large.txt",
            ["offset"] = 0,
            ["limit"] = 262_144,
        });
        Assert.Equal(ToolResultStatuses.Succeeded, first.Status);
        var source = first.Output.ToJsonString();
        var projected = ProjectResult("read", providerCallId, "{\"path\":\"large.txt\",\"offset\":0,\"limit\":262144}", source);
        using var view = JsonDocument.Parse(projected.Content);
        var visible = view.RootElement.GetProperty("content").GetString()!;
        var cursor = view.RootElement.GetProperty("projection_next_offset").GetInt32();
        Assert.DoesNotContain(hiddenMarker, visible, StringComparison.Ordinal);
        Assert.True(cursor < System.Text.Encoding.UTF8.GetByteCount("\uFEFF" + string.Concat(Enumerable.Repeat("Tiếng🙂", 1_300))));
        Assert.Equal(originalBytes, first.Output["bytes_read"]!.GetValue<int>());
        Assert.Equal(source, first.Output.ToJsonString());

        var next = await Execute(fixture, "read", "req-read-next", new JsonObject
        {
            ["path"] = "large.txt",
            ["offset"] = cursor,
            ["limit"] = 262_144,
        });

        Assert.Equal(ToolResultStatuses.Succeeded, next.Status);
        Assert.Contains(hiddenMarker, next.Output["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grep_cursor_uses_original_nonzero_offset_and_recovers_omitted_match_without_duplicates()
    {
        using var fixture = BrokerFixture.Create();
        const string hiddenMarker = "NEEDLE-GREP-PROJECTION-MARKER";
        var lines = Enumerable.Range(0, 120)
            .Select(index => $"needle match {index:D3} {new string('x', 200)}")
            .ToArray();
        lines[45] += " " + hiddenMarker;
        await File.WriteAllLinesAsync(Path.Combine(fixture.Workspace, "matches.txt"), lines, new System.Text.UTF8Encoding(false));
        const int originalOffset = 7;
        const string providerCallId = "provider_grep_cursor";
        var arguments = new JsonObject
        {
            ["query"] = "needle",
            ["path"] = "matches.txt",
            ["offset"] = originalOffset,
            ["max_results"] = 64,
        };
        var first = await Execute(fixture, "grep", "req-grep-first", arguments.DeepClone().AsObject());
        Assert.Equal(ToolResultStatuses.Succeeded, first.Status);
        Assert.Equal(64, first.Output["count"]!.GetValue<int>());
        var source = first.Output.ToJsonString();
        var projected = ProjectResult("grep", providerCallId, arguments.ToJsonString(), source);
        using var view = JsonDocument.Parse(projected.Content);
        var visible = view.RootElement.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("preview").GetString()!)
            .ToArray();
        var cursor = view.RootElement.GetProperty("projection_next_offset").GetInt32();
        Assert.DoesNotContain(visible, result => result.Contains(hiddenMarker, StringComparison.Ordinal));
        Assert.Equal(originalOffset + visible.Length, cursor);
        Assert.Equal(64, view.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(source, first.Output.ToJsonString());

        arguments["offset"] = cursor;
        var next = await Execute(fixture, "grep", "req-grep-next", arguments);

        Assert.Equal(ToolResultStatuses.Succeeded, next.Status);
        Assert.Contains(next.Output["results"]!.AsArray(), result =>
            result!["preview"]!.GetValue<string>().Contains(hiddenMarker, StringComparison.Ordinal));
        var firstLines = visible.Select(result => result.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]).ToArray();
        var secondLines = next.Output["results"]!.AsArray()
            .Select(result => result!["preview"]!.GetValue<string>().Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]);
        Assert.Empty(firstLines.Intersect(secondLines, StringComparer.Ordinal));
    }

    private static async Task<ToolResult> ReadOutput(
        Broker::RuAgent.Broker.Tools.ToolExecutionService service,
        string sessionId,
        string requestId,
        string executionId,
        int offset,
        int limit) =>
        await service.ExecuteAuthenticatedV2Async(
            TerminalRequest(sessionId, requestId, new JsonObject
            {
                ["mode"] = "read_output",
                ["execution_id"] = executionId,
                ["offset"] = offset,
                ["limit"] = limit,
            }),
            CancellationToken.None);

    private static Task<ToolResult> Execute(BrokerFixture fixture, string toolId, string requestId, JsonObject arguments) =>
        fixture.Service.ExecuteAuthenticatedV2Async(
            ToolRequest(fixture.SessionId, requestId, toolId, arguments),
            CancellationToken.None);

    private static ModelTurn ProjectResult(string toolId, string providerCallId, string arguments, string source)
    {
        using var parsedArguments = JsonDocument.Parse(arguments);
        var context = new ModelContextSnapshot(
        [
            ModelContextEntry.Assistant("assistant", 1, new AssistantStep(null,
            [new ModelToolCall(providerCallId, toolId, "1.0.0", parsedArguments.RootElement)], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult(
                "result",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_55555555555555555555555555555555"),
                    new ToolCallId("call_55555555555555555555555555555555"),
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: providerCallId),
                source),
        ], Array.Empty<byte>());

        return InProcessModelStepRunner.BuildConversationForTests(context).Messages![^1];
    }

    private static ModelTurn ProjectReadOutput(string executionId, string providerCallId, string source)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            mode = "read_output",
            execution_id = executionId,
            offset = 0,
            limit = 7_000,
        }));
        var context = new ModelContextSnapshot(
        [
            ModelContextEntry.Assistant("assistant", 1, new AssistantStep(null,
            [new ModelToolCall(providerCallId, "bash", "1.0.0", arguments.RootElement)], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult(
                "result",
                2,
                new ToolExecutionReceipt(
                    new ExecutionId("exec_44444444444444444444444444444444"),
                    new ToolCallId("call_44444444444444444444444444444444"),
                    ToolExecutionOutcome.Succeeded,
                    "ok",
                    null,
                    sideEffectsPossible: false,
                    providerCallId: providerCallId),
                source),
        ], Array.Empty<byte>());

        return InProcessModelStepRunner.BuildConversationForTests(context).Messages![^1];
    }

    private static AgentEnvelope TerminalRequest(string sessionId, string requestId, JsonObject arguments) => new(
        AgentEnvelopeCodec.ProtocolVersionV2,
        requestId,
        AgentMessageTypes.ToolExecute,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["tool_id"] = "bash",
            ["tool_version"] = "1.0.0",
            ["capability_class"] = "internal",
            ["run_id"] = "run_" + Guid.NewGuid().ToString("N"),
            ["arguments"] = arguments,
        });

    private static AgentEnvelope ToolRequest(string sessionId, string requestId, string toolId, JsonObject arguments) => new(
        AgentEnvelopeCodec.ProtocolVersionV2,
        requestId,
        AgentMessageTypes.ToolExecute,
        sessionId,
        null,
        30_000,
        new JsonObject
        {
            ["tool_id"] = toolId,
            ["tool_version"] = "1.0.0",
            ["capability_class"] = "internal",
            ["run_id"] = "run_" + Guid.NewGuid().ToString("N"),
            ["arguments"] = arguments,
        });

    private sealed class BrokerFixture : IDisposable
    {
        private readonly string _root;

        private BrokerFixture(string root, string workspace, string sessionId, Broker::RuAgent.Broker.Tools.ToolExecutionService service)
        {
            _root = root;
            Workspace = workspace;
            SessionId = sessionId;
            Service = service;
        }

        internal string Workspace { get; }
        internal string SessionId { get; }
        internal Broker::RuAgent.Broker.Tools.ToolExecutionService Service { get; }

        internal static BrokerFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-projection-broker-" + Guid.NewGuid().ToString("N"));
            var workspace = Path.Combine(root, "workspace");
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(sessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:18086",
                "broker-token",
                2,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            var session = Broker::RuAgent.Broker.Runtime.BrokerSession.Load(descriptorPath, DateTimeOffset.UtcNow);
            const string sessionId = "session-projection-broker";
            session.Open(sessionId, new HashSet<string>(
            [
                Broker::RuAgent.Broker.Policy.ToolScope.TerminalExecute,
                Broker::RuAgent.Broker.Policy.ToolScope.WorkspaceRead,
                Broker::RuAgent.Broker.Policy.ToolScope.SystemRead,
            ], StringComparer.Ordinal));
            return new BrokerFixture(root, workspace, sessionId,
                new Broker::RuAgent.Broker.Tools.ToolExecutionService(session, workspace));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
