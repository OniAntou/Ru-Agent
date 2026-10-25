using System.Text.Json.Nodes;
using RuAgent.Broker.Programs;
using RuAgent.Protocol.Programs;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProgramRunnerTests
{
    [Fact]
    public async Task CompletedReplayDoesNotRepeatEffectsAndCodeCannotChange()
    {
        var journal = new MemoryJournal();
        var runner = new ProgramRunner(journal);
        var invoked = 0;
        Task<JsonObject> Invoke(ProgramToolRequest _, string __, CancellationToken ___)
        {
            invoked++;
            return Task.FromResult(new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["value"] = 42 } });
        }
        const string code = "return tools.write_file({path:'x',content:'a'}).output.value;";
        var result = await runner.RunAsync("run", "call", "binding", code, Invoke, default);
        Assert.Equal(42, result["result"]!.GetValue<int>());
        Assert.Equal(1, result["tool_count"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(result, await runner.RunAsync("run", "call", "binding", code, Invoke, default)));
        Assert.Equal(1, invoked);
        var mismatch = await runner.RunAsync("run", "call", "binding", "return 0;", Invoke, default);
        Assert.Equal("program_replay_mismatch", mismatch["error"]!.GetValue<string>());
        Assert.True(mismatch["effects_unknown"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CrashAfterDispatchNeverRepeatsUnknownEffect()
    {
        var journal = new MemoryJournal { FailReceipt = true };
        var runner = new ProgramRunner(journal);
        var invoked = 0;
        Task<JsonObject> Invoke(ProgramToolRequest _, string __, CancellationToken ___)
        {
            invoked++;
            return Task.FromResult(new JsonObject());
        }
        const string code = "tools.file_delete({path:'x'}); return 1;";
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("run", "call", "binding", code, Invoke, default));
        journal.FailReceipt = false;
        var replay = await runner.RunAsync("run", "call", "binding", code, Invoke, default);
        Assert.Equal("program_execution_unknown", replay["error"]!.GetValue<string>());
        Assert.Equal(1, invoked);
    }

    [Fact]
    public async Task FullNestedFailureEvidenceSurvivesSelectedResult()
    {
        var result = await new ProgramRunner(new MemoryJournal()).RunAsync("run", "call", "binding",
            "tools.powershell({script:'exit 1',purpose:'verification'}); return 'looks fine';",
            (_, _, _) => Task.FromResult(new JsonObject { ["status"] = "failed", ["error_code"] = "process_exit_nonzero",
                ["output"] = new JsonObject { ["exit_code"] = 1 } }), default);
        Assert.Equal("looks fine", result["result"]!.GetValue<string>());
        Assert.Equal("failed", result["nested_receipts"]![0]!["response"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task BudgetExhaustionPreventsDispatch()
    {
        var result = await new ProgramRunner(new MemoryJournal { AllowBudget = false }).RunAsync("run", "call", "binding",
            "tools.file_delete({path:'x'});", (_, _, _) => throw new Exception("must not dispatch"), default);
        Assert.Equal("program_budget_exhausted", result["error"]!.GetValue<string>());
    }

    internal sealed class MemoryJournal : IProgramJournal
    {
        private readonly Dictionary<string, JsonObject> _values = new();
        public bool FailReceipt { get; set; }
        public bool AllowBudget { get; set; } = true;
        public Task<JsonObject?> ReadAsync(string run, string key, CancellationToken ct) =>
            Task.FromResult(_values.TryGetValue(run + key, out var value) ? (JsonObject?)value.DeepClone() : null);
        public Task<ProgramJournalWrite> CreateAsync(string run, string key, JsonObject value, CancellationToken ct)
        {
            if (FailReceipt && key.EndsWith("_receipt", StringComparison.Ordinal)) throw new IOException("simulated journal interruption");
            var created = _values.TryAdd(run + key, (JsonObject)value.DeepClone());
            return Task.FromResult(new ProgramJournalWrite(created, (JsonObject)_values[run + key].DeepClone()));
        }
        public Task<bool> ReserveToolAsync(string run, string key, CancellationToken ct) => Task.FromResult(AllowBudget);
    }
}
