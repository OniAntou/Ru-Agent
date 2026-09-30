using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Worker;

namespace RuBackend.Harness.Worker;

/// <summary>
/// TEMPORARY T1 bridge. <see cref="LoopbackModelStepRunner"/> still encodes the
/// worker v2 wire until T2 replaces it with the in-process runner; these are the
/// four prompt-wire conversions it and its tests still call. The worker-side
/// codec was deleted with the worker transport, so this file moves with the
/// runner in T2.
/// </summary>
internal static class LoopbackPromptWire
{
    internal static JsonObject WriteContext(SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var json = Encoding.UTF8.GetString(context.SerializeUtf8());
        if (Encoding.UTF8.GetByteCount(json) > ModelGenerationBounds.MaxPromptContextBytes)
        {
            throw new AgentProtocolException("invalid_request");
        }

        return JsonNode.Parse(json)!.AsObject();
    }

    internal static JsonArray WriteModelMessages(IReadOnlyList<ModelTurn> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var result = new JsonArray();
        foreach (var message in messages)
        {
            result.Add(WriteModelMessage(message));
        }

        return result;
    }

    internal static JsonObject WriteReceipt(SystemPromptReceipt receipt)
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

    internal static SystemPromptReceipt ReadReceipt(JsonObject? receipt, SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (receipt is null || !HasExactFields(receipt, ReceiptFields)
            || receipt["composer_version"] is not JsonValue composerValue
            || !composerValue.TryGetValue<string>(out var composerVersion)
            || receipt["prompt_sha256"] is not JsonValue hashValue
            || !hashValue.TryGetValue<string>(out var promptHash)
            || receipt["block_digests"] is not JsonArray blocks
            || blocks.Count > ModelGenerationBounds.MaxPromptReceiptBlocks)
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

    private static readonly IReadOnlySet<string> ReceiptFields = new HashSet<string>(
        ["composer_version", "prompt_sha256", "block_digests"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> BlockDigestFields = new HashSet<string>(
        ["block_id", "sha256"], StringComparer.Ordinal);

    private static JsonObject WriteModelMessage(ModelTurn message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content,
        };
        if (message.Role == "assistant" && message.ToolCalls.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.GetRawText(),
                    },
                });
            }
            result["tool_calls"] = calls;
        }
        else if (message.Role == "tool")
        {
            result["tool_call_id"] = message.ToolCallId;
            result["tool_result_code"] = message.ToolResultCode;
        }

        return result;
    }

    private static bool HasExactFields(JsonObject value, IReadOnlySet<string> expected) =>
        value.Count == expected.Count
        && value.Select(property => property.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected);
}
