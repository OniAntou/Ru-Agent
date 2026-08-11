namespace RuBackend.Business.Settings;

public interface ICredentialStore
{
    Task<string?> GetAsync(
        string serviceName,
        string accountName,
        CancellationToken cancellationToken);

    Task SetAsync(
        string serviceName,
        string accountName,
        string secret,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string serviceName,
        string accountName,
        CancellationToken cancellationToken);
}

public sealed class CredentialStoreException : Exception
{
    public CredentialStoreException(string message)
        : base(message)
    {
    }
}
