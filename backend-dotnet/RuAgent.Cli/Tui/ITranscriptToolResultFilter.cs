namespace RuAgent.Cli.Tui;

/// <summary>Interactive screens can omit successful receipts while retaining diagnostic updates.</summary>
public interface ITranscriptToolResultFilter
{
    void RemoveActivityPresentation(string activityId);
}
