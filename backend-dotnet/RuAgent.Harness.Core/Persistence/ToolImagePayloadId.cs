using System.Security.Cryptography;
using System.Text;

namespace RuAgent.Harness.Core.Persistence;

/// <summary>
/// Links a <see cref="HarnessPayloadKind.ToolImage"/> payload to the tool result it
/// belongs to without a schema column or an extra event (ADR 0026): the image id is
/// derived from the result's <see cref="HarnessPayloadKind.ToolOutput"/> payload id.
/// </summary>
public static class ToolImagePayloadId
{
    private const string Domain = "ru.tool_image.v1\n";

    public static PayloadId For(PayloadId resultPayloadId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + resultPayloadId.Value));
        return new PayloadId("payload_" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant());
    }
}
