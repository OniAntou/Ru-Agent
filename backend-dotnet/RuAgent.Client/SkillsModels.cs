using System.Text.Json.Serialization;

namespace RuAgent.Client;

public sealed record SkillCatalogItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("resource_base")] string ResourceBase,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("model_invocable")] bool ModelInvocable,
    [property: JsonPropertyName("user_invocable")] bool UserInvocable,
    [property: JsonPropertyName("requirements")] IReadOnlyList<string> Requirements,
    [property: JsonPropertyName("compatibility")] string? Compatibility);

public sealed record SkillCatalogDiagnostic(
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("code")] string Code);

public sealed record SkillCatalogResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<SkillCatalogItem> Items,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<SkillCatalogDiagnostic> Diagnostics,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("total")] int Total);

public sealed record SkillSettingsRequest(
    [property: JsonPropertyName("workspace_path")] string WorkspacePath,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("root")] string? Root = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null);
