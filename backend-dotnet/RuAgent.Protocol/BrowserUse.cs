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

    // ADR 0055 amendment 2026-11-25: seeing, moving through and answering the page.
    public const string Screenshot = "screenshot";
    public const string Scroll = "scroll";
    public const string Hover = "hover";
    public const string Select = "select";
    public const string Back = "back";
    public const string Forward = "forward";
    public const string Reload = "reload";
    public const string Find = "find";
    public const string Dialog = "dialog";

    // ADR 0055 amendment 2026-11-25: session tabs, popups and file inputs.
    public const string Tabs = "tabs";
    public const string NewTab = "new_tab";
    public const string SwitchTab = "switch_tab";
    public const string CloseTab = "close_tab";
    public const string Upload = "upload";

    public static IReadOnlyList<string> All { get; } =
    [
        Open, Navigate, Snapshot, Click, Type, Fill, Press, Eval, Wait, Console, Network, Close, Batch,
        Screenshot, Scroll, Hover, Select, Back, Forward, Reload, Find, Dialog,
        Tabs, NewTab, SwitchTab, CloseTab, Upload,
    ];

    /// <summary>
    /// Actions a <c>batch</c> step may name. open/close carry the session's approval and lifetime,
    /// screenshot carries an image, tab actions change which page later steps reach, and upload
    /// carries its own per-file approval, so they stay separate tool calls.
    /// </summary>
    public static IReadOnlyList<string> Batchable { get; } =
        [Navigate, Snapshot, Click, Type, Fill, Press, Eval, Wait, Console, Network, Scroll, Hover, Select, Back, Forward, Reload, Find, Dialog];

    /// <summary>Actions that only read the page or the session's tabs; they never change them.</summary>
    public static bool IsObservation(string? action) => action is Snapshot or Console or Network or Screenshot or Find or Tabs;

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
    public const int MaxScrollAmount = 20;
    public const int ScrollStepPixels = 100;
    public const int MaxFindQueryChars = 200;
    public const int MaxFindResults = 30;
    public const int MaxSelectValueChars = 500;
    public const int MaxDialogTextChars = 1000;
    public const int MaxDialogMessageChars = 500;
    public const int MaxTabs = 8;
    public const int MaxUploadFiles = 10;
    public const long MaxUploadFileBytes = 100L * 1024 * 1024;
    public const int MaxPathChars = 1024;
}
