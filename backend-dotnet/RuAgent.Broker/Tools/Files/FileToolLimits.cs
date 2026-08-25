namespace RuAgent.Broker.Tools.Files;

public static class FileToolLimits
{
    public const int MaxEntries = 512;
    public const int MaxResults = 64;
    public const int MaxGlobResults = 64;
    public const int MaxSearchResults = 64;
    public const int MaxReadBytes = 256 * 1024;
    public const int MaxPathLength = 4096;
    public const int MaxPatternLength = 512;
    public const int MaxQueryLength = 1024;
    public const int MaxSearchFileSize = 1 * 1024 * 1024;
    public const int MaxTotalSearchBytes = 10 * 1024 * 1024;
    public const int MaxTimeMs = 5000;
    public const int DefaultMaxEntries = 512;
    public const int DefaultMaxResults = 64;
    public const int DefaultReadLimit = 256 * 1024;
}
