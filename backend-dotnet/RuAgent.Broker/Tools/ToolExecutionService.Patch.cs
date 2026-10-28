using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private ValueTask<ToolExecutionOutcome> ExecutePatchAsync(ToolExecutionContext context, JsonObject arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tools = ToolsFor(context);
        var patch = ReadString(arguments, "patch");
        var files = UnifiedPatchParser.Parse(patch);
        if (files.Count == 0) throw new ArgumentException("patch_empty");
        var hashes = new JsonObject();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Hunks.Count == 0 || file.IsNewFile && file.IsDeletedFile) throw new ArgumentException("invalid_patch_header");
            if (!file.IsNewFile && !file.IsDeletedFile && !string.Equals(file.SourcePath, file.TargetPath, StringComparison.Ordinal))
                throw new ArgumentException("patch_rename_unsupported");
            var target = tools.MutationResolver.Resolve(file.TargetPath);
            if (target.IsOutsideWorkspace) throw new UnauthorizedAccessException("patch_outside_workspace");
            if (target.IsProtected) throw new UnauthorizedAccessException("protected_path");
            if (!paths.Add(target.CanonicalPath)) throw new ArgumentException("patch_duplicate_target");
            if (File.Exists(target.CanonicalPath)) totalBytes += new FileInfo(target.CanonicalPath).Length;
            if (totalBytes > FileMutationJournal.MaxTotalBytes) throw new ArgumentException("patch_preimages_too_large");
            var hash = FileHashService.GetCurrentHash(target.CanonicalPath);
            if (file.IsNewFile && !FileHashService.IsMissing(hash)) throw new IOException("file_exists");
            if (!file.IsNewFile && FileHashService.IsMissing(hash)) throw new IOException("file_not_found");
            if (file.IsDeletedFile)
            {
                var original = StrictUtf8.GetString(ReadBoundedBytes(target.CanonicalPath, FileWriteAdapter.MaxInputBytes));
                if (UnifiedPatchParser.ApplyHunks(original, file.Hunks).Length != 0)
                    throw new ArgumentException("patch_delete_not_empty");
            }
            hashes[file.TargetPath] = hash;
        }
        var adapterArguments = new JsonObject { ["patch"] = patch, ["expected_hashes"] = hashes };
        var profile = ActivePolicyProfile(context);
        var adapter = new FilePatchAdapter(tools.CanonicalRoot, allowProtectedPaths: profile is not null && AgentPolicyProfileCatalog.OpensProtectedPaths(profile));
        var output = FileMutationGuard.WithLock(() =>
        {
            // Resolve every hunk before the first write, then retain the same preimage hashes.
            adapter.Prepare(adapterArguments);
            ct.ThrowIfCancellationRequested();
            return adapter.Execute(adapterArguments);
        });
        return Succeeded(output, "Workspace patch applied.");
    }
}
