namespace RuAgent.Cli.Tui;

/// <summary>A screen can replace its provisional Markdown row before committing it.</summary>
public interface ITranscriptLineWriter
{
    void PreviewLine(string text);
    void CommitLine(IReadOnlyList<string> rows);
}
