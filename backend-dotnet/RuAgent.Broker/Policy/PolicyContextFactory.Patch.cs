using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Policy;

public sealed partial class PolicyContextFactory
{
    private PolicyContext CreatePatchContext(string version, JsonObject arguments)
    {
        if (arguments.Count != 1 || arguments["patch"] is not JsonValue value || !value.TryGetValue<string>(out var patch))
            throw new PolicyContextException("invalid_arguments", hint: "apply_patch requires only patch containing a unified diff.");
        IReadOnlyList<ParsedPatchFile> files;
        try { files = UnifiedPatchParser.Parse(patch); }
        catch (ArgumentException error) { throw new PolicyContextException(error.Message, error); }
        if (files.Count == 0) throw new PolicyContextException("patch_empty");
        var targets = new List<PolicyContext>(files.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundaryResolver = new CanonicalPathResolver(_canonicalRoot, allowProtectedPaths: true);
        foreach (var file in files)
        {
            // A rename is evaluated as exactly a delete of its source plus a create of its
            // target, so every rule, protected path and boundary check sees both paths.
            AddTarget(file.TargetPath, file.IsNewFile || file.IsDeletedFile || file.IsRename ? "write" : "edit",
                file.IsDeletedFile ? "delete" : "write");
            if (file.IsRename) AddTarget(file.SourcePath!, "write", "delete");
        }
        var canonicalTargets = new JsonArray(targets.Select(target => (JsonNode?)target.Target!.CanonicalIntentHash).ToArray());
        var hashArguments = new JsonObject { ["patch"] = patch, ["canonical_targets"] = canonicalTargets };
        return targets[0] with
        {
            OperationClass = PolicyOperationClasses.Patch,
            IntentHash = ComputeIntentHash("apply_patch", version, null, null, [], null, PolicyOperationClasses.Patch, hashArguments),
            PatchTargets = targets.AsReadOnly(),
        };

        void AddTarget(string path, string toolId, string operation)
        {
            var target = Create(toolId, version, new JsonObject { ["path"] = path, ["op"] = operation });
            var resolved = boundaryResolver.Resolve(path);
            if (!resolved.IsInsideWorkspace)
                throw new PolicyContextException("patch_outside_workspace");
            if (!paths.Add(resolved.CanonicalPath)) throw new PolicyContextException("patch_duplicate_target");
            targets.Add(target with { ToolId = "apply_patch", RelativePathInsideWorkspace = Path.GetRelativePath(_canonicalRoot, resolved.CanonicalPath) });
        }
    }
}
