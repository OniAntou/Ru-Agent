using System.Security.Cryptography;
using System.Text;

namespace RuAgent.Harness.Core.Persistence;

/// <summary>
/// The id of a model step's stored reasoning, derived from the step id so the live
/// thinking row can name the payload before the step commits it, without threading a
/// generated id through every commit path. One step stores at most one reasoning payload.
/// </summary>
public static class ModelReasoningPayloadId
{
    private const string Domain = "ru.model_reasoning.v1\n";

    /// <summary>
    /// The most reasoning one step stores, in UTF-8 bytes. Long enough for the reasoning
    /// of any realistic step; past it the head is kept and the cut says so.
    /// </summary>
    public const int MaximumBytes = 512 * 1024;

    /// <summary>The line that ends a stored reasoning cut at <see cref="MaximumBytes"/>.</summary>
    public const string TruncationNote = "\n\n[Reasoning truncated: Ru keeps the first 512 KB of a step's thinking.]";

    public static PayloadId For(ModelStepId modelStepId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Domain + modelStepId.Value));
        return new PayloadId("payload_" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant());
    }
}
