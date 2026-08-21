namespace RuBackend.Diagnostics;

public static class DiagnosticCodes
{
    public const string AppStart = "app.start";
    public const string AppStop = "app.stop";
    public const string WorkerConnected = "worker.connected";
    public const string WorkerDisconnected = "worker.disconnected";
    public const string WorkerTimeout = "worker.timeout";
    public const string WorkerUnavailable = "worker.unavailable";

    public const string TurnStart = "turn.start";
    public const string TurnDone = "turn.done";
    public const string TurnFailed = "turn.failed";
    public const string TurnCancelled = "turn.cancelled";

    public const string LlmRequest = "llm.request";
    public const string LlmResponse = "llm.response";

    public const string SpeechSynthesizeStart = "speech.synthesize.start";
    public const string SpeechSynthesizeDone = "speech.synthesize.done";
    public const string SpeechSynthesizeFailed = "speech.synthesize.failed";

    public const string SttStart = "stt.start";
    public const string SttDone = "stt.done";
    public const string SttFailed = "stt.failed";

    public const string MemorySearch = "memory.search";
    public const string MemorySave = "memory.save";
    public const string MemoryFailed = "memory.failed";

    public const string VtsAction = "vts.action";
    public const string VtsCatalog = "vts.catalog";
    public const string VtsFailed = "vts.failed";

    public const string ObservationTrigger = "observation.trigger";
    public const string ObservationFailed = "observation.failed";

    public const string SettingsRead = "settings.read";
    public const string SettingsSaved = "settings.saved";

    public const string GenericError = "error";
    public const string Unclassified = "diagnostic_unclassified";
}
