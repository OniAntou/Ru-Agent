using System.Text.Json.Nodes;
using RuAgent.Broker.Programs;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProgramEvaluatorTests
{
    [Fact]
    public void YieldsThenReplaysPlainJsonWithoutRepeatingCalls()
    {
        const string code = "const r=tools.read_file({path:'a.cs'}); return {lines:r.output.content.split('\\n').length};";
        var evaluator = new ProgramEvaluator();
        var first = evaluator.Evaluate(code, [], CancellationToken.None);
        Assert.Equal("read_file", first.Pending!.ToolId);
        var receipt = new ProgramToolReceipt("read_file", first.Pending.Arguments,
            new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["content"] = "a\nb" } });
        var replay = evaluator.Evaluate(code, [receipt], CancellationToken.None);
        Assert.Null(replay.Pending);
        Assert.Equal(2, replay.Result!["lines"]!.GetValue<int>());
    }

    [Fact]
    public void ParallelYieldsOnlyReadToolsAndReplaysResultsInInputOrder()
    {
        const string code = "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'grep',arguments:{query:'b'}}]).map(r=>r.output.value);";
        var evaluator = new ProgramEvaluator();
        var first = evaluator.Evaluate(code, [], CancellationToken.None);
        Assert.Null(first.Pending);
        Assert.Equal(0, first.PendingBatch!.StartIndex);
        Assert.Equal(new[] { "read_file", "grep" }, first.PendingBatch.Calls.Select(call => call.ToolId));

        var receipts = first.PendingBatch.Calls.Select((call, index) => new ProgramToolReceipt(call.ToolId,
            call.Arguments, new JsonObject { ["status"] = "succeeded", ["output"] = new JsonObject { ["value"] = index + 1 } })).ToArray();
        var replay = evaluator.Evaluate(code, receipts, CancellationToken.None);
        Assert.Null(replay.PendingBatch);
        Assert.Equal(new[] { 1, 2 }, replay.Result!.AsArray().Select(value => value!.GetValue<int>()));
    }

    [Theory]
    [InlineData("tools.parallel([{tool:'write_file',arguments:{path:'x',content:'a'}}]);", "program_parallel_tool_not_read_only")]
    [InlineData("tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'write_file',arguments:{path:'x',content:'a'}}]);", "program_parallel_tool_not_read_only")]
    [InlineData("tools.parallel(Array(9).fill({tool:'read_file',arguments:{path:'a'}}));", "program_parallel_limit")]
    public void ParallelValidatesWholeBatchBeforeYielding(string code, string expected)
    {
        var result = new ProgramEvaluator().Evaluate(code, [], CancellationToken.None);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Null(result.Pending);
        Assert.Null(result.PendingBatch);
    }

    [Fact]
    public void ParallelSharesTheExistingThirtyTwoCallBudget()
    {
        var priorReads = Enumerable.Range(0, 25).Select(index =>
            new ProgramToolReceipt("read_file", new JsonObject { ["path"] = index.ToString() }, new JsonObject())).ToArray();
        var code = "for(let i=0;i<25;i++) tools.read_file({path:String(i)}); "
            + "return tools.parallel(Array(8).fill({tool:'read_file',arguments:{path:'batch'}}));";
        var result = new ProgramEvaluator().Evaluate(code, priorReads, CancellationToken.None);
        Assert.Equal("program_call_limit", result.ErrorCode);
        Assert.Null(result.PendingBatch);
    }

    [Fact]
    public void SelectedResultAndBatchArgumentsKeepExistingValueBounds()
    {
        var oversizedReceipt = new ProgramToolReceipt("read_file", new JsonObject { ["path"] = "a" },
            new JsonObject { ["output"] = new JsonObject { ["content"] = new string('x', ProgramEvaluator.MaximumValueBytes) } });
        var result = new ProgramEvaluator().Evaluate("return tools.read_file({path:'a'}).output.content;",
            [oversizedReceipt], CancellationToken.None);
        Assert.Equal("program_result_limit", result.ErrorCode);

        var batch = new ProgramEvaluator().Evaluate(
            "tools.parallel([{tool:'read_file',arguments:{path:'x'.repeat(300000)}}]);", [], CancellationToken.None);
        Assert.Equal("program_parallel_arguments_limit", batch.ErrorCode);
        Assert.Null(batch.PendingBatch);

        var largeOutput = new string('x', ProgramEvaluator.MaximumValueBytes / 2);
        var pending = new ProgramEvaluator().Evaluate(
            "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);",
            [], CancellationToken.None);
        var receipts = pending.PendingBatch!.Calls.Select(call => new ProgramToolReceipt(call.ToolId, call.Arguments,
            new JsonObject { ["output"] = new JsonObject { ["content"] = largeOutput } })).ToArray();
        var oversizedBatchResult = new ProgramEvaluator().Evaluate(
            "return tools.parallel([{tool:'read_file',arguments:{path:'a'}},{tool:'read_file',arguments:{path:'b'}}]);",
            receipts, CancellationToken.None);
        Assert.Equal("program_result_limit", oversizedBatchResult.ErrorCode);
    }

    [Fact]
    public void ChangedOrUnconsumedReplayIsRejected()
    {
        var receipt = new ProgramToolReceipt("read_file", new JsonObject { ["path"] = "old" }, new JsonObject());
        Assert.Equal("program_replay_mismatch", new ProgramEvaluator().Evaluate("return tools.read_file({path:'new'});", [receipt], default).ErrorCode);
        Assert.Equal("program_replay_mismatch", new ProgramEvaluator().Evaluate("return 1;", [receipt], default).ErrorCode);
    }

    [Theory]
    [InlineData("while(true) {}", "program_compute_limit")]
    [InlineData("return Promise.resolve(1);", "program_async_unsupported")]
    [InlineData("void(async()=>{await 0;tools.write_file({path:'x',content:'a'});})(); return 'done';", "program_async_unsupported")]
    [InlineData("return tools.call('run_code',{code:'return 1;'});", "program_tool_not_supported")]
    [InlineData("return System.IO.File.ReadAllText('x');", "program_script_error")]
    [InlineData("return Math.random();", "program_script_error")]
    [InlineData("return tools.read_file(undefined);", "program_arguments_invalid")]
    public void ComputationAndSdkAreBounded(string code, string expected) =>
        Assert.Equal(expected, new ProgramEvaluator().Evaluate(code, [], default).ErrorCode);

    [Fact]
    public void ScriptCannotCatchYieldOrReplaceJsonBridge()
    {
        var result = new ProgramEvaluator().Evaluate("JSON.stringify=()=> 'forged'; try {tools.write_file({path:'x',content:'a'});} catch(e) {} return 'hidden';", [], default);
        Assert.Equal("write_file", result.Pending!.ToolId);
    }

    [Fact]
    public void CanceledComputationStops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new ProgramEvaluator().Evaluate("return 1;", [], cts.Token));
    }
}
