using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
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
    public void NestedMutationStalesPriorRootCheckAndNestedRerunResolvesIt()
    {
        const string args = """{"script":"dotnet test","purpose":"verification"}""";
        var root = VerificationCompletionTests.Batch(1, "root", "powershell", args, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");
        var edit = Receipt("edit_file", """{"path":"x","edits":[{"old_string":"a","new_string":"b"}]}""", "succeeded", null, "{}");
        var stale = root.Concat(ProgramBatch(3, edit));
        Assert.Equal("verification_stale", VerificationCompletionTests.Snapshot(stale).VerificationEvidence.BlockingCode);
        var fixedEntries = root.Concat(ProgramBatch(3, edit, Receipt("powershell", args, "succeeded", null, """{"exit_code":0}""")));
        Assert.Null(VerificationCompletionTests.Snapshot(fixedEntries).VerificationEvidence.BlockingCode);
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

    private static JsonObject Receipt(string tool, string args, string status, string? code, string output) => new()
    {
        ["tool_id"] = tool, ["arguments"] = JsonNode.Parse(args),
        ["response"] = new JsonObject { ["status"] = status, ["error_code"] = code, ["output"] = JsonNode.Parse(output), ["adapter_invoked"] = true },
    };
    private static ModelContextEntry[] ProgramBatch(long sequence, params JsonObject[] receipts)
    {
        var output = new JsonObject { ["program_revision"] = "coding_code_v1.jint-4.16.3.1", ["tool_count"] = receipts.Length,
            ["effects_unknown"] = false, ["result"] = "looks fine", ["nested_receipts"] = new JsonArray(receipts.Select(r => r.DeepClone()).ToArray()) };
        return VerificationCompletionTests.Batch(sequence, "program", "run_code", "{}", ToolExecutionOutcome.Succeeded, "ok", output.ToJsonString());
    }
}
