namespace RuBackend.Worker;

public static class WorkerMessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
    public const string RuntimeLoad = "runtime.load";
    public const string RuntimeState = "runtime.state";
    public const string RuntimeRequest = "runtime.request";
    public const string RuntimeResponse = "runtime.response";
    public const string VoiceStart = "voice.start";
    public const string VoiceControl = "voice.control";
    public const string VoiceDone = "voice.done";
    public const string WorkerHealth = "worker.health";
    public const string WorkerHealthOk = "worker.health.ok";
    public const string Error = "error";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            Hello,
            HelloOk,
            RuntimeLoad,
            RuntimeState,
            RuntimeRequest,
            RuntimeResponse,
            VoiceStart,
            VoiceControl,
            VoiceDone,
            WorkerHealth,
            WorkerHealthOk,
            Error,
        ],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> OrderedResponses { get; } = new HashSet<string>(
        [
            HelloOk,
            RuntimeState,
            RuntimeResponse,
            VoiceControl,
            VoiceDone,
            WorkerHealthOk,
        ],
        StringComparer.Ordinal);
}
