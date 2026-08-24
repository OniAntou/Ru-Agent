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

    public const string ToolRequested = "tool.requested";
    public const string ToolPolicyDenied = "tool.policy.denied";
    public const string ToolApprovalRequired = "tool.approval.required";
    public const string ToolAdapterStarted = "tool.adapter.started";
    public const string ToolAdapterCompleted = "tool.adapter.completed";
    public const string ToolVerificationSucceeded = "tool.verification.succeeded";
    public const string ToolVerificationFailed = "tool.verification.failed";
    public const string ToolReceiptDelivered = "tool.receipt.delivered";
    public const string ToolReceiptRejected = "tool.receipt.rejected";

    // Harness stable codes
    public const string HarnessRunCreated = "harness.run.created";
    public const string HarnessRunStateChanged = "harness.run.state_changed";
    public const string HarnessRunFailed = "harness.run.failed";
    public const string HarnessModelStepStarted = "harness.model.step_started";
    public const string HarnessModelStepCompleted = "harness.model.step_completed";
    public const string HarnessToolProposed = "harness.tool.proposed";
    public const string HarnessToolExecutionRequested = "harness.tool.execution_requested";
    public const string HarnessToolExecutionCompleted = "harness.tool.execution_completed";
    public const string HarnessApprovalRequested = "harness.approval.requested";
    public const string HarnessContinueRequested = "harness.continue.requested";
    public const string HarnessReconciliationRequired = "harness.reconciliation.required";
    public const string HarnessTransientFramesDropped = "harness.transient.frames_dropped";
    public const string HarnessTrustDecided = "harness.trust.decided";
    public const string HarnessWorkspaceBusy = "harness.workspace.busy";
    public const string HarnessIdempotencyConflict = "harness.idempotency.conflict";
    public const string HarnessPresetMismatch = "harness.preset.mismatch";
    public const string HarnessTrustStale = "harness.trust.stale";
    public const string HarnessPhaseNotAvailable = "harness.phase.not_available";
    public const string HarnessCursorMismatch = "harness.cursor.mismatch";

    public const string GenericError = "error";
    public const string Unclassified = "diagnostic_unclassified";
}
