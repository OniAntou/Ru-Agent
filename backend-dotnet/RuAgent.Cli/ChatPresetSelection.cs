using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

internal sealed record ChatPresetSelectionResult(ChatCommand? Command, string? Error = null);

/// <summary>Chooses the next turn's intent; the existing run-start path still owns policy updates.</summary>
internal static class ChatPresetSelection
{
    internal static async Task<ChatPresetSelectionResult> SelectAsync(
        ChatCommand current, string? argument, bool hasRetainedRun,
        Func<ChoiceList, CancellationToken, Task<string?>>? picker, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (hasRetainedRun)
            return new(null, "preset_change_blocked: finish or release the retained run before changing permissions.");

        var requested = argument;
        if (string.IsNullOrWhiteSpace(requested))
        {
            if (picker is null)
                return new(null, "interactive_required: use /preset plan or /preset standard; full_access requires an interactive confirmation.");
            var presets = new[]
            {
                new ChoiceItem("plan", "Read only; no terminal or file changes", "plan"),
                new ChoiceItem("standard", "Workspace edits and terminal; scoped approvals", "standard"),
                new ChoiceItem("full_access", "Skip approvals; requires explicit confirmation", "full_access"),
            };
            var selected = Array.FindIndex(presets, item => item.Value == current.Preset);
            requested = await picker(new ChoiceList("permissions", presets, Math.Max(0, selected),
                "Applies to new turns · esc keeps current preset"), ct).ConfigureAwait(false);
            if (requested is null) return new(null);
        }

        if (!AgentPolicyProfileCatalog.TryNormalizePresetInput(requested, out var preset))
            return new(null, "invalid_preset: choose plan, standard, or full_access.");

        var confirmed = false;
        if (preset == "full_access")
        {
            if (picker is null)
                return new(null, "full_access_confirmation_required: choose full_access inside the interactive TUI.");
            var answer = await picker(new ChoiceList("Enable Full Access?", [
                new ChoiceItem("Keep current preset", current.Preset, "keep"),
                new ChoiceItem("Enable full_access", "Any file, terminal and network as Windows user", "enable_full_access"),
            ], selected: 0, footer: "Choose explicitly · esc keeps current preset"), ct).ConfigureAwait(false);
            if (answer != "enable_full_access") return new(null);
            confirmed = true;
        }

        ct.ThrowIfCancellationRequested();
        return new(current with { Preset = preset, ConfirmFullAccess = confirmed });
    }
}
