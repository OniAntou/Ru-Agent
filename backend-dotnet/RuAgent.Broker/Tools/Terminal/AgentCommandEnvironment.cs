using System.Collections.Frozen;

namespace RuAgent.Broker.Tools.Terminal;

public sealed record EnvironmentDiagnostics(
    int AllowedCount,
    int StrippedCount,
    IReadOnlyList<string> StrippedNames);

/// <summary>
/// Builds the sanitized, non-secret command environment handed to every agent
/// terminal child process. Values are copied once from the parent snapshot
/// captured at construction and are never re-read after execution begins;
/// model input can neither add nor override entries. Diagnostics expose only
/// names and counts. Stripped credential values are retained internally
/// as redaction seeds for the streaming output redactor and never re-enter
/// any child environment or diagnostic payload.
/// </summary>
public sealed class AgentCommandEnvironment
{
    private static readonly FrozenSet<string> AllowedNames = new[]
    {
        "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME",
        "LOCALAPPDATA", "APPDATA", "PROGRAMDATA",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)",
        "PATH", "PATHEXT", "COMSPEC",
        // Preserve the trusted parent's module search path. Removing it can
        // stall Windows PowerShell's module discovery on hosted Windows images.
        "PSMODULEPATH",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS",
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
        "DOTNET_ROOT", "NUGET_PACKAGES",
        "PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV",
        "PIP_CACHE_DIR",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private const int MinimumRedactionSeedLength = 8;

    private static readonly (string Name, string Value)[] FixedChildValues =
    [
        ("GIT_TERMINAL_PROMPT", "0"),
        ("GCM_INTERACTIVE", "Never"),
        ("PYTHONUTF8", "1"),
        ("PYTHONIOENCODING", "utf-8"),
    ];

    private readonly Dictionary<string, string> _childEnvironment;
    private readonly List<string> _strippedNames;
    private readonly List<string> _redactionSeeds;

    public AgentCommandEnvironment(IReadOnlyDictionary<string, string>? parentSnapshot = null)
    {
        var snapshot = parentSnapshot ?? CaptureCurrentProcessSnapshot();
        _childEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _strippedNames = [];
        _redactionSeeds = [];

        foreach (var (name, value) in snapshot)
        {
            if (SecretEnvironmentNamePolicy.IsSecretName(name))
            {
                _strippedNames.Add(name);
                // Flag-like values ("1", "false") carry no secret and would
                // corrupt unrelated output if treated as redaction seeds.
                if (value is { Length: >= MinimumRedactionSeedLength })
                {
                    _redactionSeeds.Add(value);
                }

                continue;
            }

            if (!AllowedNames.Contains(name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                _childEnvironment[name] = value;
            }
        }

        foreach (var (name, value) in FixedChildValues)
        {
            _childEnvironment[name] = value;
        }
    }

    public IReadOnlyDictionary<string, string> ChildEnvironment => _childEnvironment;

    public IReadOnlyList<string> StrippedNames => _strippedNames;

    /// <summary>Stripped credential values used to seed the streaming redactor.</summary>
    public IReadOnlyList<string> GetRedactionSeeds() => _redactionSeeds.ToArray();

    public EnvironmentDiagnostics Describe() =>
        new(_childEnvironment.Count, _strippedNames.Count, _strippedNames.ToArray());

    private static IReadOnlyDictionary<string, string> CaptureCurrentProcessSnapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }
}
