using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PendingApprovalStoreTests
{
    [Fact]
    public void Approval_is_bound_to_session_and_consumed_once()
    {
        var store = new PendingApprovalStore();
        var request = Request("request-1", "session-1");
        var manifest = new ToolManifest(
            "terminal.shell",
            "1.0.0",
            "terminal",
            RiskClass.Destructive,
            new HashSet<string>([ToolScope.TerminalExecute], StringComparer.Ordinal),
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            30_000,
            256 * 1024,
            "builtin");
        var arguments = new JsonObject
        {
            ["shell"] = "powershell",
            ["script"] = "Get-Date",
        };

        var approvalId = store.Issue(
            request,
            manifest,
            arguments,
            "Run a terminal action in the workspace.",
            DateTimeOffset.UtcNow.AddMinutes(1),
            "private-terminal-token");

        Assert.False(string.IsNullOrWhiteSpace(approvalId));
        Assert.False(store.TryConsume(
            approvalId,
            "other-session",
            approved: true,
            DateTimeOffset.UtcNow,
            out _));
        Assert.True(store.TryConsume(
            approvalId,
            "session-1",
            approved: true,
            DateTimeOffset.UtcNow,
            out var pending));
        Assert.Equal("Get-Date", pending!.Arguments["script"]!.GetValue<string>());
        Assert.Equal("private-terminal-token", pending.TerminalApprovalToken);
        Assert.False(store.TryConsume(
            approvalId,
            "session-1",
            approved: true,
            DateTimeOffset.UtcNow,
            out _));
    }

    [Fact]
    public void Expired_or_rejected_approval_is_not_available_for_dispatch()
    {
        var store = new PendingApprovalStore();
        var request = Request("request-2", "session-2");
        var manifest = new ToolManifest(
            "files.write",
            "1.0.0",
            "builtin",
            RiskClass.Reversible,
            new HashSet<string>([ToolScope.WorkspaceWrite], StringComparer.Ordinal),
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            30_000,
            256 * 1024,
            "builtin");

        var rejectedId = store.Issue(
            request,
            manifest,
            new JsonObject { ["path"] = "notes/rejected.md" },
            "Write a file in the workspace.",
            DateTimeOffset.UtcNow.AddMinutes(1),
            null);
        Assert.True(store.TryConsume(
            rejectedId,
            "session-2",
            approved: false,
            DateTimeOffset.UtcNow,
            out var rejected));
        Assert.False(rejected!.Approved);

        var expiredId = store.Issue(
            request with { RequestId = "request-3" },
            manifest,
            new JsonObject { ["path"] = "notes/expired.md" },
            "Write a file in the workspace.",
            DateTimeOffset.UtcNow.AddSeconds(1),
            null);
        Assert.False(store.TryConsume(
            expiredId,
            "session-2",
            approved: true,
            DateTimeOffset.UtcNow.AddSeconds(2),
            out _));
    }

    [Fact]
    public void V2_approval_is_bound_to_12_fields_and_consumed_once()
    {
        var store = new PendingApprovalStore();
        var binding = V2Binding();
        var approvalId = store.IssueV2(binding, DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.False(string.IsNullOrWhiteSpace(approvalId));

        var first = store.TryConsumeV2(approvalId, ApprovalCommandV2.AllowOnce, V2Binding(), DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.ConsumedApproved, first.Outcome);

        var replay = store.TryConsumeV2(approvalId, ApprovalCommandV2.AllowOnce, V2Binding(), DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.NotFound, replay.Outcome);
    }

    [Fact]
    public void V2_expired_returns_expired_and_requires_new_request()
    {
        var store = new PendingApprovalStore();
        var binding = new V2ApprovalBinding(
            RunId: "run_11111111111111111111111111111111",
            ToolCallId: "call_11111111111111111111111111111111",
            ToolId: "files.read",
            ToolVersion: "1.0.0",
            CanonicalPath: "a.txt",
            ResolvedExecutable: null,
            Argv: Array.Empty<string>(),
            Cwd: null,
            OperationClass: "read",
            NetworkIntent: "none",
            SnapshotVersion: 5,
            IntentHash: new string('0', 64));
        // Issue with future expiry, then consume with now after expiry
        var id = store.IssueV2(binding, DateTimeOffset.UtcNow.AddSeconds(1));
        var expired = store.TryConsumeV2(id, ApprovalCommandV2.AllowOnce, binding, DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal(V2ApprovalOutcome.Expired, expired.Outcome);

        // New request succeeds
        var fresh = store.IssueV2(V2Binding(), DateTimeOffset.UtcNow.AddMinutes(5));
        var ok = store.TryConsumeV2(fresh, ApprovalCommandV2.AllowOnce, V2Binding(), DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.ConsumedApproved, ok.Outcome);
    }

    [Theory]
    [InlineData("run_id")]
    [InlineData("tool_call_id")]
    [InlineData("tool_id")]
    [InlineData("tool_version")]
    [InlineData("canonical_path")]
    [InlineData("resolved_executable")]
    [InlineData("argv_pattern")]
    [InlineData("cwd")]
    [InlineData("operation_class")]
    [InlineData("network_intent")]
    [InlineData("snapshot_version")]
    [InlineData("intent_hash")]
    public void V2_any_changed_binding_field_returns_stale(string changedField)
    {
        var store = new PendingApprovalStore();
        var baseBinding = V2Binding(argv: new[] { "x" }, exe: @"C:\tools\tool.exe", cwd: @"C:\ws");
        var id = store.IssueV2(baseBinding, DateTimeOffset.UtcNow.AddMinutes(5));

        var mutated = changedField switch
        {
            "run_id" => baseBinding with { RunId = "run_22222222222222222222222222222222" },
            "tool_call_id" => baseBinding with { ToolCallId = "call_22222222222222222222222222222222" },
            "tool_id" => baseBinding with { ToolId = "files.write" },
            "tool_version" => baseBinding with { ToolVersion = "1.0.1" },
            "canonical_path" => baseBinding with { CanonicalPath = @"C:\ws\other.txt" },
            "resolved_executable" => baseBinding with { ResolvedExecutable = @"C:\tools\other.exe" },
            "argv_pattern" => baseBinding with { Argv = new[] { "x", "y" } },
            "cwd" => baseBinding with { Cwd = @"C:\ws\sub" },
            "operation_class" => baseBinding with { OperationClass = "edit" },
            "network_intent" => baseBinding with { NetworkIntent = "declared" },
            "snapshot_version" => baseBinding with { SnapshotVersion = baseBinding.SnapshotVersion + 1 },
            _ => baseBinding with { IntentHash = new string('1', 64) },
        };

        var res = store.TryConsumeV2(id, ApprovalCommandV2.AllowOnce, mutated, DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.Stale, res.Outcome);
    }

    [Fact]
    public void V2_preview_is_exact_without_glob()
    {
        var binding = V2Binding(canonicalPath: @"C:\ws\target.txt", cwd: @"C:\ws", argv: new[] { "--flag", "value" });
        var preview = PendingApprovalStore.BuildMatcherPreview(binding);
        Assert.Equal("files.read", preview["tool_id"]!.GetValue<string>());
        foreach (var key in new[] { "canonical_path", "cwd_pattern" })
        {
            var val = preview[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (val is not null) Assert.DoesNotContain("*", val);
        }
        var argv = preview["argv_pattern"]!.AsArray();
        foreach (var item in argv)
        {
            Assert.DoesNotContain("*", item!.GetValue<string>());
            Assert.DoesNotContain("?", item!.GetValue<string>());
        }
    }

    [Fact]
    public void V2_allow_once_approves()
    {
        var store = new PendingApprovalStore();
        var binding = V2Binding(canonicalPath: @"C:\ws\target.txt", cwd: @"C:\ws", argv: new[] { "a" });
        var id = store.IssueV2(binding, DateTimeOffset.UtcNow.AddMinutes(5));
        var res = store.TryConsumeV2(id, ApprovalCommandV2.AllowOnce, V2Binding(canonicalPath: @"C:\ws\target.txt", cwd: @"C:\ws", argv: new[] { "a" }), DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.ConsumedApproved, res.Outcome);
    }

    [Fact]
    public void V2_allow_session_directory_approves()
    {
        var store = new PendingApprovalStore();
        var binding = V2Binding() with { ApprovalKind = "external_directory", BoundaryDirectory = @"C:\shared" };
        var id = store.IssueV2(binding, DateTimeOffset.UtcNow.AddMinutes(5));
        var res = store.TryConsumeV2(id, ApprovalCommandV2.AllowSessionDirectory, binding, DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.ConsumedApproved, res.Outcome);
    }

    [Theory]
    [InlineData("approval_kind")]
    [InlineData("boundary_directory")]
    public void V2_changed_kind_or_boundary_returns_stale(string changedField)
    {
        var store = new PendingApprovalStore();
        var baseBinding = V2Binding() with { ApprovalKind = "external_directory", BoundaryDirectory = @"C:\shared" };
        var id = store.IssueV2(baseBinding, DateTimeOffset.UtcNow.AddMinutes(5));
        var mutated = changedField switch
        {
            "approval_kind" => baseBinding with { ApprovalKind = "terminal_risk" },
            _ => baseBinding with { BoundaryDirectory = @"C:\other" },
        };
        var res = store.TryConsumeV2(id, ApprovalCommandV2.AllowSessionDirectory, mutated, DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.Stale, res.Outcome);
    }

    [Fact]
    public void V2_deny_consumes_as_denied()
    {
        var store = new PendingApprovalStore();
        var id = store.IssueV2(V2Binding(), DateTimeOffset.UtcNow.AddMinutes(5));
        var res = store.TryConsumeV2(id, ApprovalCommandV2.Deny, V2Binding(), DateTimeOffset.UtcNow);
        Assert.Equal(V2ApprovalOutcome.ConsumedDenied, res.Outcome);
    }

    private static V2ApprovalBinding V2Binding(
        string? canonicalPath = "a.txt",
        string? exe = null,
        IReadOnlyList<string>? argv = null,
        string? cwd = null,
        string operationClass = "read",
        string networkIntent = "none",
        long snapshotVersion = 5,
        string? intentHash = null) => new(
            RunId: "run_11111111111111111111111111111111",
            ToolCallId: "call_11111111111111111111111111111111",
            ToolId: "files.read",
            ToolVersion: "1.0.0",
            CanonicalPath: canonicalPath,
            ResolvedExecutable: exe,
            Argv: argv ?? Array.Empty<string>(),
            Cwd: cwd,
            OperationClass: operationClass,
            NetworkIntent: networkIntent,
            SnapshotVersion: snapshotVersion,
            IntentHash: intentHash ?? new string('0', 64));

    private static AgentEnvelope Request(string requestId, string sessionId) => new(
        AgentEnvelopeCodec.ProtocolVersion,
        requestId,
        AgentMessageTypes.ToolRequest,
        sessionId,
        "corr-1",
        30_000,
        new JsonObject());
}
