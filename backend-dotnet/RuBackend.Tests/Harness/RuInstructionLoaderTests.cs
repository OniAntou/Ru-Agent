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

    [Fact]
    public void Appends_markdown_skills_after_fixed_roots_in_precedence_order_and_marks_origins()
    {
        using var fixture = TempRoot.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "Ru.md"), "root guidance", new UTF8Encoding(false));
        var workspaceSkills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var ruSkills = Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru", "skills")).FullName;
        var userSkills = Directory.CreateDirectory(Path.Combine(fixture.UserDataRoot, "Ru", "skills")).FullName;
        WriteSkill(workspaceSkills, "alpha", "# Alpha\n\nWorkspace alpha.");
        WriteSkill(ruSkills, "alpha-copy", "---\nname: alpha\n---\n# Alpha copy\n\nThe first root wins.");
        WriteSkill(ruSkills, "beta", "# Beta\n\nRu beta.");
        WriteSkill(userSkills, "gamma", "# Gamma\n\nUser gamma.");

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot).Load(fixture.Root);

        Assert.Equal(
            new[] { "root:Ru.md", "skill:alpha", "skill:beta", "skill:gamma" },
            result.Blocks.Select(block => block.SourceId));
        Assert.Equal(PromptInstructionOrigin.Workspace, result.Blocks[0].Origin);
        Assert.All(result.Blocks.Skip(1), block => Assert.Equal(PromptInstructionOrigin.ExternalSkill, block.Origin));
        Assert.Contains(result.Skips, skip => skip.Reason == "duplicate");
        var alpha = result.Blocks.Single(block => block.SourceId == "skill:alpha");
        Assert.Contains("Workspace alpha", alpha.Content);
        Assert.Contains("Alpha", alpha.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("Alpha copy", StringComparison.Ordinal));

        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("model", "windows", "en-US", "UTC", "2026-09-07", "workspace"),
            result.Blocks,
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(null, null, null, null, Array.Empty<PromptReference>()));
        var prompt = SystemPromptComposer.Compose(context);
        prompt.Receipt.ValidateAgainst(context, prompt.Text);
        Assert.Contains(SystemPromptComposer.ExternalSkillInstructionBoundary, prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Skips_later_skills_when_fixed_roots_consume_the_combined_instruction_cap()
    {
        using var fixture = TempRoot.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllText(Path.Combine(fixture.Root, "Ru.md"), new string('r', 64 * 1024), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), new string('a', 64 * 1024), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), new string('c', 64 * 1024), new UTF8Encoding(false));
        var skills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(skills, "first", "# First\n\n" + new string('f', 65_000));
        WriteSkill(skills, "second", "# Second\n\n" + new string('s', 65_000));

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot).Load(fixture.Root);

        Assert.Contains(result.Blocks, block => block.SourceId == "skill:first");
        Assert.DoesNotContain(result.Blocks, block => block.SourceId == "skill:second");
        Assert.Contains(result.Skips, skip => skip.SourceId == "skill:second" && skip.Reason == "oversize");
    }

    private static void WriteSkill(string root, string directoryName, string content)
    {
        var package = Directory.CreateDirectory(Path.Combine(root, directoryName)).FullName;
        File.WriteAllText(Path.Combine(package, "SKILL.md"), content, new UTF8Encoding(false));
    }

    private sealed class TempRoot : IDisposable
    {
        private TempRoot(string parent, string root)
        {
            Parent = parent;
            Root = root;
            UserDataRoot = Path.Combine(parent, "local-app-data");
            Directory.CreateDirectory(UserDataRoot);
        }

        public string Parent { get; }
        public string Root { get; }

        public string UserDataRoot { get; }

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
