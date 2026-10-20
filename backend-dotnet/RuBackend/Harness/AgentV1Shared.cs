using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness;

/// <summary>
/// Pure shared helpers extracted from AgentV1Service (slice 1).
/// No DB, no Broker, no runtime state. Business logic unchanged.
/// </summary>
internal static class AgentV1Shared
{
    internal const int MaxCursorChars = 512;
    internal const int MaxCursorBytes = 384;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static AgentServiceResult BoundedError(int status, string code, string message) =>
        new(status, new AgentErrorResponse(code, message));

    /// <summary>
    /// A busy conflict carries the run that holds the resource so a client can tell
    /// the operator exactly which run to cancel instead of only reporting a code.
    /// </summary>
    internal static AgentServiceResult BusyConflict(string code, string message, AgentRun blocking) =>
        new(409, new AgentErrorResponse(code, message, blocking.RunId.Value, MapRunState(blocking.Status)));

    internal static string MapHistoryWaitKind(HistoryWaitKind? kind) => kind switch
    {
        HistoryWaitKind.UserBeforeFirstStep => "user_before_first_step",
        HistoryWaitKind.FinalBeforeCompletion => "final_before_completion",
        null => "none",
        _ => "unknown",
    };

    internal static string MapRunState(HarnessRunStatus status) =>
        MapRunState(status, isWaitingInput: false);

    internal static string MapRunState(HarnessRunStatus status, bool isWaitingInput)
    {
        if (status.HistoryWaitKind is not null)
        {
            return status.HistoryWaitKind == HistoryWaitKind.UserBeforeFirstStep
                ? "waiting_history_user"
                : "waiting_history_final";
        }

        return status.State switch
        {
            HarnessRunState.Created => "created",
            HarnessRunState.WaitingWorkspaceTrust => "waiting_workspace_trust",
            HarnessRunState.WaitingHistory => "waiting_history",
            HarnessRunState.Running => "running",
            HarnessRunState.WaitingApproval => "waiting_approval",
            HarnessRunState.WaitingUser => isWaitingInput ? "waiting_input" : "waiting_user",
            HarnessRunState.WaitingSubagents => "waiting_subagents",
            HarnessRunState.ReconciliationRequired => "reconciliation_required",
            HarnessRunState.Completed => "completed",
            HarnessRunState.Failed => "failed",
            HarnessRunState.Cancelled => "cancelled",
            HarnessRunState.Interrupted => "interrupted",
            _ => "unknown",
        };
    }

    internal static AgentRun AdvanceRun(
        AgentRun current,
        HarnessRunStatus nextStatus,
        int nextBudgetWindow,
        int nextModelSteps,
        int nextToolCalls,
        long appendedCount,
        DateTimeOffset updatedAt,
        string? terminalErrorCode = null)
    {
        DateTimeOffset? terminalAt = nextStatus.State.IsTerminal() ? updatedAt : null;
        return new AgentRun(
            current.RunId,
            current.SessionId,
            current.WorkspaceId,
            nextStatus,
            current.Version + 1,
            current.LastSequence + appendedCount,
            nextBudgetWindow,
            nextModelSteps,
            nextToolCalls,
            current.Preset,
            current.PolicySnapshotVersion,
            current.PolicySnapshotSha256,
            current.CreatedAt,
            updatedAt,
            terminalAt,
            terminalErrorCode,
            current.PermissionProfileVersion);
    }

    internal static string ComputeSha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static bool IsSessionId(string value) => value.Length == 32 && IsLowerHex(value);

    internal static bool IsWorkspaceId(string value) => value.StartsWith("ws_", StringComparison.Ordinal)
        && value.Length == 35
        && IsLowerHex(value.AsSpan(3));

    internal static bool IsRunId(string value) =>
        value.StartsWith("run_", StringComparison.Ordinal) && value.Length == 36 && IsLowerHex(value.AsSpan(4));

    internal static bool IsPayloadId(string value) =>
        value.StartsWith("payload_", StringComparison.Ordinal) && value.Length == 40 && IsLowerHex(value.AsSpan(8));

    internal static bool IsTrustRequestId(string value) =>
        value.StartsWith("trust_", StringComparison.Ordinal) && value.Length == 38 && IsLowerHex(value.AsSpan(6));

    internal static bool IsPolicyRuleId(string value) =>
        value.StartsWith("rule_", StringComparison.Ordinal) && value.Length == 37 && IsLowerHex(value.AsSpan(5));

    internal static bool IsInputId(string value) =>
        value.StartsWith("inp_", StringComparison.Ordinal) && value.Length == 36 && IsLowerHex(value.AsSpan(4));

    internal static bool IsLowerHex(string value) => IsLowerHex(value.AsSpan());

    internal static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static string ResolveWorkspaceFile(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new HarnessStoreException("workspace_path_outside_root");
        }

        return target;
    }

    internal static bool ContainsReparsePointBoundary(string root, string target)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(target);
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            if (string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)
                || !parent.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = parent;
        }
    }

    internal static bool TryReadRestoreContent(ReadOnlySpan<byte> plaintext, out byte[] content)
    {
        content = Array.Empty<byte>();
        try
        {
            using var document = JsonDocument.Parse(plaintext.ToArray());
            if (!document.RootElement.TryGetProperty("content_base64", out var encoded)
                || encoded.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var value = encoded.GetString();
            // An empty string is a valid payload: the original file was empty.
            if (value is null || value.Length > 22_369_624)
            {
                return false;
            }

            content = Convert.FromBase64String(value);
            return content.Length <= 16 * 1024 * 1024;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return false;
        }
    }

    internal static bool IsValidIsoTimestamp(string value)
    {
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _);
    }

    internal static bool TryReadPolicyString(JsonElement obj, string propertyName, int maxLength, out string? value)
    {
        value = null;
        if (!obj.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        if (raw is null
            || raw.Length == 0
            || raw.Length > maxLength
            || string.IsNullOrWhiteSpace(raw)
            || raw.Contains('\0'))
        {
            return false;
        }

        value = raw;
        return true;
    }

    internal static string EncodeCursor(string createdAt, string runId)
    {
        var raw = $"{createdAt}|{runId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    internal static (string CreatedAt, string RunId)? TryDecodeCursor(string cursor)
    {
        if (cursor.Length > MaxCursorChars)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(cursor);
            if (bytes.Length == 0 || bytes.Length > MaxCursorBytes)
            {
                return null;
            }

            var raw = StrictUtf8.GetString(bytes);
            var parts = raw.Split('|');
            if (parts.Length != 2
                || parts[0].Length is 0 or > 64
                || parts[1].Length is 0 or > 64
                || parts[0].Contains('\0')
                || parts[1].Contains('\0')
                || !IsValidIsoTimestamp(parts[0])
                || !IsRunId(parts[1]))
            {
                return null;
            }

            return (parts[0], parts[1]);
        }
        catch
        {
            return null;
        }
    }
}
