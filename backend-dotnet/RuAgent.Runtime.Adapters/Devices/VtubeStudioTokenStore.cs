using System.Text;

namespace RuAgent.Runtime.Adapters.Devices;

public interface IVtubeStudioTokenStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(string token, CancellationToken cancellationToken);
}

public sealed class NullVtubeStudioTokenStore : IVtubeStudioTokenStore
{
    public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task WriteAsync(string token, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FileVtubeStudioTokenStore : IVtubeStudioTokenStore
{
    private const int MaxTokenChars = 4_096;
    private readonly string _path;

    /// <summary>
    /// Resolves the same canonical token location used by the Python
    /// compatibility runtime. <c>RU_DATA_ROOT</c> is a complete isolation
    /// override; normal desktop runs keep the token under the user's
    /// <c>%LOCALAPPDATA%\Ru</c> directory.
    /// </summary>
    public static string ResolveDefaultPath()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("RU_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(Path.GetFullPath(configuredRoot.Trim()), "pyvts_token.txt");
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return Path.Combine(Path.GetFullPath(root), "Ru", "pyvts_token.txt");
    }

    public FileVtubeStudioTokenStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxTokenChars)
        {
            throw new ArgumentException("VTube Studio token path is invalid.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            if (new FileInfo(_path).Length > Encoding.UTF8.GetMaxByteCount(MaxTokenChars))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            var bytes = await ReadBoundedBytesAsync(
                _path,
                Encoding.UTF8.GetMaxByteCount(MaxTokenChars),
                cancellationToken).ConfigureAwait(false);
            var token = Encoding.UTF8.GetString(bytes).Trim();
            return IsSafeToken(token) ? token : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsSafeToken(token))
        {
            throw new ArgumentException("VTube Studio token is invalid.", nameof(token));
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_path, token, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSafeToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Length <= MaxTokenChars
        && !token.Any(char.IsControl);

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("VTube Studio token is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }
}
