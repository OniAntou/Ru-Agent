namespace RuAgent.Harness.Core;

/// <summary>The caller's immutable provider/model choice before the full provider snapshot is resolved.</summary>
public sealed record RunProviderIntent
{
    public RunProviderIntent(string backend, string model)
    {
        Backend = RequireValue(backend, 128, nameof(backend));
        Model = RequireValue(model, 256, nameof(model));
    }

    public string Backend { get; }
    public string Model { get; }

    private static string RequireValue(string value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)
            || value != value.Trim()) throw new ArgumentException("Invalid provider intent.", name);
        return value;
    }
}
