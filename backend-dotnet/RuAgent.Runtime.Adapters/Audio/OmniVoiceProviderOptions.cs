namespace RuAgent.Runtime.Adapters.Audio;

public sealed record OmniVoiceProviderOptions
{
    public OmniVoiceProviderOptions(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri
            || !baseUri.IsLoopback
            || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("OmniVoice URI must be loopback HTTP(S).", nameof(baseUri));
        }

        BaseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public Uri BaseUri { get; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaxResponseBytes <= 0 || MaxResponseBytes > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        }
    }
}
