using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    // Fixed read-only argv through the bounded Git channel; the adapter keeps one
    // invocation per call and the parser owns the porcelain-v2 interpretation.
    private readonly GitStatusAdapter _gitStatus = new();

    private async ValueTask<ToolExecutionOutcome> ExecuteGitStatusAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = arguments;
        var report = await _gitStatus.ExecuteAsync(
            ToolsFor(context).CanonicalRoot,
            cancellationToken).ConfigureAwait(false);

        return report is { Succeeded: true, Output: { } output }
            ? ToolExecutionOutcome.Succeeded(
                output,
                ToolVerificationStates.NotRequired,
                DescribeGitStatus(output))
            : ToolExecutionOutcome.Failed(
                report.ErrorCode!,
                report.ErrorHint!,
                ToolVerificationStates.NotRequired);
    }

    private static string DescribeGitStatus(JsonObject output)
    {
        if (output["is_repository"] is JsonValue repositoryValue
            && repositoryValue.TryGetValue<bool>(out var isRepository)
            && !isRepository)
        {
            return "Workspace is not a Git repository.";
        }

        var staged = output["staged"]?.AsArray().Count ?? 0;
        var unstaged = output["unstaged"]?.AsArray().Count ?? 0;
        var untracked = output["untracked"]?.AsArray().Count ?? 0;
        var unmerged = output["unmerged"]?.AsArray().Count ?? 0;
        return staged + unstaged + untracked + unmerged == 0
            ? "Git working tree is clean."
            : $"Git status read: {staged} staged, {unstaged} unstaged, {untracked} untracked, {unmerged} unmerged.";
    }
}
