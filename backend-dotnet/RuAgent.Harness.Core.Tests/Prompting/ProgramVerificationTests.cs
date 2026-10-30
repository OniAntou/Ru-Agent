using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol.Programs;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class ProgramVerificationTests
{
    [Fact]
    public void FilteredProgramResultCannotHideFailedVerification()
    {
        var entries = ProgramBatch(1, Receipt("powershell", """{"script":"dotnet test","purpose":"verification"}""", "failed", "process_exit_nonzero", """{"exit_code":1}"""));
        Assert.Equal("process_exit_nonzero", VerificationCompletionTests.Snapshot(entries).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void NestedMutationDoesNotObligateAPriorRootCheckAndNestedEditIsASourceEdit()
    {
        const string args = """{"script":"dotnet test","purpose":"verification"}""";
        var root = VerificationCompletionTests.Batch(1, "root", "powershell", args, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");
        var edit = Receipt("edit_file", """{"path":"x","edits":[{"old_string":"a","new_string":"b"}]}""", "succeeded", null, "{}");
        var edited = VerificationCompletionTests.Snapshot(root.Concat(ProgramBatch(3, edit))).VerificationEvidence;
        Assert.Null(edited.BlockingCode);
        Assert.True(edited.HasSourceEdits);
        Assert.True(edited.SourceEditAwaitingVerification);
        var rerun = root.Concat(ProgramBatch(3, edit, Receipt("powershell", args, "succeeded", null, """{"exit_code":0}""")));
        Assert.Null(VerificationCompletionTests.Snapshot(rerun).VerificationEvidence.BlockingCode);
        Assert.False(VerificationCompletionTests.Snapshot(rerun).VerificationEvidence.SourceEditAwaitingVerification);
    }

    [Fact]
    public void MissingTraceOrUnknownEffectsBlocksCompletion()
    {
        var missing = VerificationCompletionTests.Batch(1, "program", "run_code", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}");
        Assert.Equal("program_evidence_unavailable", VerificationCompletionTests.Snapshot(missing).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void ReadOnlyProgramDoesNotStaleCheck()
    {
        var root = VerificationCompletionTests.Batch(1, "root", "powershell", """{"script":"test","purpose":"verification"}""",
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");
        var entries = root.Concat(ProgramBatch(3, Receipt("read_file", """{"path":"x"}""", "succeeded", null, "{}")));
        Assert.Null(VerificationCompletionTests.Snapshot(entries).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void ParallelReadTraceStaysFlatAndContainsEveryNestedReceiptInInputOrder()
    {
        var first = Receipt("read_file", """{"path":"a"}""", "succeeded", null, "{}", adapterInvoked: true);
        var second = Receipt("grep", """{"query":"b"}""", "succeeded", null, "{}", adapterInvoked: true);
        var entries = ProgramBatch(1, first, second);
        var content = entries.Single(entry => entry.Kind == ModelContextEntryKind.ToolResult).Content;
        Assert.True(ProgramReceiptTrace.TryRead(content, "provider-call", out var trace));
        Assert.Equal(new[] { "read_file", "grep" }, trace.Select(item => item.Call.ToolId));
        Assert.Equal("a", trace[0].Call.Arguments.GetProperty("path").GetString());
        Assert.Equal("b", trace[1].Call.Arguments.GetProperty("query").GetString());
        Assert.Equal(2, trace.Count);
    }

    [Fact]
    public void SparseNestedReceiptIndexesCannotBeReinterpretedAsAContiguousTrace()
    {
        var first = Receipt("read_file", """{"path":"a"}""", "succeeded", null, "{}");
        first["call_index"] = 0;
        var third = Receipt("read_file", """{"path":"c"}""", "succeeded", null, "{}");
        third["call_index"] = 2;
        var output = new JsonObject
        {
            ["program_revision"] = ProgramToolContract.Revision,
            ["tool_count"] = 2,
            ["effects_unknown"] = false,
            ["nested_receipts"] = new JsonArray(first, third),
        };

        Assert.False(ProgramReceiptTrace.TryRead(output.ToJsonString(), "provider-call", out _));
    }

    private static JsonObject Receipt(string tool, string args, string status, string? code, string output,
        bool adapterInvoked = true) => new()
    {
        ["tool_id"] = tool, ["arguments"] = JsonNode.Parse(args),
        ["response"] = new JsonObject { ["status"] = status, ["error_code"] = code, ["output"] = JsonNode.Parse(output), ["adapter_invoked"] = adapterInvoked },
    };
    private static ModelContextEntry[] ProgramBatch(long sequence, params JsonObject[] receipts)
    {
        var output = new JsonObject { ["program_revision"] = ProgramToolContract.Revision, ["tool_count"] = receipts.Length,
            ["effects_unknown"] = false, ["result"] = "looks fine", ["nested_receipts"] = new JsonArray(receipts
                .Select((receipt, index) =>
                {
                    var indexed = (JsonObject)receipt.DeepClone();
                    indexed["call_index"] = index;
                    return indexed;
                }).ToArray()) };
        return VerificationCompletionTests.Batch(sequence, "program", "run_code", "{}", ToolExecutionOutcome.Succeeded, "ok", output.ToJsonString());
    }
}
