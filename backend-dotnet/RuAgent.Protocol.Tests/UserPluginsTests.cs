using System.Text.Json;
using Xunit;

namespace RuAgent.Protocol.Tests;

/// <summary>User plugins (ADR 0044): discovery, the strict manifest, the enabled set and creation.</summary>
public sealed class UserPluginsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-plugins-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "profile");
    private string AppData => Path.Combine(_root, "appdata");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_plugin_is_found_with_its_parts_and_starts_disabled()
    {
        var directory = WritePlugin(UserPlugins.LocalRoot(Profile), "team-rules", "Team rules");
        File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "Use pytest.");
        Directory.CreateDirectory(Path.Combine(directory, "skills", "release"));
        File.WriteAllText(Path.Combine(directory, "skills", "release", "SKILL.md"), "---\nname: release\ndescription: x\n---\n");

        var plugin = Assert.Single(UserPlugins.Discover(AppData, Profile));

        Assert.Equal("team-rules", plugin.Id);
        Assert.Equal(UserPluginSource.Local, plugin.Source);
        Assert.False(plugin.Enabled);
        Assert.True(plugin.HasSkills && plugin.HasInstructions);
        Assert.False(plugin.HasHooks || plugin.HasMcpServers);
        Assert.Empty(UserPlugins.EnabledSkillRoots(AppData, Profile));
    }

    [Fact]
    public void Enabling_adds_the_skill_folder_and_disabling_removes_it()
    {
        var directory = WritePlugin(UserPlugins.LocalRoot(Profile), "team-rules", "Team rules");
        Directory.CreateDirectory(Path.Combine(directory, "skills", "release"));

        UserPlugins.SetEnabled("team-rules", true, AppData);
        Assert.Equal([Path.Combine(directory, "skills")], UserPlugins.EnabledSkillRoots(AppData, Profile));

        UserPlugins.SetEnabled("team-rules", false, AppData);
        Assert.Empty(UserPlugins.EnabledSkillRoots(AppData, Profile));
    }

    [Theory]
    [InlineData("""{"id":"tools","name":"Tools","extra":true}""", "no other fields")]
    [InlineData("""{"id":"Tools","name":"Tools"}""", "lowercase")]
    [InlineData("""{"id":"other","name":"Tools"}""", "does not match the folder")]
    [InlineData("""{"id":"tools","name":""}""", "name must be")]
    [InlineData("""not json""", "not valid")]
    public void An_invalid_manifest_is_skipped_with_a_reason(string manifest, string reason)
    {
        var directory = Path.Combine(UserPlugins.LocalRoot(Profile), "tools");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UserPlugins.ManifestFileName), manifest);
        var skipped = new List<string>();

        Assert.Empty(UserPlugins.Discover(AppData, Profile, skipped));
        Assert.Contains(skipped, line => line.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_local_plugin_wins_over_an_installed_one_with_the_same_id()
    {
        WritePlugin(UserPlugins.LocalRoot(Profile), "tools", "Mine");
        WritePlugin(UserPlugins.ManagedRoot(AppData), "tools", "Installed");
        var skipped = new List<string>();

        var plugin = Assert.Single(UserPlugins.Discover(AppData, Profile, skipped));

        Assert.Equal("Mine", plugin.Manifest.Name);
        Assert.Single(skipped);
    }

    [Fact]
    public void Hooks_and_mcp_files_are_recognized()
    {
        var directory = WritePlugin(UserPlugins.LocalRoot(Profile), "runner", "Runner");
        File.WriteAllText(Path.Combine(directory, "hooks.json"), "{}");

        var plugin = Assert.Single(UserPlugins.Discover(AppData, Profile));

        Assert.True(plugin.HasHooks);
        Assert.False(plugin.HasMcpServers);
    }

    [Fact]
    public void Approvals_are_stored_per_plugin_and_survive_enabling_changes()
    {
        var digest = new string('a', 64);
        UserPlugins.SetApproval("runner", digest, AppData);
        UserPlugins.SetEnabled("runner", true, AppData);
        UserPlugins.SetEnabled("other", true, AppData);

        Assert.Equal(digest, UserPlugins.ReadApprovals(AppData)["runner"]);
        Assert.Contains("runner", UserPlugins.ReadEnabled(AppData));

        UserPlugins.SetApproval("runner", null, AppData);
        Assert.False(UserPlugins.ReadApprovals(AppData).ContainsKey("runner"));
        Assert.Contains("runner", UserPlugins.ReadEnabled(AppData));
        Assert.Throws<ArgumentException>(() => UserPlugins.SetApproval("runner", "not-a-digest", AppData));
    }

    [Fact]
    public void At_most_eight_enabled_plugins_add_skill_folders()
    {
        for (int index = 0; index < UserPlugins.MaxSkillPlugins + 2; index++)
        {
            var id = $"p{index:00}";
            var directory = WritePlugin(UserPlugins.LocalRoot(Profile), id, id);
            Directory.CreateDirectory(Path.Combine(directory, "skills", id));
            UserPlugins.SetEnabled(id, true, AppData);
        }

        Assert.Equal(UserPlugins.MaxSkillPlugins, UserPlugins.EnabledSkillRoots(AppData, Profile).Count);
    }

    [Fact]
    public void Unreadable_preferences_enable_nothing()
    {
        WritePlugin(UserPlugins.LocalRoot(Profile), "tools", "Tools");
        Directory.CreateDirectory(Path.GetDirectoryName(UserPlugins.PreferencesPath(AppData))!);
        File.WriteAllText(UserPlugins.PreferencesPath(AppData), "{broken");

        Assert.False(Assert.Single(UserPlugins.Discover(AppData, Profile)).Enabled);
    }

    [Fact]
    public void Create_scaffolds_a_valid_plugin_and_never_overwrites()
    {
        var plugin = UserPlugins.Create("team-rules", "Team rules", "Our conventions", withInstructions: true, withSkill: true, AppData, Profile);

        Assert.True(plugin.HasInstructions && plugin.HasSkills);
        Assert.True(File.Exists(Path.Combine(plugin.SkillsDirectory, "team-rules-example", "SKILL.md")));
        var manifest = UserPlugins.ReadManifest(plugin.Directory, out var problem);
        Assert.Equal("", problem);
        Assert.Equal("Our conventions", manifest!.Description);
        Assert.Throws<IOException>(() => UserPlugins.Create("team-rules", "Again", null, true, true, AppData, Profile));
        Assert.Throws<ArgumentException>(() => UserPlugins.Create("Bad Id", "x", null, true, true, AppData, Profile));
    }

    [Theory]
    [InlineData("tools", true)]
    [InlineData("a", true)]
    [InlineData("team-rules-2", true)]
    [InlineData("1tools", false)]
    [InlineData("tools-", false)]
    [InlineData("Tools", false)]
    [InlineData("this-id-is-much-too-long-x", false)]
    [InlineData(null, false)]
    public void Ids_leave_room_for_mcp_server_names(string? id, bool valid) =>
        Assert.Equal(valid, UserPlugins.IsValidId(id));

    [Fact]
    public void Import_copies_a_draft_into_the_local_root_and_leaves_it_off()
    {
        var draft = WritePlugin(Path.Combine(_root, "workspace", "ru-plugin-drafts"), "team-rules", "Team rules");
        File.WriteAllText(Path.Combine(draft, "AGENTS.md"), "Use pytest.");
        Directory.CreateDirectory(Path.Combine(draft, ".git"));
        File.WriteAllText(Path.Combine(draft, ".git", "HEAD"), "ref");

        var plugin = UserPlugins.Import(draft, AppData, Profile);

        var installed = Path.Combine(UserPlugins.LocalRoot(Profile), "team-rules");
        Assert.Equal(installed, plugin.Directory);
        Assert.False(plugin.Enabled);
        Assert.True(File.Exists(Path.Combine(installed, "AGENTS.md")));
        Assert.False(Directory.Exists(Path.Combine(installed, ".git")));
        Assert.True(File.Exists(Path.Combine(draft, "AGENTS.md")));
        Assert.Empty(UserPlugins.ReadEnabled(AppData));
        Assert.Single(Directory.GetDirectories(UserPlugins.LocalRoot(Profile)));
    }

    [Fact]
    public void Import_clears_an_earlier_enable_and_approval_for_the_same_id()
    {
        UserPlugins.SetEnabled("team-rules", true, AppData);
        UserPlugins.SetApproval("team-rules", new string('a', 64), AppData);
        var draft = WritePlugin(Path.Combine(_root, "drafts"), "team-rules", "Team rules");

        Assert.False(UserPlugins.Import(draft, AppData, Profile).Enabled);

        Assert.Empty(UserPlugins.ReadEnabled(AppData));
        Assert.False(UserPlugins.ReadApprovals(AppData).ContainsKey("team-rules"));
        Assert.Empty(UserPlugins.EnabledSkillRoots(AppData, Profile));
    }

    [Fact]
    public void Import_never_replaces_an_installed_plugin()
    {
        WritePlugin(UserPlugins.ManagedRoot(AppData), "team-rules", "Managed");
        var draft = WritePlugin(Path.Combine(_root, "drafts"), "team-rules", "Draft");

        Assert.Equal("plugin_exists", Assert.Throws<IOException>(() => UserPlugins.Import(draft, AppData, Profile)).Message);
        Assert.False(Directory.Exists(Path.Combine(UserPlugins.LocalRoot(Profile), "team-rules")));

        var local = WritePlugin(UserPlugins.LocalRoot(Profile), "other", "Other");
        Assert.Equal("plugin_import_already_installed",
            Assert.Throws<IOException>(() => UserPlugins.Import(local, AppData, Profile)).Message);
    }

    [Fact]
    public void Import_refuses_invalid_relative_and_oversized_drafts_without_writing()
    {
        var invalid = Path.Combine(_root, "drafts", "tools");
        Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, UserPlugins.ManifestFileName), """{"id":"other","name":"Tools"}""");
        Assert.StartsWith("plugin_import_invalid", Assert.Throws<InvalidDataException>(() => UserPlugins.Import(invalid, AppData, Profile)).Message);
        Assert.Throws<ArgumentException>(() => UserPlugins.Import(@"drafts\tools", AppData, Profile));
        Assert.Throws<ArgumentException>(() => UserPlugins.Import(Path.Combine(_root, "missing"), AppData, Profile));

        var large = WritePlugin(Path.Combine(_root, "drafts"), "large", "Large");
        for (var index = 0; index < UserPlugins.MaxImportFiles; index++)
            File.WriteAllText(Path.Combine(large, $"note-{index}.md"), "x");
        Assert.Equal("plugin_import_too_large", Assert.Throws<IOException>(() => UserPlugins.Import(large, AppData, Profile)).Message);

        Assert.False(Directory.Exists(UserPlugins.LocalRoot(Profile)) && Directory.EnumerateFileSystemEntries(UserPlugins.LocalRoot(Profile)).Any());
    }

    private static string WritePlugin(string root, string id, string name)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, UserPlugins.ManifestFileName),
            JsonSerializer.Serialize(new { id, name }));
        return directory;
    }
}
