using Acornima;
using Acornima.Ast;

namespace RuAgent.Broker.Programs;

internal static class ProgramSyntaxGuard
{
    public static string? Validate(string source)
    {
        try
        {
            var script = new Parser().ParseScript(source, "program.js", true);
            var pending = new Stack<Node>();
            pending.Push(script);
            while (pending.TryPop(out var node))
            {
                if (node is IFunction { Async: true } or AwaitExpression) return "program_async_unsupported";
                if (node is IFunction { Generator: true } or ImportExpression) return "program_syntax_not_supported";
                foreach (var child in node.ChildNodes) pending.Push(child);
            }
            return null;
        }
        catch (ParseErrorException) { return "program_script_error"; }
    }
}
