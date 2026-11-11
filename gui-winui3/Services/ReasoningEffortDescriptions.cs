namespace RuApp.Services;

/// <summary>
/// One plain sentence per reasoning effort the panel can show. The wording says what the
/// setting asks of the model, never how a particular provider behaves, because the
/// catalog publishes only the level names.
/// </summary>
public static class ReasoningEffortDescriptions
{
    public const string NotAdjustable = "This model has no adjustable reasoning effort.";

    public static string For(string? effort) => (effort ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "default" => "Uses the model's own default effort.",
        "none" or "off" or "minimal" => "Little or no extra thinking before answering.",
        "low" => "Thinks briefly before answering.",
        "medium" => "A middle setting between speed and thoroughness.",
        "high" => "Thinks longer before answering.",
        "xhigh" or "max" => "The most thinking the model offers.",
        "on" or "enabled" => "Turns the model's extended thinking on.",
        var other => $"Provider setting: {other}.",
    };
}