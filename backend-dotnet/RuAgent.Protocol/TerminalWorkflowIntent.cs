using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

/// <summary>Workflow metadata validation, separate from execution permission.</summary>
public static class TerminalWorkflowIntent
{
    public static bool HasValidExpectedExitCode(JsonObject arguments)
    {
        var expectedFailure = arguments["purpose"] is JsonValue purpose
            && purpose.TryGetValue<string>(out var text) && text == "expected_failure";
        if (!arguments.TryGetPropertyValue("expected_exit_code", out var value)) return !expectedFailure;
        return value is JsonValue number && number.TryGetValue<int>(out var code) && code != 0;
    }
}
