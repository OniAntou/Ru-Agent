using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Protocol;

namespace RuAgent.Broker.Transport;

public sealed partial class AgentConnection
{
    private async Task<IReadOnlyList<AgentEnvelope>> HandleSkillStartAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!_session.Owns(request.SessionId))
        {
            // SkillStart has a dedicated response stream (SkillEvent/SkillResult).
            // Keep even pre-session failures on that stream so strict clients do
            // not have to special-case a ToolResult with a different shape.
            return [SkillResultError(request, "broker_not_ready", "session is not open")];
        }

        var skillId = ReadOptionalPayloadString(request.Payload, "skill_id");
        var version = ReadOptionalPayloadString(request.Payload, "version") ?? "1.0.0";
        if (skillId is null || version is null)
        {
            return [SkillResultError(request, "invalid_skill", "skill identity is invalid")];
        }

        if (request.Payload["input"] is not JsonObject input)
        {
            return [SkillResultError(request, "schema_invalid", "skill input must be an object")];
        }

        var skill = _skillResolver?.Invoke(skillId, version)
            ?? _skillListProvider?.Invoke(request.SessionId)
                .FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal)
                    && string.Equals(item.Version, version, StringComparison.Ordinal));
        if (skill is null || !_session.SkillIds.Contains(skillId))
        {
            return [SkillResultError(request, "skill_not_granted", "skill is not granted")];
        }

        var canonicalHash = ComputeSkillRequestHash(skillId, version, input);
        if (_skillRequests.TryGetValue(request.RequestId, out var replay))
        {
            if (!string.Equals(replay.CanonicalHash, canonicalHash, StringComparison.Ordinal))
            {
                return [SkillResultError(request, "request_conflict", "request id is already bound to another skill input")];
            }

            return replay.Messages
                .Select(message => message with { Payload = (JsonObject)message.Payload.DeepClone() })
                .ToArray();
        }

        if (_skillExecutor is null)
        {
            return [SkillResultError(request, "broker_not_ready", "skill execution is not registered")];
        }

        SkillRun run;
        try
        {
            run = await _skillExecutor(
                request.SessionId,
                skillId,
                version,
                (JsonObject)input.DeepClone(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run = new SkillRun(
                $"skill-{Guid.NewGuid():N}",
                request.SessionId,
                skillId,
                SkillRunState.Cancelled,
                0,
                "Skill cancelled.",
                "cancelled");
        }
        catch (Exception)
        {
            run = new SkillRun(
                $"skill-{Guid.NewGuid():N}",
                request.SessionId,
                skillId,
                SkillRunState.Failed,
                0,
                "Skill failed.",
                "skill_failed");
        }

        var messages = new List<AgentEnvelope>();
        foreach (var step in run.StepReceipts)
        {
            messages.Add(Reply(request, AgentMessageTypes.SkillEvent, new JsonObject
            {
                ["status"] = step.Status,
                ["skill_id"] = run.SkillId,
                ["step_index"] = step.StepIndex,
                ["step_kind"] = step.Kind.ToString(),
                ["step_name"] = step.Name,
                ["tool_id"] = step.ToolId,
                ["safe_summary"] = ClampProtocolText(step.SafeSummary),
            }));
        }

        var resultPayload = new JsonObject
        {
            ["status"] = run.State switch
            {
                SkillRunState.Completed => ToolResultStatuses.Succeeded,
                SkillRunState.Cancelled => ToolResultStatuses.Cancelled,
                SkillRunState.ApprovalRequired => ToolResultStatuses.ApprovalRequired,
                _ => ToolResultStatuses.Failed,
            },
            ["skill_id"] = run.SkillId,
            ["version"] = version,
            ["run_id"] = run.RunId,
            ["receipt_id"] = run.Receipt,
            ["state"] = run.State.ToString(),
            ["step_count"] = run.StepReceipts.Count,
            ["safe_summary"] = ClampProtocolText(run.SafeSummary),
        };
        if (run.ErrorCode is not null)
        {
            resultPayload["error_code"] = StableErrorCode(run.ErrorCode, "skill_failed");
        }

        if (run.Output is not null && run.Output.ToJsonString().Length <= 256 * 1024)
        {
            resultPayload["output"] = run.Output.DeepClone();
        }

        messages.Add(Reply(request, AgentMessageTypes.SkillResult, resultPayload));
        _skillRequests[request.RequestId] = new SkillRequestReplay(canonicalHash, messages.ToArray());
        return messages;
    }

    private SkillManifest? ResolveRequestedSkill(string requestedId, string sessionId)
    {
        var separator = requestedId.LastIndexOf('@');
        if (separator > 0 && separator < requestedId.Length - 1)
        {
            var id = requestedId[..separator];
            var version = requestedId[(separator + 1)..];
            return _skillResolver?.Invoke(id, version)
                ?? _skillListProvider?.Invoke(sessionId)
                    .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)
                        && string.Equals(item.Version, version, StringComparison.Ordinal));
        }

        var catalog = _skillCatalogProvider?.Invoke()
            ?? _skillListProvider?.Invoke(sessionId)
            ?? Array.Empty<SkillManifest>();
        return catalog
            .Where(item => string.Equals(item.Id, requestedId, StringComparison.Ordinal))
            .OrderByDescending(item => item.Version, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string ComputeSkillRequestHash(string skillId, string version, JsonObject input)
    {
        var canonical = skillId + "\n" + version + "\n" + input.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static AgentEnvelope SkillResultError(AgentEnvelope request, string errorCode, string summary)
    {
        // SkillResult has one stable shape for both success and failure. Keep
        // failure replies parseable by the strict host client without echoing
        // untrusted request text: malformed identities use a fixed placeholder.
        var skillId = ReadOptionalPayloadString(request.Payload, "skill_id");
        if (skillId is null || !IsProtocolSkillId(skillId))
        {
            skillId = "invalid.skill";
        }

        var version = ReadOptionalPayloadString(request.Payload, "version");
        if (version is null || !IsProtocolVersion(version))
        {
            version = "1.0.0";
        }

        var runId = "skill-error-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestId))).ToLowerInvariant();
        return Reply(request, AgentMessageTypes.SkillResult, new JsonObject
        {
            ["status"] = ToolResultStatuses.Denied,
            ["skill_id"] = skillId,
            ["version"] = version,
            ["run_id"] = runId,
            ["state"] = SkillRunState.Failed.ToString(),
            ["step_count"] = 0,
            ["safe_summary"] = ClampProtocolText(summary),
            ["error_code"] = StableErrorCode(errorCode, "skill_failed"),
            ["output"] = new JsonObject(),
        });
    }

    private static bool IsProtocolSkillId(string value) =>
        value.Length is > 0 and <= 128
        && value[0] is >= 'a' and <= 'z'
        && value.Contains('.', StringComparison.Ordinal)
        && value.Split('.').All(segment =>
            segment.Length > 0
            && (segment[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
            && segment.All(character => character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-'));

    private static bool IsProtocolVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3
            && parts.All(part => part.Length > 0 && part.Length <= 8 && part.All(char.IsAsciiDigit))
            && parts.All(part => part.Length == 1 || part[0] != '0');
    }

    private static string ClampProtocolText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? "Skill step completed."
            : text.Length <= 512 ? text : text[..512];

    private static string StableErrorCode(string? code, string fallback)
    {
        if (string.IsNullOrWhiteSpace(code)
            || code.Length > 64
            || code.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_'))
        {
            return fallback;
        }

        return code;
    }

    private static IReadOnlySet<string> ReadSkillIds(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("skill_ids", out var raw) || raw is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (raw is not JsonArray values)
        {
            throw new AgentProtocolException("invalid_skill_ids");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is JsonValue item
                && item.TryGetValue<string>(out var skillId)
                && !string.IsNullOrWhiteSpace(skillId)
                && skillId.Length <= 128
                && result.Add(skillId))
            {
                continue;
            }

            throw new AgentProtocolException("invalid_skill_ids");
        }

        return result;
    }
}
