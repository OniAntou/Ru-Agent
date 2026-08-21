using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuApp.Services;

public sealed record HarnessSessionDescriptor(
    Uri BaseUri,
    string AuthToken,
    string Scenario,
    string EventsPath,
    string VoiceMode)
{
    private static readonly HashSet<string> AllowedScenarios = new(StringComparer.OrdinalIgnoreCase)
    {
        "turn",
        "stop",
        "cancel",
        "stale-session",
    };

    public static HarnessSessionDescriptor Create(
        string baseUrl,
        string authToken,
        string scenario,
        string eventsPath,
        string voiceMode = "deterministic")
    {
        var session = BackendSession.FromHttpUri(baseUrl, authToken);
        ValidateScenario(scenario);
        if (!string.Equals(voiceMode, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Harness voice mode must be deterministic.", nameof(voiceMode));
        }

        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            throw new ArgumentException("Harness event path is required.", nameof(eventsPath));
        }

        return new HarnessSessionDescriptor(
            session.HttpBaseUri,
            session.AuthSecret,
            scenario,
            Path.GetFullPath(eventsPath),
            "deterministic");
    }

    public static HarnessSessionDescriptor LoadFromArgument(string arguments)
    {
        var path = ParseDescriptorPath(arguments);
        var descriptorPath = Path.GetFullPath(path);
        try
        {
            var json = File.ReadAllText(descriptorPath);
            var payload = JsonSerializer.Deserialize<DescriptorPayload>(json)
                ?? throw new InvalidDataException("Harness descriptor is empty.");
            var descriptor = Create(
                payload.BaseUrl,
                payload.AuthToken,
                payload.Scenario,
                payload.EventsPath,
                payload.VoiceMode);
            EnsureUnderDescriptorRoot(descriptorPath, descriptor.EventsPath);
            return descriptor;
        }
        finally
        {
            TryDeleteDescriptor(descriptorPath);
        }
    }

    public void WriteTo(string descriptorPath)
    {
        var fullPath = Path.GetFullPath(descriptorPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var payload = new DescriptorPayload
        {
            BaseUrl = BaseUri.ToString(),
            AuthToken = AuthToken,
            Scenario = Scenario,
            EventsPath = EventsPath,
            VoiceMode = VoiceMode,
        };
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ParseDescriptorPath(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            throw new ArgumentException("--ru-harness-session <path> is required.", nameof(arguments));
        }

        const string flag = "--ru-harness-session";
        var trimmed = arguments.Trim();
        if (!trimmed.StartsWith(flag, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only --ru-harness-session <path> is accepted.", nameof(arguments));
        }

        var rest = trimmed[flag.Length..].TrimStart();
        if (rest.Length == 0)
        {
            throw new ArgumentException("Harness descriptor path is missing.", nameof(arguments));
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            if (end <= 1 || !string.IsNullOrWhiteSpace(rest[(end + 1)..]))
            {
                throw new ArgumentException("Harness descriptor path is invalid.", nameof(arguments));
            }

            return rest[1..end];
        }

        var separator = rest.IndexOfAny([' ', '\t']);
        return separator < 0 ? rest : throw new ArgumentException("Harness descriptor arguments are invalid.", nameof(arguments));
    }

    private static void ValidateScenario(string scenario)
    {
        if (!AllowedScenarios.Contains(scenario))
        {
            throw new ArgumentException("Harness scenario is not allowed.", nameof(scenario));
        }
    }

    private static void EnsureUnderDescriptorRoot(string descriptorPath, string eventsPath)
    {
        var root = Path.GetDirectoryName(descriptorPath)!;
        var relative = Path.GetRelativePath(root, eventsPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Harness event path must remain under the session root.", nameof(eventsPath));
        }
    }

    private static void TryDeleteDescriptor(string descriptorPath)
    {
        try
        {
            if (File.Exists(descriptorPath))
            {
                File.Delete(descriptorPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class DescriptorPayload
    {
        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("auth_token")]
        public string AuthToken { get; set; } = string.Empty;

        [JsonPropertyName("scenario")]
        public string Scenario { get; set; } = string.Empty;

        [JsonPropertyName("events_path")]
        public string EventsPath { get; set; } = string.Empty;

        [JsonPropertyName("voice_mode")]
        public string VoiceMode { get; set; } = string.Empty;
    }
}
