using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Policy;

/// <summary>
/// Builds a <see cref="PolicyContext"/> from trusted resolution. The workspace root comes only from
/// the v2 run lease — never from RU_WORKSPACE_ROOT, the broker current directory, or model input.
/// </summary>
public sealed class PolicyContextFactory
{
    private readonly string _canonicalRoot;

    public PolicyContextFactory(BrokerRunLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _canonicalRoot = lease.CanonicalRoot;
    }

    public PolicyContext Create(string toolId, string toolVersion, JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(arguments);

        var resolver = new CanonicalPathResolver(_canonicalRoot);
        var operationClass = OperationClassFor(toolId);
        var networkDeclared = arguments["network"] is JsonValue networkValue
            && networkValue.TryGetValue<bool>(out var declared)
            && declared;

        ResolvedPath? target = null;
        string? canonicalCwd = null;
        var argv = PolicyContext.EmptyArgv;
        string? resolvedExecutable = null;

        if (operationClass == PolicyOperationClasses.Terminal)
        {
            var workingDirectory = OptionalText(arguments, "working_directory") ?? ".";
            try
            {
                var cwd = resolver.Resolve(workingDirectory);
                if (cwd.Classification != PathClassification.InsideWorkspace)
                {
                    throw new PolicyContextException("cwd_outside_workspace");
                }

                canonicalCwd = cwd.CanonicalPath;
            }
            catch (ArgumentException error)
            {
                throw new PolicyContextException("invalid_cwd", error);
            }

            resolvedExecutable = OptionalText(arguments, "executable");
            if (arguments["arguments"] is JsonArray argvNode)
            {
                var list = new List<string>(argvNode.Count);
                foreach (var item in argvNode)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var element))
                    {
                        throw new PolicyContextException("malformed_call");
                    }

                    list.Add(element);
                }

                argv = list;
            }
            else if (arguments["arguments"] is not null || arguments.ContainsKey("arguments"))
            {
                throw new PolicyContextException("malformed_call");
            }
        }
        else
        {
            var pathInput = OptionalText(arguments, "path");
            if (pathInput is null)
            {
                throw new PolicyContextException("malformed_call");
            }

            try
            {
                target = resolver.Resolve(pathInput);
            }
            catch (ArgumentException error)
            {
                throw new PolicyContextException("invalid_path", error);
            }
        }

        return new PolicyContext(
            ToolId: toolId,
            ToolVersion: toolVersion,
            CanonicalWorkspaceRoot: _canonicalRoot,
            Target: target,
            RelativePathInsideWorkspace: target is { IsInsideWorkspace: true } inside
                ? ToRelative(_canonicalRoot, inside.CanonicalPath)
                : null,
            ResolvedExecutable: resolvedExecutable,
            Argv: argv,
            CanonicalCwd: canonicalCwd,
            OperationClass: operationClass,
            NetworkIntent: networkDeclared ? PolicyNetworkIntents.Declared : PolicyNetworkIntents.None,
            NetworkDeclared: networkDeclared,
            IntentHash: ComputeIntentHash(
                toolId,
                toolVersion,
                target?.CanonicalIntentHash,
                resolvedExecutable,
                argv,
                canonicalCwd,
                operationClass));
    }

    private static string OperationClassFor(string toolId) => toolId switch
    {
        "files.read" => PolicyOperationClasses.Read,
        "files.list" => PolicyOperationClasses.List,
        "files.glob" => PolicyOperationClasses.Glob,
        "files.search" => PolicyOperationClasses.Search,
        "files.write" => PolicyOperationClasses.Write,
        "files.edit" => PolicyOperationClasses.Edit,
        "files.apply_patch" => PolicyOperationClasses.Patch,
        "files.delete" => PolicyOperationClasses.Delete,
        "files.restore" => PolicyOperationClasses.Restore,
        "terminal.run" => PolicyOperationClasses.Terminal,
        "terminal.shell" => PolicyOperationClasses.Terminal,
        _ => PolicyOperationClasses.Mutation,
    };

    private static string? OptionalText(JsonObject arguments, string key) =>
        arguments[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
            ? result
            : null;

    private static string ToRelative(string root, string canonicalPath)
    {
        var relative = Path.GetRelativePath(root, canonicalPath);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    private static string ComputeIntentHash(
        string toolId,
        string toolVersion,
        string? targetHash,
        string? resolvedExecutable,
        IReadOnlyList<string> argv,
        string? canonicalCwd,
        string operationClass)
    {
        var builder = new StringBuilder();
        builder.Append(toolId).Append('|').Append(toolVersion).Append('|')
            .Append(targetHash ?? "-").Append('|')
            .Append(resolvedExecutable ?? "-").Append('|')
            .Append(string.Join(' ', argv)).Append('|')
            .Append(canonicalCwd ?? "-").Append('|')
            .Append(operationClass);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
