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
    public const string ModeClamped = "mode_clamped";

    public const string LlmRequest = "llm.request";
    public const string LlmResponse = "llm.response";

    public const string MemorySearch = "memory.search";
    public const string MemorySave = "memory.save";
    public const string MemoryFailed = "memory.failed";

    public const string SettingsRead = "settings.read";
    public const string SettingsSaved = "settings.saved";

    public const string ToolRequested = "tool.requested";
    public const string ToolPolicyDenied = "tool.policy.denied";
    public const string ToolApprovalRequired = "tool.approval.required";
    public const string ToolAdapterStarted = "tool.adapter.started";
    public const string ToolAdapterCompleted = "tool.adapter.completed";
    public const string ToolVerificationSucceeded = "tool.verification.succeeded";
    public const string ToolVerificationFailed = "tool.verification.failed";
    public const string ToolReceiptDelivered = "tool.receipt.delivered";
    public const string ToolReceiptRejected = "tool.receipt.rejected";

    // Harness stable codes lived here for HarnessDiagnosticEventFactory, which was the only
    // reader; harness events are emitted as structured log records now, so the factory and
    // its private vocabulary went together. DiagnosticEventPolicy still sanitizes every
    // record on the way out, so a code that is not in its patterns is still unclassified.
    public const string GenericError = "error";
    public const string Unclassified = "diagnostic_unclassified";
}
