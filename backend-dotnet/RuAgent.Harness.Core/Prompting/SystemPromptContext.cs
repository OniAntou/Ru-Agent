using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// The bounded, host-owned input used to compose one Agent system prompt.
/// These records deliberately contain no model output and no mutable collections.
/// </summary>
public sealed record PromptEnvironment
{
    public PromptEnvironment(
        string model,
        string platform,
        string locale,
        string timezone,
        string date,
        string? workspaceLabel,
        string? toolInventory = null)
    {
        Model = PromptText.Require(model, 256, nameof(model));
        Platform = PromptText.Require(platform, 64, nameof(platform));
        Locale = PromptText.Require(locale, 64, nameof(locale));
        Timezone = PromptText.Require(timezone, 128, nameof(timezone));
        Date = PromptText.Require(date, 32, nameof(date));
        WorkspaceLabel = PromptText.Optional(workspaceLabel, 128, nameof(workspaceLabel));
        ToolInventory = PromptText.Optional(toolInventory, 256, nameof(toolInventory));
    }

    public string Model { get; }

    public string Platform { get; }

    public string Locale { get; }

    public string Timezone { get; }

    public string Date { get; }

    public string? WorkspaceLabel { get; }

    /// <summary>
    /// What shells and helper tools this machine actually has, resolved fresh
    /// per run by PATH probing (no execution). Lets the model reach for what
    /// exists instead of guessing pwsh and learning via executable_not_found.
    /// </summary>
    public string? ToolInventory { get; }
}

public enum PromptInstructionOrigin
{
    Workspace,
    ExternalSkill,
}

public sealed record PromptInstructionBlock
{
    public PromptInstructionBlock(
        string sourceId,
        string displayName,
        string content,
        string contentSha256,
        PromptInstructionOrigin origin = PromptInstructionOrigin.Workspace)
    {
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        SourceId = PromptText.RequireIdentifier(sourceId, 128, nameof(sourceId), allowColon: true);
        DisplayName = PromptText.Require(displayName, 256, nameof(displayName));
        Content = PromptText.NormalizeAndRequireBytes(content, 64 * 1024, nameof(content));
        ContentSha256 = PromptText.RequireDigest(contentSha256, nameof(contentSha256));
        if (!string.Equals(ContentSha256, PromptHash.Sha256(Content), StringComparison.Ordinal))
        {
            throw new ArgumentException("Instruction content digest does not match content.", nameof(contentSha256));
        }

        Origin = origin;
    }

    public string SourceId { get; }

    public string DisplayName { get; }

    public string Content { get; }

    public string ContentSha256 { get; }

    public PromptInstructionOrigin Origin { get; }
}

public sealed record PromptCapability
{
    public PromptCapability(
        string id,
        string version,
        string description,
        string scope,
        string inputSchemaJson,
        string inputSchemaDigest,
        string? exampleArgumentsJson,
        string availability)
    {
        Id = PromptText.RequireIdentifier(id, 128, nameof(id));
        Version = PromptText.RequireSemVer(version, nameof(version));
        Description = PromptText.Require(description, 2 * 1024, nameof(description));
        Scope = PromptText.RequireIdentifier(scope, 128, nameof(scope));
        InputSchemaJson = PromptJson.RequireCanonicalObject(inputSchemaJson, 32 * 1024, nameof(inputSchemaJson));
        InputSchemaDigest = PromptText.RequireDigest(inputSchemaDigest, nameof(inputSchemaDigest));
        ExampleArgumentsJson = PromptJson.OptionalCanonicalObject(exampleArgumentsJson, 4 * 1024, nameof(exampleArgumentsJson));
        Availability = PromptText.RequireIdentifier(availability, 32, nameof(availability));
    }

    public string Id { get; }

    public string Version { get; }

    public string Description { get; }

    public string Scope { get; }

    public string InputSchemaJson { get; }

    public string InputSchemaDigest { get; }

    public string? ExampleArgumentsJson { get; }

    public string Availability { get; }
}

public sealed record PromptReference
{
    /// <summary>
    /// The most content one reference may carry.
    /// </summary>
    /// <remarks>
    /// Public because callers that build references from tool output have to fit inside it.
    /// A reference over this bound is rejected, and a rejected reference fails the whole
    /// prompt rather than the row that was too large -- a listing or a file read big enough
    /// to exceed it used to end the run.
    /// </remarks>
    public const int ContentMaxBytes = 128 * 1024;

    public PromptReference(string kind, string label, string content)
    {
        Kind = PromptText.RequireIdentifier(kind, 64, nameof(kind));
        Label = PromptText.Require(label, 512, nameof(label));
        Content = PromptText.NormalizeAndRequireBytes(content, ContentMaxBytes, nameof(content));
    }

    public string Kind { get; }

    public string Label { get; }

    public string Content { get; }
}

public sealed record PromptRuntimeContext
{
    /// <summary>
    /// The most references one runtime context may carry.
    /// </summary>
    /// <remarks>
    /// Public for the same reason as <see cref="PromptReference.ContentMaxBytes"/>: a
    /// caller that appends references has to fit inside it. A context over the bound is
    /// rejected, and a rejected context fails the whole prompt -- so a long run that had
    /// accumulated more tool results than this used to end with <c>prompt_contract_invalid</c>
    /// instead of costing the model the oldest results. Callers that can exceed it must
    /// keep the newest references and drop the rest.
    /// </remarks>
    public const int MaxRuntimeReferences = 32;

    public PromptRuntimeContext(
        string? mood,
        string? hardware,
        IReadOnlyList<PromptReference> references)
    {
        Mood = PromptText.Optional(mood, 256, nameof(mood));
        Hardware = PromptText.Optional(hardware, 2 * 1024, nameof(hardware));
        ArgumentNullException.ThrowIfNull(references);
        var copiedReferences = references.ToArray();
        if (copiedReferences.Any(reference => reference is null))
        {
            throw new ArgumentException("Prompt references cannot contain null.", nameof(references));
        }

        if (copiedReferences.Length > MaxRuntimeReferences)
        {
            throw new ArgumentException("Prompt references exceed the Core cap.", nameof(references));
        }

        References = new ReadOnlyCollection<PromptReference>(copiedReferences);
    }

    public string? Mood { get; }

    public string? Hardware { get; }

    public IReadOnlyList<PromptReference> References { get; }
}

public sealed record SystemPromptContext
{
    public const int PromptContextSchemaVersion = 2;
    public const string RequiredAgentProfile = "ru-agent";
    public const int MaxInstructionBlocks = 16;
    public const int MaxCapabilities = 16;
    public const int MaxCombinedInstructionBytes = 256 * 1024;
    public const int MaxSerializedBytes = 8 * 1024 * 1024;

    // Transitional empty context for callers that construct requests directly. The
    // production coordinator replaces this with a root-bound context provider before
    // dispatching a model step.
    public static SystemPromptContext Empty => new(
        PromptContextSchemaVersion,
        RequiredAgentProfile,
        "standard",
        new PromptEnvironment("unknown", "unknown", "en-US", "UTC", "1970-01-01", null),
        Array.Empty<PromptInstructionBlock>(),
        Array.Empty<PromptCapability>(),
        new PromptRuntimeContext(null, null, Array.Empty<PromptReference>()));

    public SystemPromptContext(
        int schemaVersion,
        string agentProfile,
        string preset,
        PromptEnvironment environment,
        IReadOnlyList<PromptInstructionBlock> instructionBlocks,
        IReadOnlyList<PromptCapability> capabilities,
        PromptRuntimeContext runtimeContext)
    {
        if (schemaVersion != PromptContextSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (!string.Equals(agentProfile, RequiredAgentProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported Agent profile.", nameof(agentProfile));
        }

        if (!string.Equals(preset, "plan", StringComparison.Ordinal)
            && !string.Equals(preset, "standard", StringComparison.Ordinal)
            && !string.Equals(preset, "full_access", StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported Agent preset.", nameof(preset));
        }

        var effectivePolicy = AgentPolicyProfileCatalog.Get(preset);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(instructionBlocks);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(runtimeContext);

        var copiedInstructions = instructionBlocks.ToArray();
        if (copiedInstructions.Any(block => block is null))
        {
            throw new ArgumentException("Instruction blocks cannot contain null.", nameof(instructionBlocks));
        }

        if (copiedInstructions.Length > MaxInstructionBlocks)
        {
            throw new ArgumentException("Instruction blocks exceed the Core cap.", nameof(instructionBlocks));
        }

        var instructionIds = new HashSet<string>(StringComparer.Ordinal);
        var combinedBytes = 0;
        foreach (var block in copiedInstructions)
        {
            if (!instructionIds.Add(block.SourceId))
            {
                throw new ArgumentException("Instruction source IDs must be unique.", nameof(instructionBlocks));
            }

            combinedBytes = checked(combinedBytes + PromptText.StrictUtf8.GetByteCount(block.Content));
        }

        if (combinedBytes > MaxCombinedInstructionBytes)
        {
            throw new ArgumentException("Combined instruction content exceeds the Core cap.", nameof(instructionBlocks));
        }

        var copiedCapabilities = capabilities.ToArray();
        if (copiedCapabilities.Any(capability => capability is null))
        {
            throw new ArgumentException("Capabilities cannot contain null.", nameof(capabilities));
        }

        if (copiedCapabilities.Length > MaxCapabilities)
        {
            throw new ArgumentException("Capabilities exceed the Core cap.", nameof(capabilities));
        }

        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in copiedCapabilities)
        {
            if (!capabilityIds.Add(capability.Id))
            {
                throw new ArgumentException("Capability IDs must be unique.", nameof(capabilities));
            }
        }

        SchemaVersion = schemaVersion;
        AgentProfile = agentProfile;
        Preset = preset;
        EffectivePolicy = effectivePolicy;
        Environment = environment;
        InstructionBlocks = new ReadOnlyCollection<PromptInstructionBlock>(copiedInstructions);
        Capabilities = new ReadOnlyCollection<PromptCapability>(copiedCapabilities);
        RuntimeContext = runtimeContext;

        if (SerializeUtf8().Length > MaxSerializedBytes)
        {
            throw new ArgumentException("Serialized system prompt context exceeds the Core cap.");
        }
    }

    public int SchemaVersion { get; }

    public string AgentProfile { get; }

    public string Preset { get; }

    public AgentPolicyProfile EffectivePolicy { get; }

    public PromptEnvironment Environment { get; }

    public IReadOnlyList<PromptInstructionBlock> InstructionBlocks { get; }

    public IReadOnlyList<PromptCapability> Capabilities { get; }

    public PromptRuntimeContext RuntimeContext { get; }

    /// <summary>Returns the strict UTF-8 JSON representation used for the context byte cap.</summary>
    public byte[] SerializeUtf8() => JsonSerializer.SerializeToUtf8Bytes(
        new
        {
            schema_version = SchemaVersion,
            agent_profile = AgentProfile,
            preset = Preset,
            effective_policy = AgentPolicyProfileCatalog.WriteProfile(EffectivePolicy),
            environment = new
            {
                model = Environment.Model,
                platform = Environment.Platform,
                locale = Environment.Locale,
                timezone = Environment.Timezone,
                date = Environment.Date,
                workspace_label = Environment.WorkspaceLabel,
                tool_inventory = Environment.ToolInventory,
            },
            instruction_blocks = InstructionBlocks.Select(block => new
            {
                source_id = block.SourceId,
                display_name = block.DisplayName,
                content = block.Content,
                content_sha256 = block.ContentSha256,
                origin = block.Origin.ToString(),
            }),
            capabilities = Capabilities.Select(capability => new
            {
                id = capability.Id,
                version = capability.Version,
                description = capability.Description,
                scope = capability.Scope,
                input_schema_json = capability.InputSchemaJson,
                input_schema_digest = capability.InputSchemaDigest,
                example_arguments_json = capability.ExampleArgumentsJson,
                availability = capability.Availability,
            }),
            runtime_context = new
            {
                mood = RuntimeContext.Mood,
                hardware = RuntimeContext.Hardware,
                references = RuntimeContext.References.Select(reference => new
                {
                    kind = reference.Kind,
                    label = reference.Label,
                    content = reference.Content,
                }),
            },
        });
}

internal static class PromptHash
{
    public static bool IsLowerHexSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(PromptText.StrictUtf8.GetBytes(value))).ToLowerInvariant();
    }
}

internal static class PromptText
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly System.Text.RegularExpressions.Regex SemVer = new(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Require(string? value, int maxChars, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxChars || value.Contains('\0'))
        {
            throw new ArgumentException($"Value must contain between 1 and {maxChars} characters and no NUL.", parameterName);
        }

        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Value is not valid UTF-8 text.", parameterName, error);
        }

        return value;
    }

    public static string? Optional(string? value, int maxChars, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return Require(value, maxChars, parameterName);
    }

    public static string? OptionalBytes(string? value, int maxBytes, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return NormalizeAndRequireBytes(value, maxBytes, parameterName);
    }

    public static string NormalizeAndRequireBytes(string? value, int maxBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Normalize(NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('\0'))
        {
            throw new ArgumentException("Value must be non-empty and contain no NUL.", parameterName);
        }

        try
        {
            if (StrictUtf8.GetByteCount(normalized) > maxBytes)
            {
                throw new ArgumentException($"Value exceeds the {maxBytes}-byte cap.", parameterName);
            }
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Value is not valid Unicode text.", parameterName, error);
        }

        return normalized;
    }

    public static string RequireIdentifier(string? value, int maxChars, string parameterName, bool allowColon = false)
    {
        var text = Require(value, maxChars, parameterName);
        foreach (var character in text)
        {
            var valid = (character is (>= 'A' and <= 'Z')
                or (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '.'
                or '-'
                or '_')
                || (allowColon && character == ':');
            if (!valid)
            {
                throw new ArgumentException("Value is not a safe identifier.", parameterName);
            }
        }

        return text;
    }

    public static string RequireSemVer(string? value, string parameterName)
    {
        var text = Require(value, 32, parameterName);
        if (!SemVer.IsMatch(text))
        {
            throw new ArgumentException("Value is not a semantic version.", parameterName);
        }

        return text;
    }

    public static string RequireDigest(string? value, string parameterName)
    {
        if (!PromptHash.IsLowerHexSha256(value))
        {
            throw new ArgumentException("Value is not a lowercase SHA-256 digest.", parameterName);
        }

        return value!;
    }
}

internal static class PromptJson
{
    public static string RequireCanonicalObject(string value, int maxBytes, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || PromptText.StrictUtf8.GetByteCount(value) > maxBytes)
        {
            throw new ArgumentException("JSON exceeds the capability bound.", parameterName);
        }

        try
        {
            var node = JsonNode.Parse(value);
            if (node is not JsonObject objectNode)
            {
                throw new ArgumentException("Capability JSON must be an object.", parameterName);
            }

            var canonical = objectNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            if (!string.Equals(canonical, value, StringComparison.Ordinal))
            {
                throw new ArgumentException("Capability JSON must be canonical compact JSON.", parameterName);
            }

            return canonical;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Capability JSON is invalid.", parameterName, error);
        }
    }

    public static string? OptionalCanonicalObject(string? value, int maxBytes, string parameterName) =>
        value is null ? null : RequireCanonicalObject(value, maxBytes, parameterName);
}
