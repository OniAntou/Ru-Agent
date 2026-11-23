using System.Text.RegularExpressions;

namespace RuAgent.Harness.Core;

/// <summary>
/// Bounded, message-free identity of the failure that ended a run: the exception's
/// kind bucket and the throwing type and method. Both are validated here, so the
/// terminal state can never carry a message, a path or a secret, and a failure whose
/// code is only the driver fallback still says where it happened without opening a log.
/// </summary>
public sealed record RunFailureFingerprint
{
    public const int MaxKindChars = 32;
    public const int MaxSiteChars = 160;

    private static readonly Regex SafeKind = new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);
    private static readonly Regex SafeSite = new("^[A-Za-z0-9_.`<>+]{1,160}$", RegexOptions.Compiled);

    public RunFailureFingerprint(string kind, string? site = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (!SafeKind.IsMatch(kind))
        {
            throw new ArgumentException("Failure kind must be a bounded lowercase token.", nameof(kind));
        }

        string? normalizedSite = string.IsNullOrWhiteSpace(site) ? null : site.Trim();
        if (normalizedSite is not null && !SafeSite.IsMatch(normalizedSite))
        {
            throw new ArgumentException("Failure site must be a type and method name.", nameof(site));
        }

        Kind = kind;
        Site = normalizedSite;
    }

    public string Kind { get; }

    /// <summary>Throwing type and method, or null when the runtime could not name it.</summary>
    public string? Site { get; }
}
