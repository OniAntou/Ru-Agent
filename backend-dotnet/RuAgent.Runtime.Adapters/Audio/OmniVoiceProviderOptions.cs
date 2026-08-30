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

    /// <summary>
    /// Maximum time allowed for a process that was just started to expose a
    /// healthy model. This is separate from synthesis so a stale or broken
    /// provider cannot consume the full TTS budget on every request.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Per-request budget for the lightweight provider status probe.</summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;

    public void Validate()
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        }

        if (ReadinessTimeout <= TimeSpan.Zero || ReadinessTimeout > TimeSpan.FromSeconds(15))
        {
            throw new ArgumentOutOfRangeException(nameof(ReadinessTimeout));
        }

        if (MaxResponseBytes <= 0 || MaxResponseBytes > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        }
    }
}
