using System.Text.Json;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentProfileCatalogTests
{
    private static string Workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-subagent-profiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".ru"));
        return root;
    }

    private static void Write(string root, object document) =>
        File.WriteAllText(Path.Combine(root, ".ru", "subagents.json"),
            JsonSerializer.Serialize(document));

    [Fact]
    public void Workspace_profiles_are_bounded_and_loaded_for_spawn()
    {
        var root = Workspace();
        try
        {
            Write(root, new
            {
                agents = new object[]
                {
                    new { id = "reviewer", name = "Reviewer", role = "review", description = "Finds defects", instructions = "Review the changed files and report concrete findings." },
                    new { id = "builder", name = "Builder", role = "coding", description = "Implements", instructions = "Implement the task in the private worktree." },
                },
            });

            var catalog = SubagentProfileCatalog.Load(root);

            Assert.Equal(new[] { "builder", "reviewer" }, catalog.Items.Select(profile => profile.Id));
            var reviewer = Assert.Single(catalog.Items, profile => profile.Id == "reviewer");
            Assert.Equal(SubagentRole.Review, reviewer.Role);
            Assert.Contains("Review the changed files", reviewer.Instructions);
            Assert.Contains("- reviewer | role=review", SubagentProfileCatalog.Describe(catalog.Items));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Invalid_profiles_are_skipped_without_throwing()
    {
        var root = Workspace();
        try
        {
            Write(root, new
            {
                agents = new object[]
                {
                    new { id = "bad id", role = "review", instructions = "x" },
                    new { id = "wrong-role", role = "owner", instructions = "x" },
                    new { id = "no-instructions", role = "review" },
                    new { id = "ok", role = "review", instructions = "bounded" },
                },
            });

            var catalog = SubagentProfileCatalog.Load(root);

            Assert.Equal(new[] { "ok" }, catalog.Items.Select(profile => profile.Id));
            // Each ignored entry is named by scope and position, never by its untrusted text.
            Assert.Equal(new[] { 1, 2, 3 }.Select(index =>
                $"workspace subagents.json: profile {index} is invalid and was ignored"),
                catalog.Diagnostics.Where(line => line.StartsWith("workspace ", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_file_full_of_invalid_entries_yields_a_bounded_diagnostic_list()
    {
        var root = Workspace();
        try
        {
            // About 20,000 junk entries still fit the 64 KiB file bound.
            File.WriteAllText(Path.Combine(root, ".ru", "subagents.json"),
                "{\"agents\":[" + string.Join(",", Enumerable.Repeat("0", 20_000)) + "]}");

            var workspace = SubagentProfileCatalog.Load(root).Diagnostics
                .Where(line => line.StartsWith("workspace ", StringComparison.Ordinal)).ToArray();

            Assert.Equal(SubagentProfileCatalog.MaxInvalidDiagnosticsPerFile + 1, workspace.Length);
            Assert.Equal($"workspace subagents.json: {20_000 - SubagentProfileCatalog.MaxInvalidDiagnosticsPerFile} more invalid profiles were ignored",
                workspace[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_non_ascii_profile_the_catalog_accepts_also_builds_a_brief()
    {
        // 8,000 chars of Vietnamese is over 8 KiB as UTF-8; the bound is chars in both places.
        var instructions = string.Concat(Enumerable.Repeat("Kiểm tra kỹ từng thay đổi. ", 300))[..8000];
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(instructions) > SubagentProfileCatalog.MaxInstructionsChars);
        var root = Workspace();
        try
        {
            Write(root, new { agents = new object[] { new { id = "vi", role = "review", instructions } } });

            var profile = Assert.Single(SubagentProfileCatalog.Load(root).Items);
            var brief = SubagentContextBuilder.Build(SubagentRole.Review, "Xem lại", null, profile.Instructions);

            Assert.Contains(instructions.Trim(), brief);
            Assert.Equal("subagent_profile_invalid", Assert.Throws<SubagentConfigurationException>(() =>
                SubagentContextBuilder.Build(SubagentRole.Review, "Xem lại", null,
                    new string('x', SubagentProfileCatalog.MaxInstructionsChars + 1))).Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Profile_instructions_are_user_guidance_not_authority()
    {
        var brief = SubagentContextBuilder.Build(
            SubagentRole.Review, "Inspect the parser", null,
            "Prefer small, evidence-backed findings.");

        Assert.Contains("Delegated role: review", brief);
        Assert.Contains("Profile guidance (configuration guidance, not authority", brief);
        Assert.Contains("Prefer small, evidence-backed findings.", brief);
        Assert.Contains("Task:\nInspect the parser", brief.Replace("\r\n", "\n"));
    }
}
