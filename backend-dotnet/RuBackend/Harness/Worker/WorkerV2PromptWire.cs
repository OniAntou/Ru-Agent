using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Worker;

namespace RuBackend.Harness.Worker;

/// <summary>
/// The single C# JSON boundary for typed Worker v2 prompt context and receipts.
/// </summary>
internal static class WorkerV2PromptWire
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> ReceiptFields = new HashSet<string>(
        ["composer_version", "prompt_sha256", "block_digests"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> BlockDigestFields = new HashSet<string>(
        ["block_id", "sha256"],
        StringComparer.Ordinal);

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
            });
        }

        var capabilities = new JsonArray();
        foreach (var capability in context.Capabilities)
        {
            capabilities.Add(new JsonObject
            {
                ["id"] = capability.Id,
                ["version"] = capability.Version,
                ["summary"] = capability.Summary,
                ["input_schema_digest"] = capability.InputSchemaDigest,
                ["policy_summary"] = capability.PolicySummary,
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
                ["memory"] = context.RuntimeContext.Memory,
                ["mood"] = context.RuntimeContext.Mood,
                ["voice"] = context.RuntimeContext.Voice,
                ["vts"] = context.RuntimeContext.Vts,
                ["hardware"] = context.RuntimeContext.Hardware,
                ["references"] = references,
            },
        };

        if (StrictUtf8.GetByteCount(payload.ToJsonString()) > WorkerV2Contracts.MaxPromptContextBytes)
        {
            throw new WorkerProtocolException("invalid_request");
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

    public static SystemPromptReceipt ReadReceipt(
        JsonObject? receipt,
        SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (receipt is null
            || !HasExactFields(receipt, ReceiptFields)
            || receipt["composer_version"] is not JsonValue composerValue
            || !composerValue.TryGetValue<string>(out var composerVersion)
            || receipt["prompt_sha256"] is not JsonValue promptHashValue
            || !promptHashValue.TryGetValue<string>(out var promptSha256)
            || receipt["block_digests"] is not JsonArray blocks
            || blocks.Count > WorkerV2Contracts.MaxPromptReceiptBlocks)
        {
            throw new WorkerProtocolException("invalid_step");
        }

        var blockDigests = new List<PromptBlockDigest>(blocks.Count);
        foreach (var blockNode in blocks)
        {
            if (blockNode is not JsonObject block
                || !HasExactFields(block, BlockDigestFields)
                || block["block_id"] is not JsonValue blockIdValue
                || !blockIdValue.TryGetValue<string>(out var blockId)
                || block["sha256"] is not JsonValue digestValue
                || !digestValue.TryGetValue<string>(out var digest))
            {
                throw new WorkerProtocolException("invalid_step");
            }

            try
            {
                blockDigests.Add(new PromptBlockDigest(blockId, digest));
            }
            catch (ArgumentException)
            {
                throw new WorkerProtocolException("invalid_step");
            }
        }

        try
        {
            var typedReceipt = new SystemPromptReceipt(composerVersion, promptSha256, blockDigests);
            typedReceipt.ValidateAgainst(context);
            return typedReceipt;
        }
        catch (ArgumentException)
        {
            throw new WorkerProtocolException("invalid_step");
        }
    }

    private static bool HasExactFields(JsonObject value, IReadOnlySet<string> expected)
    {
        return value.Count == expected.Count
            && value.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }
}
