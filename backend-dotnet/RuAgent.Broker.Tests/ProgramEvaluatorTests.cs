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
