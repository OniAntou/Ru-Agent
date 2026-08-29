using System.Text.Json;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// Encodes the small durable receipt envelope. The prompt text and instruction
/// contents deliberately have no representation in this payload.
/// </summary>
internal static class PromptReceiptPayloadCodec
{
    internal const int MaximumBytes = 16 * 1024;

    internal static byte[] Serialize(SystemPromptReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("composer_version", receipt.ComposerVersion);
            writer.WriteString("prompt_sha256", receipt.PromptSha256);
            writer.WriteStartArray("block_digests");
            foreach (var block in receipt.BlockDigests)
            {
                writer.WriteStartObject();
                writer.WriteString("block_id", block.BlockId);
                writer.WriteString("sha256", block.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidOperationException("prompt_receipt_size_exceeded");
        }

        return bytes;
    }
}
