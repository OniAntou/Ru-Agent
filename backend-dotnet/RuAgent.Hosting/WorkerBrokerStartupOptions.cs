namespace RuAgent.Hosting;

public sealed record WorkerBrokerStartupOptions(
    int? BrokerPort,
    string? BrokerExecutable)
{
    public void Validate()
    {
        if (BrokerPort is not null)
        {
            ValidatePort(BrokerPort.Value, nameof(BrokerPort));
        }

        if (!string.IsNullOrWhiteSpace(BrokerExecutable) && Path.GetFileName(BrokerExecutable).Length == 0)
        {
            throw new ArgumentException("Broker executable path is invalid.", nameof(BrokerExecutable));
        }
    }

    private static void ValidatePort(int value, string parameterName)
    {
        if (value is < 1024 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
