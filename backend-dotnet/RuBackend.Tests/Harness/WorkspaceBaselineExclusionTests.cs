using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceBaselineExclusionTests
{
    [Theory]
    [InlineData("AppData/Local/Ru/agent_harness.db-wal", true)]
    [InlineData("AppData/Local/Ru/agent_harness.db", true)]
    [InlineData("appdata/local/ru/settings.json", true)]
    [InlineData(".codex/state_5.sqlite-wal", true)]
    [InlineData("data/cache.sqlite3", true)]
    [InlineData("logs/app.log", true)]
    [InlineData("work/.git/index", true)]
    [InlineData("web/node_modules/pkg/index.js", true)]
    [InlineData("src/__pycache__/mod.cpython-312.pyc", true)]
    [InlineData("src/bin/Debug/net10.0/app.dll", true)]
    [InlineData("src/obj/project.assets.json", true)]
    [InlineData("tests/.pytest_cache/v/cache/nodeids", true)]
    [InlineData("tests/.mypy_cache/3.12/pkg.data.json", true)]
    [InlineData("tests/.ruff_cache/0.13/cache.db", true)]
    [InlineData("build.tmp", true)]
    [InlineData("src/binary/source.cs", false)]
    [InlineData("src/object_mapper.py", false)]
    [InlineData("src/bin-tools/source.cs", false)]
    [InlineData("src/obj-models/source.py", false)]
    [InlineData("src/app.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("AppData/Roaming/other/settings.json", false)]
    [InlineData("docs/database.md", false)]
    [InlineData("gitignore.txt", false)]
    public void Volatile_paths_are_recognised_by_folder_and_suffix(string path, bool expected) =>
        Assert.Equal(expected, WorkspaceExternalChangeMonitor.IsVolatilePath(path));

    [Fact]
    public void The_baseline_skips_volatile_files_and_keeps_content_only_for_small_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        Directory.CreateDirectory(Path.Combine(root, "src", "bin"));
        Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "src", "binary"));
        Directory.CreateDirectory(Path.Combine(root, "src", "object_mapper"));
        try
        {
            File.WriteAllText(Path.Combine(root, "small.txt"), "tiny");
            File.WriteAllBytes(Path.Combine(root, "large.bin"), new byte[WorkspaceExternalChangeMonitor.MaxRetainedContentBytes + 1]);
            File.WriteAllBytes(Path.Combine(root, "store.db-wal"), new byte[64]);
            File.WriteAllText(Path.Combine(root, "run.log"), "noise");
            File.WriteAllText(Path.Combine(root, ".git", "index"), "git");
            File.WriteAllText(Path.Combine(root, "node_modules", "dep.js"), "js");
            File.WriteAllText(Path.Combine(root, "src", "bin", "generated.dll"), "generated");
            File.WriteAllText(Path.Combine(root, "src", "obj", "generated.cache"), "generated");
            File.WriteAllText(Path.Combine(root, "src", "binary", "source.cs"), "source");
            File.WriteAllText(Path.Combine(root, "src", "object_mapper", "source.py"), "source");
            var monitor = new WorkspaceExternalChangeMonitor();

            var captured = monitor.CaptureExpectedHashes(root);

            Assert.Equal(4, captured);
            Assert.True(monitor.TryGetExpectedContent(root, "small.txt", out var small));
            Assert.Equal("tiny", System.Text.Encoding.UTF8.GetString(small!));
            Assert.False(monitor.TryGetExpectedContent(root, "large.bin", out _));
            Assert.True(monitor.TryGetExpectedContent(root, "src/binary/source.cs", out _));
            Assert.True(monitor.TryGetExpectedContent(root, "src/object_mapper/source.py", out _));
            Assert.False(monitor.TryGetExpectedContent(root, "src/bin/generated.dll", out _));
            Assert.False(monitor.TryGetExpectedContent(root, "src/obj/generated.cache", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
