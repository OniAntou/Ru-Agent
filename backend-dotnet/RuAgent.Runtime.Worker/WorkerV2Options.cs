namespace RuAgent.Runtime.Worker;

public sealed record WorkerV2Options
{
    public WorkerV2Options(
        int port,
        string token,
        Uri providerEndpoint,
        string providerModel,
        string providerCredential)
    {
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token.Any(character => character > 127 || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException("Worker token is invalid.", nameof(token));
        }

        ArgumentNullException.ThrowIfNull(providerEndpoint);
        if (providerEndpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(providerEndpoint.UserInfo))
        {
            throw new ArgumentException("Provider endpoint is invalid.", nameof(providerEndpoint));
        }

        if (string.IsNullOrWhiteSpace(providerModel) || providerModel.Length > 256)
        {
            throw new ArgumentException("Provider model is invalid.", nameof(providerModel));
        }

        if (providerCredential is null || providerCredential.Length > 4096 || providerCredential.Any(char.IsControl))
        {
            throw new ArgumentException("Provider credential is invalid.", nameof(providerCredential));
        }

        Port = port;
        Token = token;
        ProviderEndpoint = providerEndpoint;
        ProviderModel = providerModel;
        ProviderCredential = providerCredential;
    }

    public int Port { get; }

    public string Token { get; }

    public Uri ProviderEndpoint { get; }

    public string ProviderModel { get; }

    public string ProviderCredential { get; }
}
