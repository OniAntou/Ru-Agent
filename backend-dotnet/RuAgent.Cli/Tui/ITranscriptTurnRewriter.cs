namespace RuAgent.Cli.Tui;

/// <summary>Repairs a completed turn without discarding its tool/result rows.</summary>
public interface ITranscriptTurnRewriter
{
    void BeginAssistantTurn();
    Task<bool> ReplaceAssistantTurnAsync(IReadOnlyList<string> prose);
}
