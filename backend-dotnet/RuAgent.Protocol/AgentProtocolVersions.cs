namespace RuAgent.Protocol;

public static class AgentProtocolVersions
{
    public const int BrokerV1 = 1;
    public const int BrokerV2 = 2;
    public const int WorkerV1 = 1;
    public const int WorkerV2 = 2;
    public const int HarnessEventsV1 = 1;

    public static bool IsBrokerSupported(int version) => version is BrokerV1 or BrokerV2;

    public static bool IsWorkerSupported(int version) => version is WorkerV1 or WorkerV2;
}
