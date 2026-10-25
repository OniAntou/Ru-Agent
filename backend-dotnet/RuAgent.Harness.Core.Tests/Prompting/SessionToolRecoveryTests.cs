using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class SessionToolRecoveryTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"purpose\":\"inspection\"")]
    public void Alternative_web_source_can_follow_a_failed_exploratory_terminal(string purpose)
    {
        var entries = VerificationCompletionTests.Batch(1, "curl", "powershell",
            "{\"script\":\"curl https://example.com | head\"" + purpose + "}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(VerificationCompletionTests.Batch(3, "source", "web_fetch", "{}",
                ToolExecutionOutcome.Succeeded, "ok", """{"content":"source evidence"}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(VerificationCompletionTests.Snapshot(entries), out _));
    }

    [Fact]
    public void Changing_file_names_does_not_reset_the_same_invalid_operation_repair()
    {
        var entries = Enumerable.Range(0, 3).SelectMany(index => VerificationCompletionTests.Batch(
            2 * index + 1, "bad" + index, "write_file", Args(index), ToolExecutionOutcome.Failed,
            "malformed_call", """{"error":"tool_denied","code":"malformed_call"}"""));
        using var json = JsonDocument.Parse(Args(4));
        var call = new ModelToolCall("next", "write_file", "1.0.0", json.RootElement);
        Assert.Equal(RepetitionGuardVerdict.Advise, ConsecutiveToolCallGuard.Inspect(
            VerificationCompletionTests.Snapshot(entries), [call], out var code));
        Assert.Equal("malformed_call", code);
        using var fixedJson = JsonDocument.Parse("""{"path":"real.py","content":"print('ok')"}""");
        Assert.Equal(RepetitionGuardVerdict.NotRepeated, ConsecutiveToolCallGuard.Inspect(
            VerificationCompletionTests.Snapshot(entries),
            [new ModelToolCall("fixed", "write_file", "1.0.0", fixedJson.RootElement)], out _));
    }

    private static string Args(int index) => JsonSerializer.Serialize(new
    { overwrite = "yes", path = "test" + index + ".py", content = "print('hi')" });
}
