using System.Text.Json;

namespace RuAgent.Broker.Runtime;

public sealed class SafeAgentArtifactWriter
{
    private static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(
        ["component", "check_id", "tool_id", "duration_ms", "exit_code", "status", "error_code"],
        StringComparer.Ordinal);

    public void Write(string path, IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fields);

        foreach (var (key, value) in fields)
        {
            if (!AllowedKeys.Contains(key))
            {
                throw new ArgumentException($"Artifact key is not allowlisted: {key}.", nameof(fields));
            }

            if (value is string text && text.Length > 256)
            {
                throw new ArgumentException($"Artifact value is too long: {key}.", nameof(fields));
            }

            if (value is not null && value is not string && value is not int && value is not long && value is not double && value is not bool)
            {
                throw new ArgumentException($"Artifact value is not scalar: {key}.", nameof(fields));
            }
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(fields, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}
