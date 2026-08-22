using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WorkspacePathGuardTests
{
    [Fact]
    public void Guard_allows_nested_paths_and_rejects_escape_forms()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "main.cs"), "class C {}");
        try
        {
            var guard = new WorkspacePathGuard(root);
            Assert.EndsWith(Path.Combine("src", "main.cs"), guard.Resolve(Path.Combine("src", "main.cs")));
            Assert.Throws<UnauthorizedAccessException>(() => guard.Resolve(".."));
            Assert.Throws<UnauthorizedAccessException>(() => guard.Resolve(Path.GetFullPath(Path.Combine(root, "..", "outside.txt"))));
            Assert.Throws<FileNotFoundException>(() => guard.Resolve("missing.txt"));
            Assert.EndsWith("missing.txt", guard.Resolve("missing.txt", allowMissing: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
