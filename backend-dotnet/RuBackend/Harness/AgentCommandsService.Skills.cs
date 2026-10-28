using System.Text;
using RuAgent.Broker.Skills;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol;
using static RuBackend.Harness.AgentV1Shared;

namespace RuBackend.Harness;

public sealed partial class AgentCommandsService
{
    private async Task<AgentServiceResult> DispatchSkillsAsync(AgentRun run, string runIdRaw, CancellationToken ct)
    {
        var root = ResolveRunWorkspaceRoot(run.RunId);
        if (root is null) return BoundedError(409, "workspace_unknown", "The run has no workspace root.");
        var loaded = await new SkillCatalog(root).DiscoverAsync(cancellationToken: ct).ConfigureAwait(false);
        return CommandSucceeded(runIdRaw, "skills", new
        {
            items = loaded.Items.Select(skill => new
            {
                id = skill.Id, name = skill.Id, display_name = skill.Name, description = skill.Description,
                source_path = skill.SourcePath, resource_base = skill.ResourceBase, revision = skill.Revision,
                enabled = skill.Enabled, model_invocable = skill.ModelInvocable, user_invocable = skill.UserInvocable,
            }),
            diagnostics = loaded.Diagnostics.Select(item => new { source_path = item.SourcePath, code = item.Code }),
            truncated = loaded.Truncated, total = loaded.Total,
        }, run.Version);
    }

    private async Task<AgentServiceResult> DispatchSkillAsync(AgentRun run, string runIdRaw, AgentCommand command, CancellationToken ct)
    {
        var root = ResolveRunWorkspaceRoot(run.RunId);
        if (root is null) return BoundedError(409, "workspace_unknown", "The run has no workspace root.");
        var text = $"/skill {command.SkillId} {command.SkillArguments}";
        var selected = await SkillInvocation.ResolveAsync(root, text, PythonAvailable, ct).ConfigureAwait(false);
        if (selected.Code is not null) return BoundedError(409, selected.Code, selected.Message ?? "Skill invocation failed.");
        if (selected.Skill is not { } skill || selected.EncodedSnapshot is not { } encoded)
            return BoundedError(400, "command_invalid", "A skill ID is required.");
        var bytes = Encoding.UTF8.GetBytes(encoded);
        var payload = new HarnessPayloadIntent(_ids.NewPayloadId(), HarnessPayloadKind.SkillInvocation,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunNoteAdded,
            payloadId: payload.PayloadId, safeCode: "skill_invoked");
        var next = AdvanceRun(run, run.Status, run.BudgetWindow, run.ModelStepsInWindow,
            run.ToolCallsInWindow, 1, _time.UtcNow < run.UpdatedAt ? run.UpdatedAt : _time.UtcNow);
        var committed = await _store.CommitAsync(new HarnessCommit(run.RunId, run.Version, next,
            [evt], [payload], []), ct).ConfigureAwait(false);
        if (committed.Outcome != HarnessCommitOutcome.Committed)
            return BoundedError(409, "commit_conflict", "The run changed. Retry the skill invocation.");
        return CommandSucceeded(runIdRaw, "skill", new
        {
            id = skill.Id, name = skill.Name, description = skill.Description, body = skill.Body,
            body_sha256 = skill.BodySha256, source_path = skill.SourcePath, resource_base = skill.ResourceBase,
            revision = skill.Revision, activated = true,
        }, committed.Run!.Version);
    }
}
