namespace RuAgent.Protocol.Harness;

public static class WorkerV1Contracts
{
    public const int ProtocolVersion = 1;
    public const int MaxRequestIdChars = 128;
    public const int MaxCorrelationIdChars = 64;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const long MaxSequence = long.MaxValue;

    public const string Hello = "hello";
    public const string HelloOk = "hello.ok";
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

    public static IReadOnlySet<string> KnownTypes { get; } = new HashSet<string>(
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
            VtsCatalog,
            VtsAction,
            WorkerHealth,
            WorkerHealthOk,
            Error,
        ],
        StringComparer.Ordinal);

    public static IReadOnlySet<string> OrderedResponseTypes { get; } = new HashSet<string>(
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
