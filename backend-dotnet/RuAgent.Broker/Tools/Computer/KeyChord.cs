namespace RuAgent.Broker.Tools.Computer;

/// <summary>A parsed key combination such as <c>ctrl+shift+s</c>: modifiers in order, then one key.</summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key)
{
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort Windows = 0x5B;

    private static readonly Dictionary<string, ushort> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = Control, ["control"] = Control,
        ["shift"] = Shift,
        ["alt"] = Alt, ["option"] = Alt,
        ["win"] = Windows, ["windows"] = Windows, ["super"] = Windows, ["meta"] = Windows, ["cmd"] = Windows,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["menu"] = 0x5D, ["apps"] = 0x5D,
        ["minus"] = 0xBD, ["plus"] = 0xBB, ["equals"] = 0xBB, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["semicolon"] = 0xBA, ["quote"] = 0xDE, ["backquote"] = 0xC0,
        ["bracketleft"] = 0xDB, ["bracketright"] = 0xDD, ["backslash"] = 0xDC,
    };

    public bool UsesWindowsKey => Key == Windows || Modifiers.Contains(Windows);

    public static KeyChord Parse(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys) || keys.Length > RuAgent.Protocol.ComputerLimits.MaxKeyChordChars)
            throw Invalid();
        var parts = keys.Trim().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 5 || parts.Any(part => part.Length == 0)) throw Invalid();
        var modifiers = new List<ushort>();
        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (!ModifierKeys.TryGetValue(parts[index], out var modifier) || modifiers.Contains(modifier)) throw Invalid();
            modifiers.Add(modifier);
        }

        var last = parts[^1];
        if (ModifierKeys.TryGetValue(last, out var lone) && parts.Length == 1) return new KeyChord([], lone);
        return new KeyChord(modifiers, KeyCode(last) ?? throw Invalid());
    }

    private static ushort? KeyCode(string name)
    {
        if (NamedKeys.TryGetValue(name, out var named)) return named;
        if (name.Length == 1)
        {
            var character = char.ToUpperInvariant(name[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9') return character;
        }

        if (name.Length is 2 or 3 && (name[0] is 'f' or 'F')
            && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        return null;
    }

    private static ComputerUseException Invalid() => new("computer_key_invalid",
        "keys must be a combination such as ctrl+s, alt+tab, shift+f10 or enter.");
}
