using System.Text;
using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;

namespace RuAgent.Broker.Programs;

public sealed record ProgramToolRequest(string ToolId, JsonObject Arguments);
public sealed record ProgramToolReceipt(string ToolId, JsonObject Arguments, JsonObject Response);
public sealed record ProgramEvaluation(JsonNode? Result = null, ProgramToolRequest? Pending = null, string? ErrorCode = null);

/// <summary>Pure computation only. Each uncached call unwinds the interpreter;
/// asynchronous execution happens outside Jint, then a fresh engine replays receipts.</summary>
public sealed class ProgramEvaluator
{
    public const int MaximumCalls = 32;
    public const int MaximumCodeBytes = 16 * 1024;
    public const int MaximumValueBytes = 256 * 1024;
    public static IReadOnlySet<string> SupportedTools => RuAgent.Protocol.Programs.ProgramToolContract.SupportedTools;

    public ProgramEvaluation Evaluate(string code, IReadOnlyList<ProgramToolReceipt> receipts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(code) > MaximumCodeBytes || receipts.Count > MaximumCalls)
            return new(ErrorCode: "program_input_limit");
        var source = "(function(){\n" + code + "\n})()";
        if (ProgramSyntaxGuard.Validate(source) is { } syntaxError) return new(ErrorCode: syntaxError);
        ProgramToolRequest? pending = null;
        string? refusal = null;
        var cursor = 0;
        using var engine = new Engine(options => options.Strict().LimitMemory(32 * 1024 * 1024)
            .MaxStatements(100_000).LimitRecursion(64).TimeoutInterval(TimeSpan.FromMilliseconds(250))
            .RegexTimeoutInterval(TimeSpan.FromMilliseconds(50)).CancellationToken(ct));
        // Only JSON strings cross the CLR boundary. The delegate is captured in
        // a closure and removed from the global object before any user code runs.
        engine.SetValue("__program_call", new Func<string, string, string>((tool, arguments) =>
        {
            if (pending is not null || refusal is not null) throw new ProgramYieldException();
            if (!SupportedTools.Contains(tool)) refusal = "program_tool_not_supported";
            else if (Encoding.UTF8.GetByteCount(arguments) > MaximumValueBytes) refusal = "program_arguments_limit";
            else
            {
                var args = JsonNode.Parse(arguments) as JsonObject;
                if (args is null) refusal = "program_arguments_invalid";
                else if (cursor < receipts.Count)
                {
                    var receipt = receipts[cursor++];
                    if (receipt.ToolId != tool || !JsonNode.DeepEquals(receipt.Arguments, args)) refusal = "program_replay_mismatch";
                    else return receipt.Response.ToJsonString();
                }
                else if (cursor >= MaximumCalls) refusal = "program_call_limit";
                else pending = new ProgramToolRequest(tool, args);
            }
            throw new ProgramYieldException();
        }));
        try
        {
            engine.Execute("""
                const __program_bridge = (() => {
                  const host = __program_call, stringify = JSON.stringify, parse = JSON.parse;
                  delete globalThis.__program_call;
                  const sdk = Object.create(null);
                  sdk.call = (name,args) => {
                    const json=stringify(args);
                    if (typeof name !== 'string' || typeof json !== 'string') throw new Error('program_arguments_invalid');
                    return parse(host(name,json));
                  };
                  return { sdk, serialize(value) {
                    if (value && typeof value.then === 'function') throw new Error('program_async_unsupported');
                    return stringify(value === undefined ? null : value);
                  }};
                })();
                globalThis.Date = undefined;
                globalThis.Intl = undefined;
                globalThis.Temporal = undefined;
                globalThis.performance = undefined;
                globalThis.eval = undefined;
                Object.defineProperty(Function.prototype, 'constructor', {value:undefined, writable:false, configurable:false});
                globalThis.Function = undefined;
                Object.defineProperty(globalThis, 'Promise', {get(){throw new Error('program_async_unsupported');}, configurable:false});
                Math.random = undefined;
                """);
            var serialize = engine.Evaluate("__program_bridge.serialize");
            foreach (var tool in SupportedTools)
                engine.Execute($"__program_bridge.sdk.{tool} = args => __program_bridge.sdk.call('{tool}',args);");
            engine.Execute("const tools=Object.freeze(__program_bridge.sdk);");
            var value = engine.Evaluate(source);
            var json = engine.Invoke(serialize, value).AsString();
            if (pending is null && refusal is null)
            {
                if (cursor != receipts.Count) return new(ErrorCode: "program_replay_mismatch");
                if (Encoding.UTF8.GetByteCount(json) > MaximumValueBytes) return new(ErrorCode: "program_result_limit");
                return new(Result: JsonNode.Parse(json));
            }
        }
        catch (ProgramYieldException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (JavaScriptException error)
        {
            refusal ??= error.Message is "program_async_unsupported" or "program_arguments_invalid" ? error.Message : "program_script_error";
        }
        catch (Exception error) when (error is StatementsCountOverflowException or MemoryLimitExceededException
            or TimeoutException or RecursionDepthOverflowException)
        {
            refusal ??= "program_compute_limit";
        }
        ct.ThrowIfCancellationRequested();
        return new(Pending: refusal is null ? pending : null, ErrorCode: refusal);
    }

    private sealed class ProgramYieldException : Exception;
}
