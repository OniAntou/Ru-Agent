using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class CodingWorkStateReceiptTests
{
    [Theory]
    [InlineData("powershell", "{\"script\":\"dotnet test\"}", "dotnet test")]
    [InlineData("run_process", "{\"executable\":\"dotnet\",\"arguments\":[\"test\"]}", "dotnet [\"test\"]")]
    public void Explicit_terminal_receipts_retain_command_and_exit_provenance(string tool, string arguments, string command)
    {
        var run = new RunId("run_" + new string('a', 32));
        var source = new EventId("evt_" + new string('b', 32));
        var state = new SessionWorkState(new SessionId(new string('c', 32)), new WorkspaceId("ws_" + new string('d', 32)),
            new string('e', 64), run, 1, source.Value, new SessionWorkFact("Build the project", run.Value, source.Value),
            [], [], false, false, [], [], [], null, null);
        using var args = JsonDocument.Parse(arguments);
        using var output = JsonDocument.Parse("{\"exit_code\":0}");
        var call = new ModelToolCall("verify", tool, "1.0.0", args.RootElement);
        var updated = SessionWorkStateReceiptReducer.Apply(state, call, output.RootElement, true, run, source, DateTimeOffset.UtcNow);
        var verification = Assert.Single(updated!.Verifications);
        Assert.Equal(command, verification.Command);
        Assert.True(verification.Succeeded);
        Assert.Equal(source.Value, verification.SourceEventId);
    }
}
