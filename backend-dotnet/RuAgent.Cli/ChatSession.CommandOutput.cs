using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private TerminalGui.TgCommandOutput? nativeCommandOutput;

        private Task RunCommandControlAsync(string title, Func<Task> action, CancellationToken ct,
            bool report = false, bool captureWelcome = true, bool captureOnChat = false) => nativeCommandOutput is { } native
                ? native.RunAsync(title, report, action, ct, captureWelcome, captureOnChat)
                : action();

        private bool IsLocalInputCommand(string line)
        {
            var command = line.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
            if (!command.StartsWith('/')) return false;
            if (ChatCommandCatalog.KnownCommands.Any(item => item.Command.Equals(command, StringComparison.OrdinalIgnoreCase))) return true;
            return CustomPromptCommands.Find(command, canonicalWorkspace,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is null;
        }
    }
}
