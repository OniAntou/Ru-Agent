using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RuInstructionLoaderTests
{
    [Fact]
    public void Loads_only_root_candidates_in_stable_order_and_normalizes_content()
    {
        using var fixture = TempRoot.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Ru.md"), "root guidance\r\n", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), "agent guidance", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), "context guidance", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Parent, "Ru.md"), "parent must not load", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, "notes.md"), "arbitrary must not load", new UTF8Encoding(false));

        var result = new RuInstructionLoader().Load(fixture.Root);

        Assert.Equal(
            new[] { "root:Ru.md", "ru:AGENTS.md", "ru:CONTEXT.md" },
            result.Blocks.Select(block => block.SourceId));
        Assert.Equal("root guidance", result.Blocks[0].Content);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("root guidance"))).ToLowerInvariant(),
            result.Blocks[0].ContentSha256);
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("parent", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("arbitrary", StringComparison.Ordinal));
    }

    [Fact]
    public void Returns_bounded_skip_reasons_for_invalid_candidates_without_paths()
    {
        using var fixture = TempRoot.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllBytes(Path.Combine(fixture.Root, "Ru.md"), new byte[] { 0xff, 0xfe });
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), new string('x', 64 * 1024 + 1), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), "contains\0nul", new UTF8Encoding(false));

        var result = new RuInstructionLoader().Load(fixture.Root);

        Assert.Empty(result.Blocks);
        Assert.Equal(
            new[] { "invalid_encoding", "oversize", "contains_nul" },
            result.Skips.Select(skip => skip.Reason));
        Assert.All(result.Skips, skip => Assert.DoesNotContain(fixture.Root, skip.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Skips_a_ru_directory_link_that_resolves_outside_the_run_root()
    {
        using var fixture = TempRoot.Create();
        using var outside = TempRoot.Create();
        File.WriteAllText(Path.Combine(outside.Root, "AGENTS.md"), "outside guidance", new UTF8Encoding(false));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(fixture.Root, ".ru"), outside.Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating directory links can require an elevated Windows token; the
            // loader's lexical and regular-file containment tests still run above.
            return;
        }

        var result = new RuInstructionLoader().Load(fixture.Root);

        Assert.DoesNotContain(result.Blocks, block => block.SourceId == "ru:AGENTS.md");
        Assert.Contains(result.Skips, skip => skip.SourceId == "ru:AGENTS.md" && skip.Reason == "outside_root");
    }

    private sealed class TempRoot : IDisposable
    {
        private TempRoot(string parent, string root)
        {
            Parent = parent;
            Root = root;
        }

        public string Parent { get; }
        public string Root { get; }

        public static TempRoot Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "ru-prompt-loader-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(parent, "workspace");
            Directory.CreateDirectory(root);
            return new TempRoot(parent, root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Parent, recursive: true);
            }
            catch
            {
            }
        }
    }
}
