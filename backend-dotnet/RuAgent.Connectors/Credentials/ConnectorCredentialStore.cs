namespace RuAgent.Connectors.Credentials;

public interface IConnectorCredentialStore
{
    string? ReadSecret(string connectorId);
}

public sealed class ConnectorCredentialStore : IConnectorCredentialStore
{
    private readonly IReadOnlyDictionary<string, string> _secrets;

    public ConnectorCredentialStore(IReadOnlyDictionary<string, string> secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public string? ReadSecret(string connectorId) =>
        _secrets.TryGetValue(connectorId, out var secret) ? secret : null;
}
