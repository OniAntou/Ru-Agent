using System.Runtime.CompilerServices;

namespace RuApp.Tests;

/// <summary>
/// Points GUI diagnostics at a per-run temporary folder before any test runs. Without it,
/// tests that simulate Host 500s or malformed usage payloads appended hundreds of
/// "GetSettingsAsync failed" and "usage payload is not valid JSON" lines to the user's
/// real <c>%LOCALAPPDATA%\Ru\logs\gui-trace.jsonl</c>, which then read like product failures.
/// </summary>
internal static class TestLogIsolation
{
    [ModuleInitializer]
    internal static void IsolateLogRoot()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RU_LOG_ROOT"))) return;
        var root = Path.Combine(Path.GetTempPath(), "ru-gui-tests-logs", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("RU_LOG_ROOT", root);
    }
}
