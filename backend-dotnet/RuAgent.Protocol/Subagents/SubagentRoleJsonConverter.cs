using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Subagents;

public sealed class SubagentRoleJsonConverter : JsonConverter<SubagentRole>
{
    public override SubagentRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("subagent_role_invalid");
        return reader.GetString() switch
        {
            "coding" => SubagentRole.Coding,
            "review" => SubagentRole.Review,
            _ => throw new JsonException("subagent_role_invalid"),
        };
    }

    public override void Write(Utf8JsonWriter writer, SubagentRole value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            SubagentRole.Coding => "coding",
            SubagentRole.Review => "review",
            _ => throw new JsonException("subagent_role_invalid"),
        });
}
