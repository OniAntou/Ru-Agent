namespace RuBackend.Worker;

public static class WorkerMessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
    public const string ChatStart = "chat.start";
    public const string ChatReset = "chat.reset";
    public const string ChatChunk = "chat.chunk";
    public const string ChatDone = "chat.done";
    public const string ChatCancel = "chat.cancel";
    public const string MemoryExtract = "memory.extract";
    public const string MemoryFacts = "memory.facts";
    public const string RuntimeLoad = "runtime.load";
    public const string RuntimeState = "runtime.state";
    public const string RuntimeRequest = "runtime.request";
    public const string RuntimeResponse = "runtime.response";
    public const string VoiceStart = "voice.start";
    public const string VoiceControl = "voice.control";
    public const string VoiceDone = "voice.done";
    public const string VtsCatalog = "vts.catalog";
    public const string VtsAction = "vts.action";
    public const string WorkerHealth = "worker.health";
    public const string WorkerHealthOk = "worker.health.ok";
    public const string Error = "error";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            Hello,
            HelloOk,
            ChatStart,
            ChatReset,
            ChatChunk,
            ChatDone,
            ChatCancel,
            MemoryExtract,
            MemoryFacts,
            RuntimeLoad,
            RuntimeState,
            RuntimeRequest,
            RuntimeResponse,
            VoiceStart,
            VoiceControl,
            VoiceDone,
            VtsCatalog,
            VtsAction,
            WorkerHealth,
            WorkerHealthOk,
            Error,
        ],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> OrderedResponses { get; } = new HashSet<string>(
        [
            HelloOk,
            ChatReset,
            ChatChunk,
            ChatDone,
            MemoryFacts,
            RuntimeState,
            RuntimeResponse,
            VoiceControl,
            VoiceDone,
            WorkerHealthOk,
        ],
        StringComparer.Ordinal);
}
