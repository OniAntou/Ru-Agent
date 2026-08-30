using System.Text;

namespace RuAgent.Runtime.Core.Contracts;

public sealed record ObservationRequest
{
    public ObservationRequest(ReadOnlyMemory<byte> jpegBytes, string prompt, string modelName)
    {
        if (jpegBytes.IsEmpty || jpegBytes.Length > RuntimeLimits.MaxObservationImageBytes)
        {
            throw new ArgumentException("Observation image is invalid.", nameof(jpegBytes));
        }

        if (string.IsNullOrWhiteSpace(prompt)
            || Encoding.UTF8.GetByteCount(prompt) > RuntimeLimits.MaxObservationPromptBytes)
        {
            throw new ArgumentException("Observation prompt is invalid.", nameof(prompt));
        }

        if (string.IsNullOrWhiteSpace(modelName) || modelName.Length > RuntimeLimits.MaxModelNameChars)
        {
            throw new ArgumentException("Observation model is invalid.", nameof(modelName));
        }

        JpegBytes = jpegBytes;
        Prompt = prompt;
        ModelName = modelName;
    }

    public ReadOnlyMemory<byte> JpegBytes { get; }
    public string Prompt { get; }
    public string ModelName { get; }
}

public sealed record VisionObservation
{
    public VisionObservation(
        string scene,
        string @event,
        double confidence,
        double novelty,
        double commentWorthiness,
        IEnumerable<string> visibleText,
        string? reaction)
    {
        Scene = LimitText(scene, 200, nameof(scene));
        Event = LimitText(@event, 1_500, nameof(@event));
        Confidence = Clamp(confidence);
        Novelty = Clamp(novelty);
        CommentWorthiness = Clamp(commentWorthiness);
        VisibleText = visibleText
            .Take(RuntimeLimits.MaxVisibleTextItems)
            .Select(value => LimitText(value, 150, nameof(visibleText)))
            .ToArray();
        Reaction = reaction is null ? null : LimitText(reaction, 500, nameof(reaction));
    }

    public string Scene { get; }
    public string Event { get; }
    public double Confidence { get; }
    public double Novelty { get; }
    public double CommentWorthiness { get; }
    public IReadOnlyList<string> VisibleText { get; }
    public string? Reaction { get; }

    private static string LimitText(string value, int max, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        return value.Length <= max ? value : value[..max];
    }

    private static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
}

public sealed record RuntimeSettingsSnapshot(
    string Backend,
    string ModelName,
    Uri? BaseUrl,
    string ApiVariant,
    bool KiloReasoningEnabled)
{
    public VoiceSettingsSnapshot Voice { get; init; } = new();
}
