using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class AgentEnvelopeTests
{
    [Fact]
    public void Serialize_round_trips_identity_and_payload()
    {
        var envelope = new AgentEnvelope(
            1,
            "tool-1",
            AgentMessageTypes.ToolRequest,
            "session-1",
            "corr-1",
            10_000,
            new JsonObject { ["tool_id"] = "files.list" });

        var json = AgentEnvelopeCodec.Serialize(envelope);
        var parsed = AgentEnvelopeCodec.Parse(json);

        Assert.Equal(envelope.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(envelope.RequestId, parsed.RequestId);
        Assert.Equal(envelope.MessageType, parsed.MessageType);
        Assert.Equal(envelope.SessionId, parsed.SessionId);
        Assert.Equal(envelope.CorrelationId, parsed.CorrelationId);
        Assert.Equal(envelope.DeadlineMs, parsed.DeadlineMs);
        Assert.Equal(envelope.Payload.ToJsonString(), parsed.Payload.ToJsonString());
        Assert.Contains("\"protocol_version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"request_id\":\"tool-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_omits_null_correlation_id()
    {
        var envelope = new AgentEnvelope(
            1,
            "health-1",
            AgentMessageTypes.Health,
            "session-1",
            null,
            1_000,
            new JsonObject());

        var json = AgentEnvelopeCodec.Serialize(envelope);

        Assert.DoesNotContain("correlation_id", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_deep_clones_payload()
    {
        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(
            new AgentEnvelope(
                1,
                "request-1",
                AgentMessageTypes.ToolResult,
                "session-1",
                null,
                1_000,
                new JsonObject { ["status"] = "succeeded" })));

        parsed.Payload["status"] = "changed";

        Assert.Equal("changed", parsed.Payload["status"]!.GetValue<string>());
    }

    [Fact]
    public void Session_opened_round_trips_as_a_known_private_message()
    {
        var envelope = new AgentEnvelope(
            1,
            "session-opened-1",
            AgentMessageTypes.SessionOpened,
            "session-1",
            "corr-1",
            1_000,
            new JsonObject
            {
                ["status"] = "succeeded",
                ["scopes"] = new JsonArray("system.write"),
                ["skill_ids"] = new JsonArray(),
            });

        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(envelope));

        Assert.Equal(AgentMessageTypes.SessionOpened, parsed.MessageType);
        Assert.Equal(new[] { "system.write" }, parsed.Payload["scopes"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
    }

    [Theory]
    [InlineData("unsupported_version")]
    [InlineData("invalid_id")]
    [InlineData("unknown_type")]
    [InlineData("invalid_payload")]
    public void Parse_rejects_invalid_envelopes(string expectedCode)
    {
        var json = InvalidEnvelopeFor(expectedCode);

        var error = Assert.Throws<AgentProtocolException>(() => AgentEnvelopeCodec.Parse(json));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void V2_envelope_round_trips_with_complete_snapshot()
    {
        var snapshot = EnvelopeTestSnapshots.StandardEmpty(version: 7);
        var envelope = new AgentEnvelope(
            2,
            "run-1",
            AgentMessageTypes.RunOpen,
            "session-1",
            "corr-1",
            10_000,
            EnvelopeTestSnapshots.RunOpenPayload(
                "run_11111111111111111111111111111111",
                "session-1",
                @"D:\workspace",
                snapshot));

        var json = AgentEnvelopeCodec.Serialize(envelope);
        var parsed = AgentEnvelopeCodec.Parse(json);
        Assert.Equal(2, parsed.ProtocolVersion);
        Assert.Equal(AgentMessageTypes.RunOpen, parsed.MessageType);
        Assert.NotNull(parsed.Payload["snapshot"]);
        Assert.Equal(7, parsed.Payload["snapshot"]!["version"]!.GetValue<long>());
    }

    [Fact]
    public void V2_run_open_without_snapshot_is_rejected_by_codec()
    {
        var envelope = new AgentEnvelope(
            2,
            "run-1",
            AgentMessageTypes.RunOpen,
            "session-1",
            null,
            10_000,
            new JsonObject
            {
                ["run_id"] = "run_11111111111111111111111111111111",
                ["session_id"] = "session-1",
                ["workspace_id"] = "ws_11111111111111111111111111111111",
                ["canonical_root"] = @"D:\workspace",
                ["volume_serial"] = "VOL-1234",
                ["root_file_id"] = "fid-1234",
                ["display_hash"] = new string('a', 64),
                ["preset"] = "standard",
                ["snapshot_version"] = 1,
                ["snapshot_hash"] = new string('b', 64),
                ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            });

        var error = Assert.Throws<AgentProtocolException>(() => AgentEnvelopeCodec.Parse(
            AgentEnvelopeCodec.Serialize(envelope)));
        Assert.Equal("invalid_run_open", error.Code);
    }

    [Fact]
    public void V2_snapshot_with_ordered_rules_round_trips_matchers()
    {
        var snapshot = EnvelopeTestSnapshots.StandardWithRules(
            new BrokerPolicyRuleWire(
                "rule_allow_reads",
                "project",
                "allow",
                new BrokerPolicyMatcherWire(
                    ToolId: "files.read",
                    ToolVersion: null,
                    CanonicalPathPattern: "src/**",
                    ResolvedExecutable: null,
                    ArgvPattern: null,
                    CwdPattern: null,
                    OperationClass: "read",
                    NetworkIntent: null),
                CreationOrder: 1,
                ExpiresAt: null),
            new BrokerPolicyRuleWire(
                "rule_deny_env",
                "global",
                "deny",
                new BrokerPolicyMatcherWire(
                    ToolId: null,
                    ToolVersion: null,
                    CanonicalPathPattern: ".env*",
                    ResolvedExecutable: null,
                    ArgvPattern: null,
                    CwdPattern: null,
                    OperationClass: null,
                    NetworkIntent: null),
                CreationOrder: 2,
                ExpiresAt: null));

        var payload = EnvelopeTestSnapshots.RunOpenPayload(
            "run_33333333333333333333333333333333",
            "session-1",
            @"D:\workspace",
            snapshot);
        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(
            new AgentEnvelope(2, "run-2", AgentMessageTypes.RunOpen, "session-1", null, 10_000, payload)));

        var rules = parsed.Payload["snapshot"]!["ordered_rules"]!.AsArray();
        Assert.Equal(2, rules.Count);
        Assert.Equal("rule_allow_reads", rules[0]!["rule_id"]!.GetValue<string>());
        Assert.Equal("src/**", rules[0]!["matcher"]!["canonical_path_pattern"]!.GetValue<string>());
        Assert.Equal("deny", rules[1]!["decision"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("run.open", true)]
    [InlineData("run.policy.rebind", true)]
    [InlineData("run.close", false)]
    [InlineData("tool.evaluate", false)]
    [InlineData("tool.decision", false)]
    [InlineData("tool.prepare", false)]
    [InlineData("tool.prepared", false)]
    [InlineData("tool.execute", false)]
    [InlineData("tool.result", false)]
    [InlineData("approval.respond", false)]
    [InlineData("approval.result", false)]
    [InlineData("tool.output.delta", false)]
    public void All_v2_message_types_round_trip_and_are_v2_only(string messageType, bool leaseShaped)
    {
        var envelope = new AgentEnvelope(
            2,
            "v2-msg-1",
            messageType,
            "session-1",
            null,
            1_000,
            leaseShaped
                ? EnvelopeTestSnapshots.RunOpenPayload(
                    "run_55555555555555555555555555555555",
                    "session-1",
                    @"D:\workspace",
                    EnvelopeTestSnapshots.StandardEmpty())
                : messageType == AgentMessageTypes.ToolOutputDelta
                    ? new JsonObject
                    {
                        ["run_id"] = "run_66666666666666666666666666666666",
                        ["tool_call_id"] = "call_66666666666666666666666666666666",
                        ["execution_id"] = "exec_66666666666666666666666666666666",
                        ["frame_sequence"] = 1,
                        ["stream"] = "stdout",
                        ["text"] = "partial",
                    }
                    : new JsonObject());

        Assert.Contains(messageType, AgentMessageTypes.V2Only);
        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(envelope));
        Assert.Equal(messageType, parsed.MessageType);
        Assert.Equal(2, parsed.ProtocolVersion);
    }

    [Fact]
    public void V1_envelope_is_codec_allowed_for_every_v2_only_type_but_flagged_for_broker_rejection()
    {
        foreach (var messageType in AgentMessageTypes.V2Only)
        {
            var envelope = new AgentEnvelope(
                1,
                "v1-reject-1",
                messageType,
                "session-1",
                null,
                1_000,
                new JsonObject());

            var ex = Record.Exception(() => AgentEnvelopeCodec.Validate(envelope));
            Assert.Null(ex);
            Assert.Contains(messageType, AgentMessageTypes.V2Only);
        }
    }

    private static string InvalidEnvelopeFor(string code)
    {
        var envelope = new JsonObject
        {
            ["protocol_version"] = 1,
            ["message_type"] = AgentMessageTypes.Health,
            ["request_id"] = "request-1",
            ["session_id"] = "session-1",
            ["deadline_ms"] = 1_000,
            ["payload"] = new JsonObject(),
        };

        switch (code)
        {
            case "unsupported_version":
                envelope["protocol_version"] = 99;
                break;
            case "invalid_id":
                envelope["request_id"] = " ";
                break;
            case "unknown_type":
                envelope["message_type"] = "unknown";
                break;
            case "invalid_payload":
                envelope["payload"] = "not-an-object";
                break;
        }

        return envelope.ToJsonString();
    }
}

internal static class EnvelopeTestSnapshots
{
    public const string WorkspaceId = "ws_11111111111111111111111111111111";
    public const string VolumeSerial = "VOL-1234";
    public const string RootFileId = "fid-1234";
    public const string DisplayHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string CreatedAt = "2026-08-24T12:00:00.0000000+00:00";

    public static BrokerPolicySnapshotWire StandardEmpty(long version = 1) =>
        Snapshot(version, "standard", []);

    public static BrokerPolicySnapshotWire StandardWithRules(params BrokerPolicyRuleWire[] rules) =>
        Snapshot(1, "standard", rules);

    public static BrokerPolicySnapshotWire FullAccessEmpty(long version = 1) =>
        Snapshot(version, "full_access", []);

    public static BrokerPolicySnapshotWire Snapshot(long version, string preset, BrokerPolicyRuleWire[] rules) =>
        new(
            version,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(WorkspaceId, VolumeSerial, RootFileId, DisplayHash),
            rules,
            CreatedAt);

    public static string HashOf(BrokerPolicySnapshotWire snapshot) =>
        RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot);

    public static JsonObject RunOpenPayload(string runId, string sessionId, string canonicalRoot, BrokerPolicySnapshotWire snapshot) =>
        new()
        {
            ["run_id"] = runId,
            ["session_id"] = sessionId,
            ["workspace_id"] = snapshot.Workspace.WorkspaceId,
            ["canonical_root"] = canonicalRoot,
            ["volume_serial"] = snapshot.Workspace.VolumeSerialNumber,
            ["root_file_id"] = snapshot.Workspace.RootFileId,
            ["display_hash"] = snapshot.Workspace.DisplayIdentityHash,
            ["preset"] = snapshot.Preset,
            ["snapshot_version"] = snapshot.Version,
            ["snapshot_hash"] = HashOf(snapshot),
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            ["snapshot"] = RuAgent.Protocol.Harness.BrokerPolicyContracts.WriteSnapshot(snapshot),
        };
}
