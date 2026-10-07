namespace RuAgent.Broker.Tools.Files;

public static class FileToolLimits
{
    public const int MaxEntries = 512;
    public const int MaxResults = 128;
    public const int MaxGlobResults = 128;
    public const int MaxSearchResults = 128;
    public const int MaxReadBytes = 512 * 1024;
    public const int MaxPathLength = 4096;
    public const int MaxPatternLength = 512;
    public const int MaxQueryLength = 2048;
    public const int MaxSearchFileSize = 1 * 1024 * 1024;
    public const int MaxTotalSearchBytes = 10 * 1024 * 1024;
    public const int MaxTimeMs = 5000;
    public const int DefaultMaxEntries = 512;
    public const int DefaultMaxResults = 128;
    public const int DefaultReadLimit = 512 * 1024;
    public const int MaxRecoveryBytes = 4 * 1024 * 1024;
    public const int HardMaxRecoveryBytes = MaxRecoveryBytes;

    /// <summary>
    /// How far into the ordered grep results a page may start. Skipping is
    /// cheap (no re-scan), so the bound only guards absurd values.
    /// </summary>
    public const int MaxOffset = 50000;

    public static int ValidateRecoveryBytes(int value)
    {
        if (value < 1 || value > HardMaxRecoveryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Recovery cap must be between 1 byte and 4 MiB.");
        }

        return value;
    }
}
