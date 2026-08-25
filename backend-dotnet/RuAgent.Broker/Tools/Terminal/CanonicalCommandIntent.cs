using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Terminal;

public sealed class CommandIntentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A bounded terminal.run request before canonical resolution.
/// </summary>
public sealed record CanonicalCommandRequest(
    string Executable,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int? TimeoutMs = null,
    bool NetworkIntent = false);

/// <summary>
/// A bounded terminal.shell request; only explicit PowerShell runtimes are legal.
/// </summary>
public sealed record CanonicalShellRequest(
    string Script,
    string? WorkingDirectory = null,
    int? TimeoutMs = null,
    bool NetworkIntent = false,
    string? ShellRuntime = null);

/// <summary>
/// The immutable, fully resolved execution identity for one terminal call.
/// The intent hash binds resolved executable, argv, canonical cwd, operation class,
/// network intent, and timeout; any changed field produces a different hash so
/// approvals bound to an older hash can never be replayed against it.
/// </summary>
public sealed record CanonicalCommandIntent(
    string ResolvedExecutable,
    IReadOnlyList<string> Arguments,
    string CanonicalWorkingDirectory,
    PathClassification WorkingDirectoryClass,
    bool NetworkIntent,
    int TimeoutMs,
    TerminalOperationClass OperationClass,
    TerminalTimeoutClass TimeoutClass,
    string IntentHash)
{
    public bool IsInsideWorkspace => WorkingDirectoryClass == PathClassification.InsideWorkspace;

    public bool IsProtected => WorkingDirectoryClass == PathClassification.Protected;

    public CanonicalCommandIntent Classified(TerminalOperationClass operationClass)
    {
        if (operationClass == OperationClass)
        {
            return this;
        }

        return this with
        {
            OperationClass = operationClass,
            IntentHash = ComputeIntentHash(
                ResolvedExecutable,
                Arguments,
                CanonicalWorkingDirectory,
                NetworkIntent,
                TimeoutMs,
                operationClass),
        };
    }

    internal static string ComputeIntentHash(
        string resolvedExecutable,
        IReadOnlyList<string> arguments,
        string canonicalWorkingDirectory,
        bool networkIntent,
        int timeoutMs,
        TerminalOperationClass operationClass)
    {
        var fields = new[]
        {
            "v1",
            JsonSerializer.Serialize(resolvedExecutable),
            JsonSerializer.Serialize(string.Join('\u001f', arguments)),
            JsonSerializer.Serialize(canonicalWorkingDirectory),
            networkIntent ? "true" : "false",
            timeoutMs.ToString(CultureInfo.InvariantCulture),
            operationClass.ToString(),
        };
        var payload = string.Join('\u001e', fields);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
