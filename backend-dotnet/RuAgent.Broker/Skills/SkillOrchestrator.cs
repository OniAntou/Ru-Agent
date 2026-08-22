using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public interface IToolInvoker
{
    Task<ToolResult> ExecuteAsync(
        string sessionId,
        ToolManifest manifest,
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken);
}

public sealed class SkillOrchestrator
{
    private readonly ToolRegistry _tools;
    private readonly ToolPolicyEngine _policy;
    private readonly IToolInvoker _invoker;

    public SkillOrchestrator(ToolRegistry tools, ToolPolicyEngine policy, IToolInvoker invoker)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public async Task<SkillRun> RunAsync(
        string sessionId,
        SkillManifest skill,
        PolicyGrant grant,
        JsonObject input,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(input);
        var runId = $"skill-{Guid.NewGuid():N}";
        var started = DateTimeOffset.UtcNow;
        var safeSummary = "Skill completed.";
        for (var index = 0; index < skill.Steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMilliseconds(skill.TimeoutMs))
            {
                return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Failed, index, "Skill timed out.");
            }

            var step = skill.Steps[index];
            if (step.Kind != SkillStepKind.CallTool || string.IsNullOrWhiteSpace(step.ToolId))
            {
                continue;
            }

            var manifest = _tools.List().FirstOrDefault(tool => tool.Id == step.ToolId)
                ?? throw new InvalidOperationException("skill_tool_not_registered");
            var decision = _policy.Evaluate(grant, manifest);
            if (!decision.Allowed)
            {
                return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Failed, index, "Skill policy denied a tool.");
            }

            if (decision.RequiresApproval || skill.RequiresApproval)
            {
                return new SkillRun(runId, sessionId, skill.Id, SkillRunState.ApprovalRequired, index, "Approval is required.");
            }

            var result = await _invoker.ExecuteAsync(
                sessionId,
                manifest,
                step.Arguments?.DeepClone().AsObject() ?? new JsonObject(),
                Math.Min(manifest.TimeoutMs, skill.TimeoutMs),
                cancellationToken).ConfigureAwait(false);
            if (!ToolResultStatuses.Known.Contains(result.Status)
                || result.Status != ToolResultStatuses.Succeeded)
            {
                return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Failed, index, "Skill tool failed.");
            }

            safeSummary = result.SafeSummary.Length <= 4096 ? result.SafeSummary : result.SafeSummary[..4096];
        }

        return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Completed, skill.Steps.Count, safeSummary);
    }
}
