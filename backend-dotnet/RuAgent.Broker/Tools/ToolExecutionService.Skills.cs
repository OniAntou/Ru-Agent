using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService : IToolInvoker
{
    public async Task<SkillRun> ExecuteSkillAsync(
        string sessionId,
        string skillId,
        string version,
        JsonObject input,
        CancellationToken cancellationToken)
    {
        var runId = $"skill-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(sessionId) || !_session.Owns(sessionId))
        {
            return new SkillRun(runId, sessionId, skillId, SkillRunState.Failed, 0, "Skill is not granted.", "skill_not_granted", null, null, runId);
        }

        SkillManifest skill;
        try
        {
            skill = _skills.Resolve(skillId, version);
        }
        catch (KeyNotFoundException)
        {
            return new SkillRun(runId, sessionId, skillId, SkillRunState.Failed, 0, "Skill is not available.", "skill_not_granted", null, null, runId);
        }

        var grant = CreateSkillGrant(sessionId);
        var decision = _policy.EvaluateSkill(grant, skill.Id, skill.RequiredScopes);
        if (!decision.Allowed)
        {
            return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Failed, 0, "Skill is not granted.", decision.Code, null, null, runId);
        }

        return await _skillOrchestrator.RunAsync(
            sessionId,
            skill,
            grant,
            input,
            cancellationToken).ConfigureAwait(false);
    }

    private PolicyGrant CreateSkillGrant(string sessionId) =>
        new(
            sessionId,
            _session.Scopes,
            _session.Descriptor.ExpiresAt,
            _session.SkillIds);

    private IReadOnlySet<string> EnabledWebScopes()
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        if (_webOptions.FetchEnabled)
        {
            scopes.Add(ToolScope.WebFetch);
        }

        if (_webOptions.SearchEnabled)
        {
            scopes.Add(ToolScope.WebSearch);
        }

        return scopes;
    }

    public async Task<ToolResult> ExecuteSkillAsync(
        AgentEnvelope request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var skillId = ReadString(request.Payload, "skill_id");
            var version = ReadOptionalString(request.Payload, "version") ?? "1.0.0";
            var input = request.Payload["input"] is JsonObject objectNode
                ? objectNode
                : throw new ToolExecutionException("schema_invalid");
            var run = await ExecuteSkillAsync(
                request.SessionId,
                skillId,
                version,
                input,
                cancellationToken).ConfigureAwait(false);
            var status = run.State switch
            {
                SkillRunState.Completed => ToolResultStatuses.Succeeded,
                SkillRunState.Cancelled => ToolResultStatuses.Cancelled,
                SkillRunState.ApprovalRequired => ToolResultStatuses.ApprovalRequired,
                _ => ToolResultStatuses.Failed,
            };
            return Result(
                request,
                status,
                run.Output?.DeepClone().AsObject() ?? new JsonObject(),
                run.ErrorCode,
                run.SafeSummary,
                started,
                ToolVerificationStates.NotRequired,
                adapterInvoked: run.StepReceipts.Any(step => step.ToolId is not null));
        }
        catch (ToolExecutionException error)
        {
            return Result(request, ToolResultStatuses.Denied, new JsonObject(), error.Code, "Skill request was rejected.", started);
        }
        catch (Exception)
        {
            return Result(request, ToolResultStatuses.Failed, new JsonObject(), "skill_failed", "Skill failed.", started);
        }
    }
}
