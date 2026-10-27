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
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "root guidance\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, "CONTEXT.md"), "workflow context", new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), "agent guidance", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), "context guidance", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Parent, "AGENTS.md"), "parent must not load", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, "notes.md"), "arbitrary must not load", new UTF8Encoding(false));

        var result = new RuInstructionLoader().Load(fixture.Root);

        Assert.Equal(
            new[] { "root:AGENTS.md", "root:CONTEXT.md", "ru:AGENTS.md", "ru:CONTEXT.md", "skill:catalog" },
            result.Blocks.Select(block => block.SourceId));
        Assert.Equal("root guidance", result.Blocks[0].Content);
        Assert.DoesNotContain("workflow context", result.Blocks[1].Content, StringComparison.Ordinal);
        Assert.Contains("CONTEXT.md", result.Blocks[1].Content, StringComparison.Ordinal);
        Assert.Contains(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("workflow context"))).ToLowerInvariant(),
            result.Blocks[1].Content,
            StringComparison.Ordinal);
        Assert.Equal(PromptInstructionOrigin.Workspace, result.Blocks[1].Origin);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("root guidance"))).ToLowerInvariant(),
            result.Blocks[0].ContentSha256);
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("parent", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("arbitrary", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("context guidance", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_domain_context_files_are_bounded_references_not_inlined_instructions()
    {
        using var fixture = TempRoot.Create();
        const string rootContext = "# Ru domain language\n\nLong root domain text.";
        const string ruContext = "# Local domain notes\n\nLong local domain text.";
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllText(Path.Combine(fixture.Root, "CONTEXT.md"), rootContext, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), ruContext, new UTF8Encoding(false));

        var result = new RuInstructionLoader().Load(fixture.Root);
        var root = result.Blocks.Single(block => block.SourceId == "root:CONTEXT.md");
        var local = result.Blocks.Single(block => block.SourceId == "ru:CONTEXT.md");

        Assert.DoesNotContain("Long root domain text", root.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Long local domain text", local.Content, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".ru", "CONTEXT.md"), local.Content, StringComparison.Ordinal);
        Assert.Contains(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootContext))).ToLowerInvariant(),
            root.Content,
            StringComparison.Ordinal);

        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("model", "windows", "en-US", "UTC", "2026-10-26", "workspace"),
            result.Blocks,
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(Array.Empty<PromptReference>()));
        var prompt = SystemPromptComposer.Compose(context);
        prompt.Receipt.ValidateAgainst(context, prompt.Text);
        Assert.Contains(SystemPromptComposer.DomainReferenceBoundary, prompt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Long root domain text", prompt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Long local domain text", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Returns_bounded_skip_reasons_for_invalid_candidates_without_paths()
    {
        using var fixture = TempRoot.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllBytes(Path.Combine(fixture.Root, "AGENTS.md"), new byte[] { 0xff, 0xfe });
        File.WriteAllBytes(Path.Combine(fixture.Root, "CONTEXT.md"), new byte[] { 0xff, 0xfe });
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), new string('x', 64 * 1024 + 1), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), "contains\0nul", new UTF8Encoding(false));

        var result = new RuInstructionLoader().Load(fixture.Root);

        Assert.Equal("skill:catalog", Assert.Single(result.Blocks).SourceId);
        Assert.Equal(
            new[] { "invalid_encoding", "invalid_encoding", "oversize", "contains_nul" },
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
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "root guidance", new UTF8Encoding(false));
        var workspaceSkills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        var ruSkills = Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru", "skills")).FullName;
        var userSkills = Directory.CreateDirectory(Path.Combine(fixture.UserDataRoot, "Ru", "skills")).FullName;
        WriteSkill(workspaceSkills, "alpha", "# Alpha\n\nWorkspace alpha.");
        WriteSkill(ruSkills, "alpha-copy", "---\nname: alpha\n---\n# Alpha copy\n\nThe first root wins.");
        WriteSkill(ruSkills, "beta", "# Beta\n\nRu beta.");
        WriteSkill(userSkills, "gamma", "# Gamma\n\nUser gamma.");

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot).Load(fixture.Root);

        Assert.Equal(
            new[] { "root:AGENTS.md", "skill:catalog" },
            result.Blocks.Select(block => block.SourceId));
        Assert.Equal(PromptInstructionOrigin.Workspace, result.Blocks[0].Origin);
        Assert.All(result.Blocks.Skip(1), block => Assert.Equal(PromptInstructionOrigin.ExternalSkill, block.Origin));
        var alpha = result.Blocks.Single(block => block.SourceId == "skill:catalog");
        // The single catalog block carries bounded metadata and paths, never workflow bodies.
        Assert.Equal("Available skills", alpha.DisplayName);
        Assert.Contains("skill tool", alpha.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha | alpha", alpha.Content, StringComparison.Ordinal);
        Assert.Contains("alpha-copy", alpha.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace alpha", alpha.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Blocks, block => block.Content.Contains("Alpha copy", StringComparison.Ordinal));

        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("model", "windows", "en-US", "UTC", "2026-09-07", "workspace"),
            result.Blocks,
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(Array.Empty<PromptReference>()));
        var prompt = SystemPromptComposer.Compose(context);
        prompt.Receipt.ValidateAgainst(context, prompt.Text);
        Assert.Contains(SystemPromptComposer.ExternalSkillInstructionBoundary, prompt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Workspace alpha.", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Loads_the_user_wide_instruction_first_and_marks_it_as_user_guidance()
    {
        using var fixture = TempRoot.Create();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Parent, "profile")).FullName;
        Directory.CreateDirectory(Path.Combine(profile, ".ru"));
        File.WriteAllText(Path.Combine(profile, ".ru", "AGENTS.md"), "always reply briefly", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), "workspace guidance", new UTF8Encoding(false));

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot, userProfileRoot: profile)
            .Load(fixture.Root);

        Assert.Equal(new[] { "user:AGENTS.md", "root:AGENTS.md", "skill:catalog" }, result.Blocks.Select(block => block.SourceId));
        Assert.Equal(PromptInstructionOrigin.User, result.Blocks[0].Origin);
        Assert.Equal("~/.ru/AGENTS.md", result.Blocks[0].DisplayName);
        Assert.Equal(PromptInstructionOrigin.Workspace, result.Blocks[1].Origin);

        var context = new SystemPromptContext(
            SystemPromptContext.PromptContextSchemaVersion,
            SystemPromptContext.RequiredAgentProfile,
            "standard",
            new PromptEnvironment("model", "windows", "en-US", "UTC", "2026-09-07", "workspace"),
            result.Blocks,
            Array.Empty<PromptCapability>(),
            new PromptRuntimeContext(Array.Empty<PromptReference>()));
        var prompt = SystemPromptComposer.Compose(context);
        prompt.Receipt.ValidateAgainst(context, prompt.Text);
        Assert.Contains(SystemPromptComposer.UserInstructionBoundary, prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_user_wide_instruction_is_not_reported_and_a_linked_one_outside_the_profile_is_refused()
    {
        using var fixture = TempRoot.Create();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Parent, "profile")).FullName;

        var missing = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot, userProfileRoot: profile)
            .Load(fixture.Root);

        Assert.DoesNotContain(missing.Skips, skip => skip.SourceId == "user:AGENTS.md");

        var outside = Directory.CreateDirectory(Path.Combine(fixture.Parent, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "not yours", new UTF8Encoding(false));
        var ru = Path.Combine(profile, ".ru");
        try
        {
            Directory.CreateSymbolicLink(ru, outside);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return; // Symbolic links need a privilege this machine does not grant.
        }

        var linked = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot, userProfileRoot: profile)
            .Load(fixture.Root);

        Assert.DoesNotContain(linked.Blocks, block => block.SourceId == "user:AGENTS.md");
        Assert.Contains(linked.Skips, skip => skip.SourceId == "user:AGENTS.md" && skip.Reason == "outside_root");
    }

    [Fact]
    public void Discovers_shared_user_skills_with_workspace_and_ru_user_precedence()
    {
        using var fixture = TempRoot.Create();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Parent, "profile")).FullName;
        var agentsSkills = Directory.CreateDirectory(Path.Combine(fixture.Root, ".agents", "skills")).FullName;
        var homeSkills = Directory.CreateDirectory(Path.Combine(profile, ".ru", "skills")).FullName;
        var sharedHomeSkills = Directory.CreateDirectory(Path.Combine(profile, ".agents", "skills")).FullName;
        WriteSkill(agentsSkills, "delta", "# Delta\n\nFrom the shared agents folder.");
        WriteSkill(homeSkills, "delta-home", "---\nname: delta\n---\n# Delta home\n\nLoses to the workspace copy.");
        WriteSkill(homeSkills, "epsilon", "# Epsilon\n\nFrom the user profile.");
        WriteSkill(sharedHomeSkills, "zeta", "---\nname: Shared Zeta\ndescription: Shared user workflow\n---\nPrivate shared body.");
        WriteSkill(homeSkills, "delta", "---\nname: Home collision\ndescription: Home delta must lose\n---\nHome body.");
        WriteSkill(sharedHomeSkills, "delta", "---\nname: Shared collision\ndescription: Shared delta must lose\n---\nShared body.");
        WriteSkill(sharedHomeSkills, "epsilon", "---\nname: Shared epsilon\ndescription: Shared epsilon must lose\n---\nShared body.");

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot, userProfileRoot: profile)
            .Load(fixture.Root);

        Assert.Equal(new[] { "skill:catalog" }, result.Blocks.Select(block => block.SourceId));
        Assert.Contains("delta-home", result.Blocks[0].Content, StringComparison.Ordinal);
        Assert.Contains("zeta | Shared Zeta | Shared user workflow", result.Blocks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Home delta must lose", result.Blocks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared delta must lose", result.Blocks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Shared epsilon must lose", result.Blocks[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Private shared body", result.Blocks[0].Content, StringComparison.Ordinal);

        var loaded = new MarkdownSkillLoader().Load(
            RuAgent.Protocol.SkillRoots.For(fixture.Root, fixture.UserDataRoot, profile, configuredRoot: ""));
        var sharedBody = Assert.Single(loaded.Skills, skill => skill.Id == "zeta");
        Assert.Equal(Path.Combine(sharedHomeSkills, "zeta"), Path.GetDirectoryName(sharedBody.SourcePath));
        Assert.Equal("Private shared body.", sharedBody.Body);
    }

    [Fact]
    public void Lazy_skill_descriptors_stay_small_no_matter_the_workflow_size()
    {
        using var fixture = TempRoot.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru"));
        File.WriteAllText(Path.Combine(fixture.Root, "AGENTS.md"), new string('r', 64 * 1024), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "AGENTS.md"), new string('a', 64 * 1024), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture.Root, ".ru", "CONTEXT.md"), new string('c', 64 * 1024), new UTF8Encoding(false));
        var skills = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills")).FullName;
        WriteSkill(skills, "first", "# First\n\n" + new string('f', 65_000));
        WriteSkill(skills, "second", "# Second\n\n" + new string('s', 65_000));

        var result = new RuInstructionLoader(localApplicationDataRoot: fixture.UserDataRoot).Load(fixture.Root);

        // Bounded metadata for both skills fits beside the fixed roots; neither body enters the prompt.
        Assert.Contains(result.Blocks, block => block.SourceId == "skill:catalog");
        Assert.DoesNotContain(result.Skips, skip => skip.Reason == "oversize");
        var first = result.Blocks.Single(block => block.SourceId == "skill:catalog");
        Assert.Contains("first", first.Content, StringComparison.Ordinal);
        Assert.Contains("second", first.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('f', 100), first.Content, StringComparison.Ordinal);
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
