using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

/// <summary>Workflow metadata validation, separate from execution permission.</summary>
public static class TerminalWorkflowIntent
{
    public const int MaximumSupersededChecks = 16;
    public const int MaximumSupersededCallIdChars = 256;

    public static bool HasValidSupersedes(JsonObject arguments)
    {
        if (!arguments.TryGetPropertyValue("supersedes", out var value)) return true;
        if (arguments["purpose"] is not JsonValue purpose || !purpose.TryGetValue<string>(out var text)
            || text != "verification" || value is not JsonArray ids || ids.Count is < 1 or > MaximumSupersededChecks)
            return false;
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        return ids.All(id => id is JsonValue item && item.TryGetValue<string>(out var name)
            && !string.IsNullOrWhiteSpace(name) && name.Length <= MaximumSupersededCallIdChars
            && !name.Any(char.IsControl) && distinct.Add(name));
    }

    public static bool HasValidExpectedExitCode(JsonObject arguments)
    {
        var expectedFailure = arguments["purpose"] is JsonValue purpose
            && purpose.TryGetValue<string>(out var text) && text == "expected_failure";
        if (!arguments.TryGetPropertyValue("expected_exit_code", out var value)) return !expectedFailure;
        return value is JsonValue number && number.TryGetValue<int>(out var code) && code != 0;
    }
}
