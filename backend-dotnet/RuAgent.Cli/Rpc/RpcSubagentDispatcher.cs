using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Cli.Rpc;

public sealed partial class RpcDispatcher
{
    private async Task<JsonNode> DispatchSubagentAsync(RpcRequest request, CancellationToken ct)
    {
        var root = RequireRunId(request);
        if (request.Method == "subagent.list")
            return JsonSerializer.SerializeToNode(await _agentClient.ListSubagentsAsync(root, ct), JsonOptions)!;
        var agent = RequireString(request, "agentId", 128);
        if (!SubagentContractValidator.IsValidId(agent))
            throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: invalid agentId");
        object result;
        switch (request.Method)
        {
            case "subagent.get":
                var offset = OptionalLong(request, "offset") ?? 0;
                var limit = OptionalLong(request, "limit") ?? 32768;
                if (offset is < 0 or > int.MaxValue || limit is < 4 or > 32768)
                    throw new RpcDispatchException(RpcProtocol.InvalidParams, "invalid_params: invalid result page");
                result = await _agentClient.GetSubagentAsync(root, agent, OptionalLong(request, "resultVersion"), (int)offset, (int)limit, ct);
                break;
            default: throw new RpcDispatchException(RpcProtocol.MethodNotFound, "method_not_found");
        }
        return JsonSerializer.SerializeToNode(result, JsonOptions)!;
    }
}
