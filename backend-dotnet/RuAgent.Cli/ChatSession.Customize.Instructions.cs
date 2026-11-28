using RuAgent.Client;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task<string?> ShowCustomizeInstructionsAsync(CancellationToken ct)
        {
            var workspacePath = InstructionFileStore.WorkspacePath(canonicalWorkspace);
            string? notice = null;
            while (!ct.IsCancellationRequested)
            {
                var rows = new List<ChoiceItem>
                {
                    new("All projects", "~/.ru/AGENTS.md", "global"),
                    new("This workspace", ".ru/AGENTS.md", "workspace", Enabled: workspacePath is not null),
                    new("Shared project files", "Show instruction sources", "sources", Enabled: workspacePath is not null),
                };
                if (notice is not null) rows.Add(new(notice, "", "", Enabled: false));
                var choice = await PickCustomizeAsync(2, rows, ct).ConfigureAwait(false);
                if (choice is null or PreviousCustomizeTab or NextCustomizeTab) return choice;
                if (choice == "sources")
                {
                    var rowsByPath = InstructionFileStore.OtherWorkspaceSources(canonicalWorkspace)
                        .Select(file => $"{file.Name} · {(file.Exists ? "present" : "not found")}").ToArray();
                    await TgPanelRequests.RequestDocumentAsync(tgWindow!, "Shared project files", rowsByPath, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                var global = choice == "global";
                var path = global ? InstructionFileStore.GlobalPath() : workspacePath;
                if (path is not null)
                    notice = await EditCustomizeInstructionsAsync(global ? "All projects" : "This workspace", path, ct)
                        .ConfigureAwait(false);
            }
            return null;
        }

        private async Task<string?> EditCustomizeInstructionsAsync(string scope, string path, CancellationToken ct)
        {
            var title = "Instructions · " + scope;
            var loaded = await ReadCustomizeInstructionsAsync(path, title, ct).ConfigureAwait(false);
            if (loaded is null) return null;
            var draft = loaded.Text;
            while (!ct.IsCancellationRequested)
            {
                var edit = await TgPanelRequests.RequestDocumentEditAsync(tgWindow!, title, path, loaded.Text, draft, ct)
                    .ConfigureAwait(false);
                if (edit is null || ct.IsCancellationRequested) return null;
                draft = edit;
                if (draft == InstructionFileStore.NormalizeNewlines(loaded.Text)) return null;

                var outcome = InstructionFileStore.Save(loaded, draft);
                if (outcome == InstructionSaveOutcome.Saved) return "Saved · applies from the next message";
                if (outcome == InstructionSaveOutcome.ChangedOnDisk)
                {
                    var current = await ReadCustomizeInstructionsAsync(path, title, ct).ConfigureAwait(false);
                    if (current is null) continue;
                    var choice = await PickCustomizeActionAsync("Instructions changed on disk",
                        [
                            new("Keep editing", "Keep your unsaved draft", "keep"),
                            new("Reload disk copy", "Discard your draft", "reload"),
                            new("Replace disk copy", "Save your draft over the revision just read", "replace"),
                        ], ct).ConfigureAwait(false);
                    if (choice == "reload")
                    {
                        loaded = current;
                        draft = current.Text;
                        continue;
                    }
                    if (choice != "replace" || ct.IsCancellationRequested) continue;
                    // Compare against the revision read for this confirmation, so later changes still get a conflict.
                    outcome = InstructionFileStore.Save(current, draft);
                    if (outcome == InstructionSaveOutcome.Saved) return "Saved · applies from the next message";
                }
                var problem = outcome switch
                {
                    InstructionSaveOutcome.Unsafe => "Ru cannot save an instruction file through a link.",
                    InstructionSaveOutcome.TooLarge => "Ru can read up to 64 KiB per instruction file.",
                    InstructionSaveOutcome.ChangedOnDisk => "The file changed again. Your draft is still open.",
                    _ => "Could not save the instruction file. Your draft is still open.",
                };
                await TgPanelRequests.RequestDocumentAsync(tgWindow!, title, [problem], ct).ConfigureAwait(false);
            }
            return null;
        }

        private async Task<InstructionFileSnapshot?> ReadCustomizeInstructionsAsync(string path, string title, CancellationToken ct)
        {
            try
            {
                var loaded = InstructionFileStore.Read(path);
                if (!loaded.Text.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r')))
                    return loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await TgPanelRequests.RequestDocumentAsync(tgWindow!, title,
                    ["Could not read the instruction file."], ct).ConfigureAwait(false);
                return null;
            }
            await TgPanelRequests.RequestDocumentAsync(tgWindow!, title,
                ["This file contains control characters. Open it in your editor to review it."], ct).ConfigureAwait(false);
            return null;
        }
    }
}
