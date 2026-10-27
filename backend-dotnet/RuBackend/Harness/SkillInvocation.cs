using RuAgent.Broker.Skills;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol;

namespace RuBackend.Harness;

internal sealed record SkillInvocationResult(bool IsInvocation, SkillBody? Skill = null,
    string? EncodedSnapshot = null, string? Code = null, string? Message = null);

/// <summary>Explicit user selection resolves guidance before dispatch, without changing tool authority.</summary>
internal static class SkillInvocation
{
    internal static async Task<SkillInvocationResult> ResolveAsync(string workspaceRoot, string prompt,
        Func<bool> pythonAvailable, CancellationToken ct)
    {
        var text = prompt.Trim();
        if (!text.StartsWith("/skill", StringComparison.OrdinalIgnoreCase)
            || (text.Length > 6 && !char.IsWhiteSpace(text[6]))) return new(false);
        var parsed = AgentActionRouter.Parse(text);
        if (!parsed.Success || parsed.Command is not { SkillId: { } id } command)
            return new(true, Code: "command_invalid", Message: parsed.SafeMessage);
        var catalog = new SkillCatalog(workspaceRoot);
        ct.ThrowIfCancellationRequested();
        var listed = await catalog.DiscoverAsync(query: id, cancellationToken: ct).ConfigureAwait(false);
        var item = listed.Items.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
        if (item is null) return new(true, Code: "skill_not_found", Message: "The selected skill was not found.");
        if (!item.Enabled) return new(true, Code: "skill_disabled", Message: "The selected skill is disabled.");
        if (!item.UserInvocable) return new(true, Code: "skill_not_user_invocable", Message: "This skill does not allow user invocation.");
        if (!catalog.TryLoadBody(id, out var body, out var loadCode, allowExplicit: true) || body is null)
            return new(true, Code: loadCode ?? "skill_unavailable", Message: "The selected skill could not be loaded. Refresh the catalog and validate its diagnostics.");
        var unmet = SkillRequirements.FindUnmet(body.Requires, requirement =>
            string.Equals(requirement, "python", StringComparison.OrdinalIgnoreCase)
                ? pythonAvailable() : SkillCapabilityAvailability.IsAvailable(requirement));
        if (unmet is not null) return new(true, Code: unmet.Code, Message: unmet.Message);
        string snapshot;
        try
        {
            snapshot = ActiveSkillSnapshots.EncodeExplicit(body.Id, body.Name, body.Description, body.Body,
                body.BodySha256, body.SourcePath, body.ResourceBase, body.Revision, command.SkillArguments ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return new(true, Code: "skill_context_oversize", Message: "The skill and expanded arguments exceed the workflow context limit.");
        }
        return new(true, body, snapshot);
    }
}
