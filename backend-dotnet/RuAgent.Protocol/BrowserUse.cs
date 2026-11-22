namespace RuAgent.Protocol;

/// <summary>The <c>browser</c> tool's actions (ADR 0055).</summary>
public static class BrowserActions
{
    public const string Open = "open";
    public const string Navigate = "navigate";
    public const string Snapshot = "snapshot";
    public const string Click = "click";
    public const string Type = "type";
    public const string Fill = "fill";
    public const string Press = "press";
    public const string Eval = "eval";
    public const string Wait = "wait";
    public const string Console = "console";
    public const string Network = "network";
    public const string Close = "close";

    /// <summary>One call that runs several page actions in order (ADR 0055 amendment 2026-11-23).</summary>
    public const string Batch = "batch";

    public static IReadOnlyList<string> All { get; } =
        [Open, Navigate, Snapshot, Click, Type, Fill, Press, Eval, Wait, Console, Network, Close, Batch];

    /// <summary>Actions a <c>batch</c> step may name; open/close stay separate tool calls.</summary>
    public static IReadOnlyList<string> Batchable { get; } =
        [Navigate, Snapshot, Click, Type, Fill, Press, Eval, Wait, Console, Network];

    /// <summary>Actions that only read the page; they never change it.</summary>
    public static bool IsObservation(string? action) => action is Snapshot or Console or Network;

    /// <summary>Actions whose url argument is required and must be http or https.</summary>
    public static bool NamesUrl(string? action) => action is Open or Navigate;
}

/// <summary>Bounds shared by the tool schema and the Broker adapter.</summary>
public static class BrowserLimits
{
    public const int MaxUrlChars = 2048;
    public const int MaxRefChars = 16;
    public const int MaxTextChars = 4000;
    public const int MaxKeyChars = 32;
    public const int MaxExpressionChars = 8000;
    public const int MinWaitMilliseconds = 100;
    public const int MaxWaitMilliseconds = 10_000;
    public const int MaxSnapshotElements = 150;
    public const int MaxSnapshotTextChars = 20_000;
    public const int MaxConsoleMessages = 50;
    public const int MaxNetworkRequests = 50;
    public const int MaxBatchSteps = 10;
    public const int MaxPageWidth = 1280;
    public const int MaxPageHeight = 800;
}
