namespace RuAgent.Hosting;

public sealed record WorkerBrokerStartupOptions(
    RuntimeMigrationMode Mode,
    string AgentWorkerToken,
    string DeviceWorkerToken,
    string ProviderBootstrapPath,
    int AgentWorkerPort,
    int DeviceWorkerPort,
    int? BrokerPort,
    string? BrokerExecutable)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        ValidateToken(AgentWorkerToken, nameof(AgentWorkerToken));
        ValidateToken(DeviceWorkerToken, nameof(DeviceWorkerToken));
        ValidatePort(AgentWorkerPort, nameof(AgentWorkerPort));
        ValidatePort(DeviceWorkerPort, nameof(DeviceWorkerPort));
        if (AgentWorkerPort == DeviceWorkerPort)
        {
            throw new ArgumentException("Agent and device worker ports must differ.");
        }

        if (BrokerPort is not null)
        {
            ValidatePort(BrokerPort.Value, nameof(BrokerPort));
        }

        if (Mode == RuntimeMigrationMode.Dotnet && string.IsNullOrWhiteSpace(ProviderBootstrapPath))
        {
            throw new ArgumentException("Dotnet mode requires a provider bootstrap path.", nameof(ProviderBootstrapPath));
        }

        if (!string.IsNullOrWhiteSpace(BrokerExecutable) && Path.GetFileName(BrokerExecutable).Length == 0)
        {
            throw new ArgumentException("Broker executable path is invalid.", nameof(BrokerExecutable));
        }
    }

    private static void ValidateToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(character => character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Worker token is invalid.", parameterName);
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
