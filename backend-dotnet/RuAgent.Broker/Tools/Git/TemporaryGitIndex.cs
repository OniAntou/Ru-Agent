namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// A disposable private index file selected through GIT_INDEX_FILE. All preview staging
/// happens against this index so the real index is never touched until an approved
/// execution explicitly stages the approved paths.
/// </summary>
public sealed class TemporaryGitIndex : IDisposable
{
    public const string EnvironmentVariable = "GIT_INDEX_FILE";

    private TemporaryGitIndex(string path)
    {
        IndexPath = path;
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [EnvironmentVariable] = path,
        };
    }

    public static TemporaryGitIndex Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "ru-git-index-" + Guid.NewGuid().ToString("N"));
        using (File.Create(path))
        {
        }

        return new TemporaryGitIndex(path);
    }

    public string IndexPath { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(IndexPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
