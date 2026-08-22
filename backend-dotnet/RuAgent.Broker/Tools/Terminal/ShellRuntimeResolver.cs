namespace RuAgent.Broker.Tools.Terminal;

public sealed record ResolvedShell(string Executable, IReadOnlyList<string> PrefixArguments);

public sealed class ShellRuntimeResolver
{
    public ResolvedShell Resolve(string shell)
    {
        return shell.Trim().ToLowerInvariant() switch
        {
            "powershell" => new("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command"]),
            "pwsh" => new("pwsh.exe", ["-NoProfile", "-NonInteractive", "-Command"]),
            "git-bash" => new("bash.exe", ["--noprofile", "--norc", "-lc"]),
            "wsl" => new("wsl.exe", ["--cd"]),
            _ => throw new ArgumentException("unsupported_shell", nameof(shell)),
        };
    }
}
