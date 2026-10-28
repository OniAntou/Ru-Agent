namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task<bool> RequireSavedSessionAsync()
        {
            if (!string.IsNullOrEmpty(sessionId)) return true;
            await WriteDiagnosticLineAsync(humanOut, "no_session: send a message, select a session or use /new first.").ConfigureAwait(false);
            return false;
        }
    }
}
