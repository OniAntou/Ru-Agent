using System.Text;

namespace RuAgent.Broker.Tests;

internal static class TerminalTestSupport
{
    public static string PowerShellExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    public static string[] PowerShellArgs(string script) =>
    [
        "-NoProfile",
        "-NonInteractive",
        "-EncodedCommand",
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
    ];
}
