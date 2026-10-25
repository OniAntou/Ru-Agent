using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private async ValueTask<ToolExecutionOutcome> ExecuteLineReadAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct) =>
        ToolExecutionOutcome.Succeeded(await ResolveTools(context, TargetPathOf(arguments)).Read.ExecuteLinesAsync(arguments, ct)
            .ConfigureAwait(false), ToolVerificationStates.NotRequired, "Workspace file read by line.");

    private ValueTask<ToolExecutionOutcome> ExecuteMultiEditAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tools = ResolveTools(context, TargetPathOf(arguments));
        return Succeeded(tools.Edit.ExecuteMany(WithInternalExpectedHash(tools.MutationResolver, arguments)), "Workspace file edited atomically.");
    }
}
