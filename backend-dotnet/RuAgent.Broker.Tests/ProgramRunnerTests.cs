using System.Text.Json.Nodes;
using System.Collections.Concurrent;
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

    [Fact]
    public async Task EffectfulBatchIsRefusedBeforeDispatchingAnyMember()
    {
        var dispatched = 0;
        var result = await new ProgramRunner(new MemoryJournal()).RunAsync("run", "parallel-reject", "binding",
            "tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'write_file',arguments:{path:'x',content:'a'}}]);",
            (_, _, _) => { dispatched++; return Task.FromResult(new JsonObject()); }, default);
        Assert.Equal("program_parallel_tool_not_read_only", result["error"]!.GetValue<string>());
        Assert.Equal(0, dispatched);
        Assert.Equal(0, result["tool_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task InsufficientBatchBudgetDoesNotReserveAPartialBatchOrDispatch()
    {
        var journal = new MemoryJournal { MaximumReservations = 7 };
        var dispatched = 0;
        var code = "tools.parallel(Array(8).fill({tool:'read_file',arguments:{path:'a'}}));";

        var result = await new ProgramRunner(journal).RunAsync("run", "parallel-budget-atomic", "binding", code,
            (_, _, _) => { dispatched++; return Task.FromResult(new JsonObject()); }, default);

        Assert.Equal("program_budget_exhausted", result["error"]!.GetValue<string>());
        Assert.Equal(0, dispatched);
        Assert.Equal(0, journal.ReservationCount);
    }

    [Fact]
    public async Task PreparedBatchIntentResumesAfterPreDispatchJournalFailure()
    {
        var journal = new MemoryJournal { FailIntentKeySuffix = "_1_intent" };
        var dispatched = 0;
        Task<JsonObject> Invoke(ProgramToolRequest _, string __, CancellationToken ___)
        {
            dispatched++;
            return Task.FromResult(new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject() });
        }
        const string code = "tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);";
        var runner = new ProgramRunner(journal);

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("run", "parallel-prepared", "binding", code, Invoke, default));
        Assert.Equal(0, dispatched);
        journal.FailIntentKeySuffix = null;

        var result = await runner.RunAsync("run", "parallel-prepared", "binding", code, Invoke, default);

        Assert.Equal("succeeded", result["status"]!.GetValue<string>());
        Assert.Equal(2, dispatched);
    }

    [Fact]
    public async Task ParallelReadsOverlapButResultsAndReceiptsStayInInputOrder()
    {
        var journal = new MemoryJournal();
        var gates = Enumerable.Range(0, 8).Select(_ => Gate()).ToArray();
        var bothStarted = Gate();
        var started = 0;
        var active = 0;
        var maxActive = 0;
        Task<JsonObject> Invoke(ProgramToolRequest call, string _, CancellationToken __) => InvokeAsync(call);
        async Task<JsonObject> InvokeAsync(ProgramToolRequest call)
        {
            var index = call.Arguments["path"]!.GetValue<string>()[0] - 'a';
            var running = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, running);
            if (Interlocked.Increment(ref started) == 8) bothStarted.TrySetResult();
            try
            {
                await gates[index].Task;
                return new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["value"] = ((char)('a' + index)).ToString() } };
            }
            finally { Interlocked.Decrement(ref active); }
        }
        const string code = "return tools.parallel(["
            + "{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}},"
            + "{tool:'read_file',arguments:{path:'c'}},{tool:'read_file',arguments:{path:'d'}},"
            + "{tool:'read_file',arguments:{path:'e'}},{tool:'read_file',arguments:{path:'f'}},"
            + "{tool:'read_file',arguments:{path:'g'}},{tool:'read_file',arguments:{path:'h'}}"
            + "]).map(r=>r.output.value);";
        var running = new ProgramRunner(journal).RunAsync("run", "parallel-order", "binding", code, Invoke, default);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(8, maxActive);
        foreach (var gate in gates.Reverse()) gate.TrySetResult();
        var result = await running;

        Assert.Equal(Enumerable.Range(0, 8).Select(index => ((char)('a' + index)).ToString()),
            result["result"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Equal("read_file", result["nested_receipts"]![0]!["tool_id"]!.GetValue<string>());
        Assert.Equal("a", result["nested_receipts"]![0]!["arguments"]!["path"]!.GetValue<string>());
        Assert.Equal("h", result["nested_receipts"]![7]!["arguments"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task NestedReceiptCallIndexesRemainGlobalAcrossDirectAndBatchCalls()
    {
        const string code = "tools.read_file({path:'before'}); "
            + "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'grep',arguments:{query:'b'}}]);";
        var result = await new ProgramRunner(new MemoryJournal()).RunAsync("run", "parallel-global-index", "binding", code,
            (call, _, _) => Task.FromResult(new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject
            {
                ["value"] = call.Arguments["path"]?.GetValue<string>() ?? call.Arguments["query"]?.GetValue<string>(),
            } }), default);

        Assert.Equal(new[] { 0, 1, 2 }, result["nested_receipts"]!.AsArray()
            .Select(receipt => receipt!["call_index"]!.GetValue<int>()));
    }

    [Fact]
    public async Task PartialBatchReceiptsSurviveRestartAndUncertainDispatchIsNeverRepeated()
    {
        var journal = new MemoryJournal { FailReceiptKeySuffix = "_1_receipt" };
        var first = Gate();
        var second = Gate();
        var third = Gate();
        var bothStarted = Gate();
        var started = 0;
        var invocations = 0;
        Task<JsonObject> Invoke(ProgramToolRequest call, string _, CancellationToken __)
        {
            Interlocked.Increment(ref invocations);
            var index = call.Arguments["path"]!.GetValue<string>()[0] - 'a';
            if (Interlocked.Increment(ref started) == 3) bothStarted.TrySetResult();
            return WaitAsync(index);
        }
        async Task<JsonObject> WaitAsync(int index)
        {
            await (index switch { 0 => first.Task, 1 => second.Task, _ => third.Task });
            return new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["value"] = index } };
        }
        const string code = "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}},{tool:'read_file',arguments:{path:'c'}}]);";
        var runner = new ProgramRunner(journal);
        var interrupted = runner.RunAsync("run", "parallel-partial", "binding", code, Invoke, default);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.TrySetResult();
        await journal.FirstReceiptWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
        third.TrySetResult();
        await journal.ThirdReceiptWritten.Task.WaitAsync(TimeSpan.FromSeconds(10));
        second.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => interrupted);
        Assert.Equal(3, invocations);
        var reservations = journal.ReservationCount;

        var replay = await runner.RunAsync("run", "parallel-partial", "binding", code, Invoke, default);
        Assert.Equal("program_execution_unknown", replay["error"]!.GetValue<string>());
        Assert.Equal(3, invocations);
        Assert.Equal(reservations, journal.ReservationCount);
        Assert.Equal(2, replay["tool_count"]!.GetValue<int>());
        Assert.Equal(new[] { 0, 2 }, replay["nested_receipts"]!.AsArray()
            .Select(receipt => receipt!["call_index"]!.GetValue<int>()));
    }

    [Fact]
    public async Task CompletedBatchReceiptsReplayWithoutRepeatOrDoubleReservation()
    {
        var journal = new MemoryJournal { FailFinal = true };
        var invocations = 0;
        Task<JsonObject> Invoke(ProgramToolRequest call, string _, CancellationToken __)
        {
            invocations++;
            return Task.FromResult(new JsonObject { ["status"] = "succeeded",
                ["output"] = new JsonObject { ["value"] = call.Arguments["path"]!.GetValue<string>() } });
        }
        const string code = "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);";
        var runner = new ProgramRunner(journal);
        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync("run", "parallel-final", "binding", code, Invoke, default));
        Assert.Equal(2, invocations);
        var reservations = journal.ReservationCount;
        journal.FailFinal = false;

        var replay = await runner.RunAsync("run", "parallel-final", "binding", code, Invoke, default);
        Assert.Equal("succeeded", replay["status"]!.GetValue<string>());
        Assert.Equal(2, invocations);
        Assert.Equal(reservations, journal.ReservationCount);
        Assert.Equal(new[] { "a", "b" }, replay["nested_receipts"]!.AsArray()
            .Select(receipt => receipt!["arguments"]!["path"]!.GetValue<string>()));
    }

    [Fact]
    public async Task BatchJournalFailureDrainsOtherAlreadyStartedReadsBeforeReturning()
    {
        var journal = new MemoryJournal { FailReceiptKeySuffix = "_0_receipt" };
        var first = Gate();
        var second = Gate();
        var bothStarted = Gate();
        var started = 0;
        Task<JsonObject> Invoke(ProgramToolRequest call, string _, CancellationToken __)
        {
            var index = call.Arguments["path"]!.GetValue<string>() == "a" ? 0 : 1;
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            return WaitAsync(index);
        }
        async Task<JsonObject> WaitAsync(int index)
        {
            await (index == 0 ? first.Task : second.Task);
            return new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject() };
        }
        const string code = "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);";
        var running = new ProgramRunner(journal).RunAsync("run", "parallel-drain", "binding", code, Invoke, default);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        first.TrySetResult();
        await journal.ReceiptWriteFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(running.IsCompleted);
        second.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => running);
    }

    [Fact]
    public async Task CancellationDrainsAllStartedReadsAndRestartDoesNotRepeatThem()
    {
        var journal = new MemoryJournal();
        var bothStarted = Gate();
        var bothDrained = Gate();
        var started = 0;
        var drained = 0;
        var invocations = 0;
        Task<JsonObject> Invoke(ProgramToolRequest _, string __, CancellationToken token)
        {
            Interlocked.Increment(ref invocations);
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            return WaitForCancellationAsync(token);
        }
        async Task<JsonObject> WaitForCancellationAsync(CancellationToken token)
        {
            var canceled = Gate();
            using var registration = token.Register(() => canceled.TrySetCanceled());
            try
            {
                await canceled.Task;
                return new JsonObject();
            }
            finally
            {
                if (Interlocked.Increment(ref drained) == 2) bothDrained.TrySetResult();
            }
        }
        const string code = "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);";
        using var cancellation = new CancellationTokenSource();
        var runner = new ProgramRunner(journal);
        var interrupted = runner.RunAsync("run", "parallel-cancel", "binding", code, Invoke, cancellation.Token);
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await bothDrained.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        Assert.Equal(2, invocations);
        var reservations = journal.ReservationCount;

        var replay = await runner.RunAsync("run", "parallel-cancel", "binding", code, Invoke, default);
        Assert.Equal("program_execution_unknown", replay["error"]!.GetValue<string>());
        Assert.Equal(2, invocations);
        Assert.Equal(reservations, journal.ReservationCount);
    }

    [Fact]
    public async Task TraceLimitKeepsAnAuthoritativeFlatPrefixAndFailsClosed()
    {
        var dispatched = 0;
        // Keep each batch result below the value cap while exceeding the
        // accumulated trace cap across batches.
        var code = "for(let batch=0;batch<32;batch++) "
            + "tools.parallel([{tool:'read_file',arguments:{path:'x'}}]); return 1;";
        var result = await new ProgramRunner(new MemoryJournal()).RunAsync("run", "parallel-trace-limit", "binding", code,
            (_, _, _) =>
            {
                dispatched++;
                return Task.FromResult(new JsonObject { ["status"] = "succeeded",
                    ["output"] = new JsonObject { ["content"] = new string('x', 100_000) } });
            }, default);
        Assert.Equal("program_trace_limit", result["error"]!.GetValue<string>());
        Assert.True(result["effects_unknown"]!.GetValue<bool>());
        Assert.Equal(result["nested_receipts"]!.AsArray().Count, result["tool_count"]!.GetValue<int>());
        Assert.All(result["nested_receipts"]!.AsArray(), receipt =>
            Assert.Equal("read_file", receipt!["tool_id"]!.GetValue<string>()));
        Assert.True(dispatched >= result["tool_count"]!.GetValue<int>());
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int previous;
        while ((previous = Volatile.Read(ref maximum)) < candidate
            && Interlocked.CompareExchange(ref maximum, candidate, previous) != previous) { }
    }

    internal sealed class MemoryJournal : IProgramJournal
    {
        private readonly ConcurrentDictionary<string, JsonObject> _values = new();
        private readonly ConcurrentDictionary<string, int> _reservations = new();
        public bool FailReceipt { get; set; }
        public bool FailFinal { get; set; }
        public string? FailIntentKeySuffix { get; set; }
        public string? FailReceiptKeySuffix { get; set; }
        public bool AllowBudget { get; set; } = true;
        public int MaximumReservations { get; set; } = 32;
        public int ReservationCount => _reservations.Values.Sum();
        public TaskCompletionSource FirstReceiptWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdReceiptWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReceiptWriteFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonObject?> ReadAsync(string run, string key, CancellationToken ct) =>
            Task.FromResult(_values.TryGetValue(run + key, out var value) ? (JsonObject?)value.DeepClone() : null);
        public Task<ProgramJournalWrite> CreateAsync(string run, string key, JsonObject value, CancellationToken ct)
        {
            if (FailFinal && key.EndsWith("_final", StringComparison.Ordinal)) throw new IOException("simulated final journal interruption");
            if (FailIntentKeySuffix is not null && key.EndsWith(FailIntentKeySuffix, StringComparison.Ordinal))
                throw new IOException("simulated intent journal interruption");
            if (FailReceipt && key.EndsWith("_receipt", StringComparison.Ordinal)
                || FailReceiptKeySuffix is not null && key.EndsWith(FailReceiptKeySuffix, StringComparison.Ordinal))
            {
                ReceiptWriteFailed.TrySetResult();
                throw new IOException("simulated journal interruption");
            }
            var created = _values.TryAdd(run + key, (JsonObject)value.DeepClone());
            if (created && key.EndsWith("_0_receipt", StringComparison.Ordinal)) FirstReceiptWritten.TrySetResult();
            if (created && key.EndsWith("_2_receipt", StringComparison.Ordinal)) ThirdReceiptWritten.TrySetResult();
            return Task.FromResult(new ProgramJournalWrite(created, (JsonObject)_values[run + key].DeepClone()));
        }
        public Task<bool> ReserveToolAsync(string run, string key, CancellationToken ct)
        {
            if (!AllowBudget) return Task.FromResult(false);
            return Task.FromResult(Reserve(run, key, 1));
        }
        public Task<bool> ReserveToolsAsync(string run, string key, int count, CancellationToken ct) =>
            Task.FromResult(AllowBudget && Reserve(run, key, count));

        private bool Reserve(string run, string key, int count)
        {
            var reservationKey = run + key;
            if (_reservations.TryGetValue(reservationKey, out var existing)) return existing == count;
            lock (_reservations)
            {
                if (_reservations.TryGetValue(reservationKey, out existing)) return existing == count;
                if (ReservationCount + count > MaximumReservations) return false;
                _reservations[reservationKey] = count;
                return true;
            }
        }
    }
}
