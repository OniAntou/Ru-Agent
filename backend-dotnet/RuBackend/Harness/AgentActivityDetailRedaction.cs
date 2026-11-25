using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

/// <summary>
/// The one secret redaction an activity row's detail gets before it leaves the Host,
/// for the live feed and the durable replay alike.
/// </summary>
/// <remarks>
/// Details carry model-supplied text: a command line that may quote a credential, an
/// edit preview, a search scope. Replay re-projects rows from stored proposals; those were
/// redacted with the secrets known when they were written, and this pass also hides one
/// configured since.
/// </remarks>
internal static class AgentActivityDetailRedaction
{
    internal static string? Redact(string? detail, KnownSecretRedactor secrets)
    {
        if (string.IsNullOrEmpty(detail)) return detail;
        return AgentEditPreviewCodec.TryDecode(detail, out var before, out var after)
            ? AgentEditPreviewCodec.Encode(secrets.Redact(before), secrets.Redact(after))
            : secrets.Redact(detail);
    }
}
