using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Tools;

namespace RuAgent.Harness.Core;

/// <summary>
/// The one classification of a tool call's access kind. The Broker gateway and the hook gateway both
/// call this, so they cannot disagree about the same tool. The class comes from the tool's descriptor
/// row. The only argument-dependent refinement is the computer tool's observation actions, which
/// change nothing (ADR 0031). A tool with no descriptor row is Terminal: unknown authority fails closed.
/// </summary>
public static class ToolAccessClassification
{
    public static ToolAccessKind Classify(string toolId, JsonObject? arguments)
    {
        var row = AgentToolDescriptorTable.Find(toolId);
        if (row is null)
        {
            return ToolAccessKind.Terminal;
        }

        if (row.Id is "computer" && ComputerActions.IsObservation(ActionOf(arguments)))
        {
            return ToolAccessKind.ReadOnly;
        }

        return row.Access switch
        {
            AgentToolAccess.ReadOnly => ToolAccessKind.ReadOnly,
            AgentToolAccess.Mutation => ToolAccessKind.Mutation,
            AgentToolAccess.Terminal => ToolAccessKind.Terminal,
            _ => throw new InvalidOperationException("unmapped_tool_access"),
        };
    }

    private static string? ActionOf(JsonObject? arguments) =>
        arguments?["action"] is JsonValue action && action.TryGetValue<string>(out var name) ? name : null;
}
