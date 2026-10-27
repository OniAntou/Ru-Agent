namespace RuAgent.Protocol;

/// <summary>The <c>computer</c> tool's actions (ADR 0031).</summary>
public static class ComputerActions
{
    public const string List = "list";
    public const string RequestAccess = "request_access";
    public const string Screenshot = "screenshot";
    public const string Click = "click";
    public const string Move = "move";
    public const string Drag = "drag";
    public const string Scroll = "scroll";
    public const string Type = "type";
    public const string Key = "key";

    public static IReadOnlyList<string> All { get; } =
        [List, RequestAccess, Screenshot, Click, Move, Drag, Scroll, Type, Key];

    /// <summary>Actions that only observe; they never send input.</summary>
    public static bool IsObservation(string? action) => action is List or Screenshot;

    /// <summary>Actions whose x and y name a point in a monitor's latest screenshot.</summary>
    public static bool IsPointer(string? action) => action is Click or Move or Drag or Scroll;
}

/// <summary>Bounds shared by the tool schema and the Broker adapter.</summary>
public static class ComputerLimits
{
    public const int MaxMonitorIndex = 15;
    public const int MaxScreenshotEdge = 1280;
    public const int MaxScrollAmount = 10;
    public const int MaxTypeChars = 2000;
    public const int MaxKeyChordChars = 64;
    public const int MaxRequestedApps = 8;
    public const int MaxAppNameChars = 64;
}

/// <summary>
/// Applications computer use can never request or target in any preset (ADR 0031):
/// Ru itself, the terminal hosts its TUI runs in, and credential or elevation UI.
/// Input reaching them would let the model answer its own approval prompts.
/// </summary>
public static class ComputerProtectedApps
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "ruapp", "rubackend", "ruagent.cli", "ru",
        "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh",
        "consent", "credentialuibroker", "logonui", "lockapp",
        // A UWP frame whose app could not be resolved; granting it would grant every UWP app.
        "applicationframehost",
    };

    /// <summary>Normalizes an executable name or path to its lowercase file name with <c>.exe</c>.</summary>
    public static string? Normalize(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        var name = app.Trim();
        var separator = name.LastIndexOfAny(['\\', '/']);
        if (separator >= 0) name = name[(separator + 1)..];
        if (name.Length == 0 || name.Length > ComputerLimits.MaxAppNameChars) return null;
        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ' ')) return null;
        }

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    public static bool IsProtected(string? app)
    {
        var normalized = Normalize(app);
        return normalized is not null && Names.Contains(normalized[..^4]);
    }
}
