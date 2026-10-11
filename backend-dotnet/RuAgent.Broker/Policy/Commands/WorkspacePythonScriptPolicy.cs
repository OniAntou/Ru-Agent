using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Policy.Commands;

// This checks the entry file's location, not what the Python program can do.
// Both policy evaluation and execution canonicalization use the same boundary.
internal static class WorkspacePythonScriptPolicy
{
    internal static bool IsLocalFile(string root, string cwd, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var resolved = new CanonicalPathResolver(root).Resolve(
                Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
            return resolved.IsInsideWorkspace && !resolved.IsProtected && File.Exists(resolved.CanonicalPath);
        }
        catch (ArgumentException) { return false; }
    }
}
