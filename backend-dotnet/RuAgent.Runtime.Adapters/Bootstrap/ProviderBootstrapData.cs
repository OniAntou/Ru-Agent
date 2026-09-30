using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Bootstrap;

public sealed record ProviderBootstrapData
{
    public ProviderBootstrapData(
        string backend,
        string protocol,
        Uri endpoint,
        string model,
        string credential,
        JsonObject? requestOptions = null,
        int? maximumOutputTokens = null)
    {
        Backend = Require(backend, 128, nameof(backend));
        Protocol = ProviderProtocols.Require(protocol);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.ToString().Length > 2048)
        {
            throw new ArgumentException("Provider endpoint is invalid.", nameof(endpoint));
        }

        Model = Require(model, 256, nameof(model));
        Credential = Require(credential, 4096, nameof(credential), allowEmpty: true);
        Endpoint = endpoint;
        RequestOptions = ProviderRequestOptionsValidator.ValidateAndClone(protocol, requestOptions, nameof(requestOptions));
        MaximumOutputTokens = ValidateOutputCap(protocol, maximumOutputTokens);
    }

    public string Backend { get; }

    public string Protocol { get; }

    public Uri Endpoint { get; }

    public string Model { get; }

    public string Credential { get; }

    public JsonObject? RequestOptions { get; }

    public int? MaximumOutputTokens { get; }

    private static int? ValidateOutputCap(string protocol, int? value)
    {
        if (value is null)
        {
            if (protocol == ProviderProtocols.AnthropicMessages)
                throw new ArgumentException("Anthropic requires an output cap.", nameof(value));
            return null;
        }
        if (value is < 1 or > 128000) throw new ArgumentException("Output cap is invalid.", nameof(value));
        if (protocol != ProviderProtocols.AnthropicMessages) throw new ArgumentException("Only Anthropic uses an output cap.", nameof(value));
        if (value > 8192) throw new ArgumentException("Output cap exceeds 8192.", nameof(value));
        return value;
    }

    private static string Require(string? value, int maximum, string parameterName, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || value.Contains('\0')
            || value.Any(char.IsControl) || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException("Provider bootstrap value is invalid.", parameterName);
        }

        return value;
    }
}
