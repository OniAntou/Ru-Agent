using System.Text.Json;
using RuAgent.Cli.Rpc;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SubagentFrameBudgetTests
{
    [Fact]
    public void Maximum_family_metadata_fits_the_existing_RPC_and_SDK_frame_budget()
    {
        var id = "run_" + new string('a', 32);
        var usage = new SubagentUsageSummary(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, false);
        var approval = new SubagentApprovalSummary("apr_" + new string('a', 32), "write",
            JsonSerializer.SerializeToElement(new { op = "write", path = "a.txt", content = new string('\uffff', 1300) }));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(approval.Arguments).Length < SubagentLimits.MaxApprovalPreviewBytes);
        var attempts = Enumerable.Range(1, 3).Select(n => new SubagentAttemptSummary(id, n,
            n == 3 ? "waiting_approval" : "completed", long.MaxValue, new string('a', 128), new string('\uffff', 256),
            "chat_completions", DateTimeOffset.MaxValue, n == 3 ? approval : null, usage)).ToArray();
        var results = Enumerable.Range(1, 3).Select(n => new SubagentResultSummary(n, id, "completed", new string('a', 64),
            "unknown", 50000, true, new("awaiting_approval", long.MaxValue, "apr_" + new string('a', 32), new string('a', 64)))).ToArray();
        var agents = Enumerable.Range(0, 8).Select(n => new SubagentSummary(new string('a', 127) + n,
            "coding", new string('\uffff', SubagentLimits.MaxTaskCharacters), false, attempts, results)).ToArray();
        var snapshot = new SubagentListResponse(id, "waiting_subagents", new string('a', 64), agents, [], 24, usage);
        // Populate every optional field to conservatively bound a full legal family.
        var frame = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = 1, result = snapshot });
        Assert.InRange(frame.Length, 1, RpcProtocol.MaxFrameBytes);
    }
}
