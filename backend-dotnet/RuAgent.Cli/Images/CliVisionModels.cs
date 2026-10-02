namespace RuAgent.Cli.Images;

/// <summary>
/// Best-effort vision-name check ported from the provider adapters
/// (<c>ModelImage.IsVisionModel</c>, name fallback only). The CLI has no catalog capability
/// metadata, so this only produces a warning, never a gate: the backend stays the source of
/// truth and fails closed with <c>vision_unsupported</c>.
/// </summary>
public static class CliVisionModels
{
    public static bool IsVisionModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var m = model.Trim().ToLowerInvariant();
        return m.Contains("gpt-4o")
            || m.Contains("gpt-5")
            || m.Contains("claude-3")
            || m.Contains("claude-4")
            || m.Contains("claude-5")
            || m.Contains("claude-sonnet")
            || m.Contains("claude-opus")
            || m.Contains("gemini")
            || m.Contains("vision")
            || m.Contains("o1")
            || m.Contains("llama-3.2-vision");
    }
}
