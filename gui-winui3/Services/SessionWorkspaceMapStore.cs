using System.Text.Json;

namespace RuApp.Services;

/// <summary>Client-side session-to-workspace bindings for sessions with no server workspace yet.</summary>
public sealed class SessionWorkspaceMapStore
{
    private const int MaximumPreferenceBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public SessionWorkspaceMapStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "session-workspaces.json");
    }

    public string? Lookup(string sessionId) =>
        Load().TryGetValue(sessionId, out var path) ? path : null;

    public bool Set(string sessionId, string workspacePath)
    {
        var map = Load();
        map[sessionId] = workspacePath;
        return TrySave(map);
    }

    public bool Remove(string sessionId)
    {
        var map = Load();
        if (!map.Remove(sessionId))
        {
            return false;
        }

        return TrySave(map);
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var bytes = ReadBoundedBytes(_filePath, MaximumPreferenceBytes);
            if (bytes is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static byte[]? ReadBoundedBytes(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new MemoryStream(Math.Min(maximumBytes, 8192));
        var buffer = new byte[Math.Min(maximumBytes, 8192)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private bool TrySave(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(map, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }
}
