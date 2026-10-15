namespace RuAgent.Cli.Tui;

/// <summary>Updates a tool result in place when its detail frame arrives late.</summary>
public interface ITranscriptActivityWriter
{
    void AppendToolRow(string activityId, string text);
    void UpdateToolRow(string activityId, string text);
}
