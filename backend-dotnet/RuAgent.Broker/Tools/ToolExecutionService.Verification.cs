using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    // Completion bookkeeping only. Approval still uses the full authorized intent,
    // including timeout and operation class; this digest can never authorize a call.
    private static void AddVerificationIdentity(JsonObject output, CanonicalCommandIntent intent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "ru-terminal-verification-v1",
            executable = intent.ResolvedExecutable.ToUpperInvariant(),
            arguments = intent.Arguments,
            cwd = Path.TrimEndingDirectorySeparator(intent.CanonicalWorkingDirectory).ToUpperInvariant(),
        });
        output["verification_identity"] = Convert.ToHexString(SHA256.HashData(bytes));
        output["verification_cwd"] = intent.CanonicalWorkingDirectory;
    }
}
