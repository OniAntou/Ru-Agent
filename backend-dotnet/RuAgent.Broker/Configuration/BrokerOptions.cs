namespace RuAgent.Broker.Configuration;

public sealed record BrokerOptions(
    string DescriptorPath,
    int MaxFrameBytes = 64 * 1024,
    int DefaultTimeoutMs = 120_000,
    int HardTimeoutMs = 300_000,
    WebOptions? Web = null);
