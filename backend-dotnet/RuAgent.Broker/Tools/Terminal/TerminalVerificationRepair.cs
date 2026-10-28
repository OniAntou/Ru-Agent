using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>Completion evidence only; never changes authorization, argv or the observed exit.</summary>
internal static class TerminalVerificationRepair
{
    internal static void Annotate(JsonObject output, CanonicalCommandIntent intent)
    {
        var executable = Path.GetFileNameWithoutExtension(intent.ResolvedExecutable);
        var python = executable.Equals("python", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("python3", StringComparison.OrdinalIgnoreCase)
            || executable.StartsWith("python3.", StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(executable[6..], out _);
        if (!python || intent.Arguments.Count != 2 || intent.Arguments[0] != "-c") return;

        // A repaired inline probe must use the same resolved interpreter and cwd.
        // Its exact invocation/approval identity remains separate and unchanged.
        var family = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "ru-python-inline-verification-repair-v1",
            executable = intent.ResolvedExecutable.ToUpperInvariant(),
            cwd = Path.TrimEndingDirectorySeparator(intent.CanonicalWorkingDirectory).ToUpperInvariant(),
        });
        output["verification_repair_identity"] = Convert.ToHexString(SHA256.HashData(family));
        if (!Number(output, "exit_code", out var exit) || exit == 0
            || !Number(output, "stdout_bytes", out var stdout) || stdout != 0
            || !Number(output, "stderr_dropped_bytes", out var dropped) || dropped != 0) return;
        var stderr = (Text(output, "stderr_head") + Text(output, "stderr_tail")).TrimStart();
        // Direct -c compilation errors have no traceback: no user statement ran.
        // Imported project syntax errors and deliberately raised SyntaxError have
        // a traceback and remain genuine failed checks.
        if (!stderr.StartsWith("File \"<string>\", line ", StringComparison.Ordinal)
            || stderr.Contains("Traceback (most recent call last):", StringComparison.Ordinal)
            || !stderr.Split('\n').Any(line => line.TrimStart().StartsWith("SyntaxError:", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("IndentationError:", StringComparison.Ordinal)
                || line.TrimStart().StartsWith("TabError:", StringComparison.Ordinal))) return;
        output["verification_invocation_invalid"] = true;
        output["hint"] = "The inline Python verification command did not parse. Correct its syntax and run a meaningful corrected probe "
            + "with the same Python interpreter and working directory. The failed exit remains recorded; project test failures cannot be cleared this way.";
    }

    private static string Text(JsonObject output, string name) =>
        output[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static bool Number(JsonObject output, string name, out long number)
    {
        number = 0;
        if (output[name] is not JsonValue value) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (!value.TryGetValue<int>(out var integer)) return false;
        number = integer;
        return true;
    }
}
