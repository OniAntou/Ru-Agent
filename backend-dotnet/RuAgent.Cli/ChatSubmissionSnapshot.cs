using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;

namespace RuAgent.Cli;

internal sealed record ChatSubmissionSnapshot(
    string Prompt,
    string SessionId,
    string WorkspacePath,
    string Preset,
    string Backend,
    string Model,
    string IdempotencyKey,
    ImmutableArray<CliImageAttachment> Images,
    ImmutableArray<CliTextAttachment> Texts,
    long OwnedBytes)
{
    public static bool IsPausedControlCommand(string line)
    {
        var command = line.TrimStart().Split(' ', 2)[0];
        return command.Equals("/retry", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/follow", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/backend", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/model", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/key", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/reasoning", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/help", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/status", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/exit", StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesContext(string sessionId, string workspace, string preset, string backend, string model) =>
        string.Equals(SessionId, sessionId, StringComparison.Ordinal)
        && string.Equals(WorkspacePath, workspace, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Preset, preset, StringComparison.Ordinal)
        && string.Equals(Backend, backend, StringComparison.Ordinal)
        && string.Equals(Model, model, StringComparison.Ordinal);

    public bool MatchesSelection(JsonElement settings)
    {
        var currentBackend = ReadString(settings, "backend");
        var modelProperty = currentBackend switch
        {
            "OpenAI" => "openai_model",
            "Anthropic" => "anthropic_model",
            _ => "model",
        };
        return string.Equals(currentBackend, Backend, StringComparison.Ordinal)
            && string.Equals(ReadString(settings, modelProperty), Model, StringComparison.Ordinal);
    }

    public static ChatSubmissionSnapshot Create(
        string prompt,
        string sessionId,
        string workspacePath,
        string preset,
        string backend,
        string model,
        IEnumerable<CliImageAttachment> images,
        IEnumerable<CliTextAttachment> texts)
    {
        var imageSnapshot = images.ToImmutableArray();
        var textSnapshot = texts.ToImmutableArray();
        long ownedBytes = checked(Utf16Bytes(prompt)
            + Utf16Bytes(sessionId)
            + Utf16Bytes(workspacePath)
            + Utf16Bytes(preset)
            + Utf16Bytes(backend)
            + Utf16Bytes(model));
        foreach (var image in imageSnapshot)
        {
            var dataUrlChars = checked("data:".Length + image.MimeType.Length + ";base64,".Length + image.Base64Data.Length);
            ownedBytes = checked(ownedBytes + (long)dataUrlChars * sizeof(char) + Utf16Bytes(image.FileName));
        }

        foreach (var text in textSnapshot)
        {
            ownedBytes = checked(ownedBytes + Math.Max(text.ByteSize, Utf16Bytes(text.TextContent)) + Utf16Bytes(text.FileName));
        }
        return new ChatSubmissionSnapshot(
            prompt,
            sessionId,
            workspacePath,
            preset,
            backend,
            model,
            Guid.NewGuid().ToString("N"),
            imageSnapshot,
            textSnapshot,
            ownedBytes);
    }

    private static int Utf16Bytes(string value) => Encoding.Unicode.GetByteCount(value);

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}
