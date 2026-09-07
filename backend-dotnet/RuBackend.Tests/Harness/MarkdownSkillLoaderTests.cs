using System.Security.Cryptography;
using System.Text;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class MarkdownSkillLoaderTests
{
    [Fact]
    public void Parses_front_matter_and_body_without_treating_allowed_tools_as_authority()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var body = "# PDF Analyzer\n\nUse the existing file tool.";
        WriteSkill(root, "pdf", $"---\nname: PDF Analyzer\ndescription: Analyze and summarize PDF files\nallowed-tools: files.read, terminal.run\nlicense: MIT\n---\n{body}");

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("pdf-analyzer", skill.Id);
        Assert.Equal("PDF Analyzer", skill.Name);
        Assert.Equal("Analyze and summarize PDF files", skill.Description);
        Assert.Equal(body, skill.Body);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            skill.ContentSha256);
        Assert.DoesNotContain(typeof(MarkdownSkill).GetProperties(), property =>
            property.Name.Contains("tool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("allowed-tools", skill.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_markdown_without_front_matter_and_derives_metadata()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(root, "friendly-skill", "# Friendly Heading\n\nDo the useful thing.");

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("friendly-skill", skill.Id);
        Assert.Equal("friendly-skill", skill.Name);
        Assert.Equal("Friendly Heading", skill.Description);
        Assert.Equal("# Friendly Heading\n\nDo the useful thing.", skill.Body);
    }

    [Fact]
    public void Ignores_unknown_front_matter_and_malformed_metadata_lines()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(root, "metadata", "---\nname: metadata-skill\ndescription\nunknown: ignored\nmalformed metadata line\n---\n# Readable body\n\nContinue.");

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("metadata-skill", skill.Id);
        Assert.Equal("metadata-skill", skill.Name);
        Assert.Equal("Readable body", skill.Description);
        Assert.Equal("# Readable body\n\nContinue.", skill.Body);
        Assert.Empty(result.Skips);
    }

    [Fact]
    public void Discovers_sorted_skill_directories_and_reports_duplicate_by_root_precedence()
    {
        using var fixture = TempRoot.Create();
        var firstRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "second")).FullName;
        WriteSkill(firstRoot, "zeta", "# Zeta\n\nFirst root zeta.");
        WriteSkill(firstRoot, "shared", "# Shared\n\nFirst root wins.");
        WriteSkill(firstRoot, "Alpha", "# Alpha\n\nOrdinal first.");
        WriteSkill(secondRoot, "SHARED", "# Shared\n\nSecond root loses.");
        WriteSkill(secondRoot, "beta", "# Beta\n\nSecond root survives.");

        var result = new MarkdownSkillLoader().Load(new[] { firstRoot, secondRoot });

        Assert.Equal(new[] { "alpha", "shared", "zeta", "beta" }, result.Skills.Select(skill => skill.Id));
        Assert.Equal("First root wins.", result.Skills.Single(skill => skill.Id == "shared").Body.Split("\n\n")[1]);
        var duplicate = Assert.Single(result.Skips, skip => skip.Reason == "duplicate");
        Assert.EndsWith(Path.Combine("SHARED", "SKILL.md"), duplicate.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid_encoding")]
    [InlineData("contains_nul")]
    [InlineData("oversize")]
    [InlineData("reparse_point")]
    public void Skips_invalid_skill_without_stopping_other_skills(string expectedReason)
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(root, "valid", "# Valid\n\nThis one survives.");

        switch (expectedReason)
        {
            case "invalid_encoding":
                Directory.CreateDirectory(Path.Combine(root, "invalid"));
                File.WriteAllBytes(Path.Combine(root, "invalid", "SKILL.md"), new byte[] { 0xff, 0xfe });
                break;
            case "contains_nul":
                WriteSkill(root, "nul-skill", "# Contains NUL\n\nvalue\0");
                break;
            case "oversize":
                WriteSkill(root, "large", "# Large\n\n" + new string('x', 64 * 1024));
                break;
            case "reparse_point":
                using (var target = TempRoot.Create())
                {
                    WriteSkill(target.Root, "target", "# Target\n\nMust not be loaded.");
                    try
                    {
                        Directory.CreateSymbolicLink(Path.Combine(root, "reparse"), Path.Combine(target.Root, "target"));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        return;
                    }
                }
                break;
        }

        var result = new MarkdownSkillLoader().Load(new[] { root });

        Assert.Contains(result.Skills, skill => skill.Id == "valid");
        Assert.Contains(result.Skips, skip => skip.Reason == expectedReason);
    }

    [Fact]
    public void Allows_ancillary_files_without_reading_or_executing_them()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "with-assets")).FullName;
        File.WriteAllText(Path.Combine(package, "SKILL.md"), "# With assets\n\nUse references when needed.", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(package, "references"));
        Directory.CreateDirectory(Path.Combine(package, "assets"));
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        File.WriteAllText(Path.Combine(package, "references", "guide.md"), "reference", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(package, "assets", "payload.bin"), "asset", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(package, "scripts", "do-not-run.ps1"), "throw 'must not execute'", new UTF8Encoding(false));

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.Equal("with-assets", skill.Id);
        Assert.Empty(result.Skips);
        Assert.DoesNotContain("must not execute", skill.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Produces_safe_ids_bounded_descriptions_and_body_digest()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var body = "# Safe body\n\nInstructions.";
        var longDescription = new string('d', 4 * 1024);
        WriteSkill(root, "fallback-folder", $"---\nname: ../../Very Unsafe Skill!!!\ndescription: {longDescription}\n---\n{body}");

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.Matches("^[a-z0-9][a-z0-9._-]*$", skill.Id);
        Assert.DoesNotContain("..", skill.Id, StringComparison.Ordinal);
        Assert.InRange(skill.Description.Length, 1, 2048);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            skill.ContentSha256);
    }

    [Fact]
    public void Enforces_the_combined_skill_byte_cap_without_hiding_earlier_skills()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        for (var index = 0; index < 5; index++)
        {
            WriteSkill(root, $"skill-{index}", "# Skill\n\n" + new string((char)('a' + index), 60 * 1024));
        }

        var result = new MarkdownSkillLoader().Load(new[] { root });

        Assert.Equal(4, result.Skills.Count);
        Assert.Equal(1, result.Skips.Count(skip => skip.Reason == "oversize"));
        Assert.Equal(new[] { "skill-0", "skill-1", "skill-2", "skill-3" }, result.Skills.Select(skill => skill.Id));
    }

    [Fact]
    public void Uses_a_deterministic_nonempty_safe_id_when_metadata_and_folder_are_not_sluggable()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(root, "!!!", "# No slug\n\nStill usable.");

        var result = new MarkdownSkillLoader().Load(new[] { root });

        var skill = Assert.Single(result.Skills);
        Assert.NotEmpty(skill.Id);
        Assert.Matches("^[a-z0-9][a-z0-9._-]*$", skill.Id);
        Assert.Equal(skill.Id, new MarkdownSkillLoader().Load(new[] { root }).Skills.Single().Id);
    }

    [Fact]
    public void Reports_a_duplicate_before_the_capacity_skip_for_a_later_root()
    {
        using var fixture = TempRoot.Create();
        var firstRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "first")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(fixture.Root, "second")).FullName;
        for (var index = 0; index < MarkdownSkillLoader.MaxSkillCount; index++)
        {
            WriteSkill(firstRoot, $"skill-{index:00}", $"# Skill {index}\n\nFirst root.");
        }

        WriteSkill(secondRoot, "duplicate", "---\nname: skill-00\n---\n# Duplicate\n\nMust be reported as duplicate.");

        var result = new MarkdownSkillLoader().Load(new[] { firstRoot, secondRoot });

        Assert.Contains(result.Skips, skip => skip.Reason == "duplicate");
        Assert.DoesNotContain(result.Skips, skip => skip.SourcePath.EndsWith(Path.Combine("duplicate", "SKILL.md"), StringComparison.OrdinalIgnoreCase) && skip.Reason == "oversize");
    }

    [Fact]
    public void Bounds_child_directory_discovery_and_skip_diagnostics()
    {
        using var fixture = TempRoot.Create();
        var root = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        for (var index = 0; index < 300; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, $"empty-{index:000}"));
        }

        var result = new MarkdownSkillLoader().Load(new[] { root });

        Assert.Empty(result.Skills);
        Assert.Contains(result.Skips, skip => skip.Reason == "oversize");
        Assert.InRange(result.Skips.Count, 1, 257);
    }

    [Fact]
    public void Rejects_a_reparse_discovery_root_before_canonicalizing_it()
    {
        using var fixture = TempRoot.Create();
        using var target = TempRoot.Create();
        var root = Path.Combine(fixture.Root, "skills-link");
        WriteSkill(target.Root, "visible", "# Visible\n\nMust not be discovered through a root link.");

        try
        {
            Directory.CreateSymbolicLink(root, target.Root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = new MarkdownSkillLoader().Load(new[] { root });

        Assert.Empty(result.Skills);
        Assert.Contains(result.Skips, skip => skip.Reason == "reparse_point");
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
        }

        public string Parent { get; }

        public string Root { get; }

        public static TempRoot Create()
        {
            var parent = Path.Combine(Path.GetTempPath(), "ru-markdown-skill-loader-" + Guid.NewGuid().ToString("N"));
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
