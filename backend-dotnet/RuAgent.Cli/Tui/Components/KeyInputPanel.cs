namespace RuAgent.Cli.Tui;

/// <summary>
/// The secret-input surface as card content: the title names the provider, the body shows
/// one bullet per entered character (count only, never content) with a caret where the next
/// character lands. The window composes both into the shared card frame, so the masked field
/// reads exactly like the pickers' search row.
/// </summary>
public static class KeyInputPanel
{
    public const char MaskGlyph = '•';

    /// <summary>The caret that marks where the next character lands.</summary>
    public const char Caret = '▌';

    /// <summary>Card heading for the ask: which provider the key belongs to.</summary>
    public static string CardTitle(string providerDisplay) =>
        "api key · " + (string.IsNullOrWhiteSpace(providerDisplay) ? "provider" : providerDisplay.Trim());

    /// <summary>The card body: the masked field with its caret.</summary>
    public static IReadOnlyList<CardRow> CardBody(int bulletCount) =>
    [
        new(CardRowKind.Field, MaskText(bulletCount)),
    ];

    /// <summary>Visible mask text: one bullet per character and the caret after the last one.</summary>
    public static string MaskText(int bulletCount) =>
        new string(MaskGlyph, Math.Max(0, bulletCount)) + Caret;
}
