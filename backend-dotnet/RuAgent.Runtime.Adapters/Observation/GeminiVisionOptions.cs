namespace RuAgent.Runtime.Adapters.Observation;

public sealed record GeminiVisionOptions
{
    public GeminiVisionOptions(string apiKey)
    {
        ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public const string DefaultModel = "gemini-3.1-flash-lite";

    public static IReadOnlyList<string> DefaultModels { get; } =
        ["gemini-3.1-flash-lite", "gemini-3.5-flash", "gemini-3.7-flash", "gemini-3-flash-preview"];

    public string ApiKey { get; }

    public IReadOnlyList<string> Models { get; init; } = DefaultModels;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(12);

    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public void Validate()
    {
        if (ApiKey.Length > 512 || ApiKey.Any(char.IsControl))
        {
            throw new ArgumentException("Gemini API key is invalid.", nameof(ApiKey));
        }

        if (Models is null || Models.Count is < 1 or > 8 || Models.Any(model => !IsSafeModel(model)))
        {
            throw new ArgumentException("Gemini model list is invalid.", nameof(Models));
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }

        if (MaxResponseBytes is < 256 or > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        }
    }

    private static bool IsSafeModel(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && model.Length <= 128
        && !model.Any(char.IsControl)
        && !model.Contains('/', StringComparison.Ordinal)
        && !model.Contains('?', StringComparison.Ordinal)
        && !model.Contains('#', StringComparison.Ordinal);
}
