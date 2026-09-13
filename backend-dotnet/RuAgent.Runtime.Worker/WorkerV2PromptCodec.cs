using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;

namespace RuAgent.Runtime.Worker;

public sealed record WorkerV2StartRequest(
    string Prompt,
    IReadOnlyList<ModelTurn> Turns,
    SystemPromptContext Context,
    JsonObject Settings,
    IReadOnlyList<ModelImage>? Images = null);

/// <summary>One typed JSON conversion boundary for Worker v2 prompt records.</summary>
public static class WorkerV2PromptCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> ReceiptFields = new HashSet<string>(
        ["composer_version", "prompt_sha256", "block_digests"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> BlockDigestFields = new HashSet<string>(
        ["block_id", "sha256"], StringComparer.Ordinal);

    public static WorkerV2StartRequest ReadStart(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            WorkerV2Contracts.ValidateStart(payload);
            var prompt = RequiredString(payload["prompt"]);
            var turns = new List<ModelTurn>();
            if (payload["turns"] is JsonArray turnArray)
            {
                foreach (var node in turnArray)
                {
                    if (node is not JsonObject turn)
                    {
                        throw new AgentProtocolException("invalid_request");
                    }

                    turns.Add(new ModelTurn("user", RequiredString(turn["prompt"])));
                    turns.Add(new ModelTurn("assistant", RequiredString(turn["response"])));
                }
            }

            var context = ReadContext(payload["system_prompt_context"] as JsonObject
                ?? throw new AgentProtocolException("invalid_request"));
            var settings = payload["settings"] is JsonObject configured
                ? (JsonObject)configured.DeepClone()
                : new JsonObject();

            var images = new List<ModelImage>();
            if (payload["images"] is JsonArray imagesArray)
            {
                foreach (var node in imagesArray)
                {
                    if (node is JsonValue iv && iv.TryGetValue<string>(out var dataUrl))
                    {
                        var semi = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
                        if (semi >= 5)
                        {
                            var mime = dataUrl.Substring(5, semi - 5);
                            var b64 = dataUrl.Substring(semi + 8);
                            images.Add(new ModelImage(mime, b64));
                        }
                    }
                }
            }

            return new WorkerV2StartRequest(prompt, turns, context, settings, images);
        }
        catch (AgentProtocolException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        {
            throw new AgentProtocolException("invalid_request", error.Message);
        }
    }

    public static JsonObject WriteContext(SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var instructions = new JsonArray();
        foreach (var instruction in context.InstructionBlocks)
        {
            instructions.Add(new JsonObject
            {
                ["source_id"] = instruction.SourceId,
                ["display_name"] = instruction.DisplayName,
                ["content"] = instruction.Content,
                ["content_sha256"] = instruction.ContentSha256,
                ["origin"] = instruction.Origin.ToString(),
            });
        }

        var capabilities = new JsonArray();
        foreach (var capability in context.Capabilities)
        {
            capabilities.Add(new JsonObject
            {
                ["id"] = capability.Id,
                ["version"] = capability.Version,
                ["description"] = capability.Description,
                ["scope"] = capability.Scope,
                ["input_schema_json"] = capability.InputSchemaJson,
                ["input_schema_digest"] = capability.InputSchemaDigest,
                ["example_arguments_json"] = capability.ExampleArgumentsJson,
                ["availability"] = capability.Availability,
            });
        }

        var references = new JsonArray();
        foreach (var reference in context.RuntimeContext.References)
        {
            references.Add(new JsonObject
            {
                ["kind"] = reference.Kind,
                ["label"] = reference.Label,
                ["content"] = reference.Content,
            });
        }

        var payload = new JsonObject
        {
            ["schema_version"] = context.SchemaVersion,
            ["agent_profile"] = context.AgentProfile,
            ["preset"] = context.Preset,
            ["environment"] = new JsonObject
            {
                ["model"] = context.Environment.Model,
                ["platform"] = context.Environment.Platform,
                ["locale"] = context.Environment.Locale,
                ["timezone"] = context.Environment.Timezone,
                ["date"] = context.Environment.Date,
                ["workspace_label"] = context.Environment.WorkspaceLabel,
            },
            ["instruction_blocks"] = instructions,
            ["capabilities"] = capabilities,
            ["runtime_context"] = new JsonObject
            {
                ["mood"] = context.RuntimeContext.Mood,
                ["hardware"] = context.RuntimeContext.Hardware,
                ["references"] = references,
            },
        };

        if (StrictUtf8.GetByteCount(payload.ToJsonString()) > WorkerV2Contracts.MaxPromptContextBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }

        return payload;
    }

    public static JsonObject WriteReceipt(SystemPromptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var blocks = new JsonArray();
        foreach (var block in receipt.BlockDigests)
        {
            blocks.Add(new JsonObject
            {
                ["block_id"] = block.BlockId,
                ["sha256"] = block.Sha256,
            });
        }

        return new JsonObject
        {
            ["composer_version"] = receipt.ComposerVersion,
            ["prompt_sha256"] = receipt.PromptSha256,
            ["block_digests"] = blocks,
        };
    }

    public static SystemPromptReceipt ReadReceipt(JsonObject? receipt, SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (receipt is null || !HasExactFields(receipt, ReceiptFields)
            || receipt["composer_version"] is not JsonValue composerValue
            || !composerValue.TryGetValue<string>(out var composerVersion)
            || receipt["prompt_sha256"] is not JsonValue hashValue
            || !hashValue.TryGetValue<string>(out var promptHash)
            || receipt["block_digests"] is not JsonArray blocks
            || blocks.Count > WorkerV2Contracts.MaxPromptReceiptBlocks)
        {
            throw new AgentProtocolException("invalid_step");
        }

        var digests = new List<PromptBlockDigest>(blocks.Count);
        foreach (var node in blocks)
        {
            if (node is not JsonObject block || !HasExactFields(block, BlockDigestFields)
                || block["block_id"] is not JsonValue idValue
                || !idValue.TryGetValue<string>(out var id)
                || block["sha256"] is not JsonValue digestValue
                || !digestValue.TryGetValue<string>(out var digest))
            {
                throw new AgentProtocolException("invalid_step");
            }

            try
            {
                digests.Add(new PromptBlockDigest(id, digest));
            }
            catch (ArgumentException error)
            {
                throw new AgentProtocolException("invalid_step", error.Message);
            }
        }

        try
        {
            var typed = new SystemPromptReceipt(composerVersion, promptHash, digests);
            typed.ValidateAgainst(context);
            return typed;
        }
        catch (ArgumentException error)
        {
            throw new AgentProtocolException("invalid_step", error.Message);
        }
    }

    private static SystemPromptContext ReadContext(JsonObject context)
    {
        if (context["environment"] is not JsonObject environment
            || context["instruction_blocks"] is not JsonArray instructions
            || context["capabilities"] is not JsonArray capabilities
            || context["runtime_context"] is not JsonObject runtime
            || runtime["references"] is not JsonArray references)
        {
            throw new AgentProtocolException("invalid_request");
        }

        var instructionRecords = instructions.Select(node =>
        {
            if (node is not JsonObject value)
            {
                throw new AgentProtocolException("invalid_request");
            }

            var origin = value.ContainsKey("origin")
                ? ReadInstructionOrigin(value["origin"])
                : PromptInstructionOrigin.Workspace;

            return new PromptInstructionBlock(
                RequiredString(value["source_id"]),
                RequiredString(value["display_name"]),
                RequiredString(value["content"]),
                RequiredString(value["content_sha256"]),
                origin);
        }).ToArray();

        var capabilityRecords = capabilities.Select(node =>
        {
            if (node is not JsonObject value)
            {
                throw new AgentProtocolException("invalid_request");
            }

            return new PromptCapability(
                RequiredString(value["id"]),
                RequiredString(value["version"]),
                RequiredString(value["description"]),
                RequiredString(value["scope"]),
                RequiredString(value["input_schema_json"]),
                RequiredString(value["input_schema_digest"]),
                value["example_arguments_json"] is null || value["example_arguments_json"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null
                    ? null
                    : RequiredString(value["example_arguments_json"]),
                RequiredString(value["availability"]));
        }).ToArray();

        var referenceRecords = references.Select(node =>
        {
            if (node is not JsonObject value)
            {
                throw new AgentProtocolException("invalid_request");
            }

            return new PromptReference(
                RequiredString(value["kind"]),
                RequiredString(value["label"]),
                RequiredString(value["content"]));
        }).ToArray();

        return new SystemPromptContext(
            RequiredInt(context["schema_version"]),
            RequiredString(context["agent_profile"]),
            RequiredString(context["preset"]),
            new PromptEnvironment(
                RequiredString(environment["model"]),
                RequiredString(environment["platform"]),
                RequiredString(environment["locale"]),
                RequiredString(environment["timezone"]),
                RequiredString(environment["date"]),
                OptionalString(environment["workspace_label"])),
            instructionRecords,
            capabilityRecords,
            new PromptRuntimeContext(
                OptionalString(runtime["mood"]),
                OptionalString(runtime["hardware"]),
                referenceRecords));
    }

    private static string RequiredString(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<string>(out var text)
        ? text
        : throw new AgentProtocolException("invalid_request");

    private static string? OptionalString(JsonNode? node) => node is null
        || node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Null
        ? null
        : RequiredString(node);

    private static int RequiredInt(JsonNode? node) => node is JsonValue value
        && value.TryGetValue<int>(out var number)
        ? number
        : throw new AgentProtocolException("invalid_request");

    private static PromptInstructionOrigin ReadInstructionOrigin(JsonNode? node)
    {
        var value = RequiredString(node);
        return value switch
        {
            nameof(PromptInstructionOrigin.Workspace) => PromptInstructionOrigin.Workspace,
            nameof(PromptInstructionOrigin.ExternalSkill) => PromptInstructionOrigin.ExternalSkill,
            _ => throw new AgentProtocolException("invalid_request"),
        };
    }

    private static bool HasExactFields(JsonObject value, IReadOnlySet<string> expected) =>
        value.Count == expected.Count
        && value.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
}
