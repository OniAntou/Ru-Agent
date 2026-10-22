using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProtectedPathCatalogTests
{
    [Fact]
    public void Catalog_version_is_locked_at_3()
    {
        Assert.Equal("3", ProtectedPathCatalog.Version);
    }

    [Theory]
    [InlineData(@"C:\ws\repo\.ru\hooks.json")]
    [InlineData(@"C:\ws\repo\.RU\Hooks.JSON")]
    [InlineData(@"C:\ws\repo\.ru\hooks.json.")]
    [InlineData(@"C:\ws\repo\.ru.\hooks.json")]
    [InlineData(@"C:\Users\u\.ru\hooks.json")]
    [InlineData(@"C:\Users\u\.ru\mcp.json")]
    public void Ru_configuration_that_runs_programs_is_protected(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Theory]
    [InlineData(@"C:\ws\repo\hooks.json")]
    [InlineData(@"C:\ws\repo\config\hooks.json")]
    [InlineData(@"C:\ws\repo\.ru\settings.json")]
    [InlineData(@"C:\ws\repo\.ru\skills\deploy\SKILL.md")]
    [InlineData(@"C:\ws\repo\.rust\hooks.json")]
    public void Other_files_named_like_ru_configuration_stay_editable(string path)
    {
        Assert.False(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\agent_harness.db")]
    [InlineData(@"D:\data\Ru\agent_harness.db-wal")]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\harness_key.dat")]
    [InlineData(@"C:\Users\u\AppData\Local\Ru\runtime_descriptor.json")]
    [InlineData(@"D:\tmp\sessions\broker.json")]
    public void Harness_database_key_and_descriptor_locations_are_protected(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path));
    }

    [Theory]
    [InlineData(@"C:\ws\repo\.git")]
    [InlineData(@"C:\ws\repo\.git\config")]
    [InlineData(@"C:\ws\repo\.git\objects\ab\cd1234")]
    [InlineData(@"C:\ws\repo\.env")]
    [InlineData(@"C:\ws\repo\.env.local")]
    [InlineData(@"C:\ws\repo\sub\.env.production")]
    [InlineData(@"C:\Users\u\.ssh\id_rsa")]
    [InlineData(@"C:\Users\u\.aws\credentials")]
    [InlineData(@"D:\srv\.git-credentials")]
    [InlineData(@"D:\notes\.netrc")]
    public void Developer_and_credential_paths_are_ordinary_targets(string path)
    {
        // Mainstream agents read .git/.env/.ssh through normal permission rules
        // instead of a hard deny list, so these paths follow the preset profile.
        Assert.False(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Fact]
    public void Env_file_named_without_prefix_is_not_protected()
    {
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\environment.txt"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\src\main.cpp"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"C:\ws\repo\README.md"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_not_classified_protected(string? path)
    {
        Assert.False(ProtectedPathCatalog.IsProtected(path!));
    }

    [Fact]
    public void Workspace_root_itself_is_not_protected_by_catalog_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-protected-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(ProtectedPathCatalog.IsProtected(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(@"D:\project\src\credentials.json")]
    [InlineData(@"D:\project\CredentialStore.cs")]
    [InlineData(@"D:\work\credential-rotation-notes.md")]
    public void Files_merely_named_like_credentials_stay_editable(string path)
    {
        Assert.False(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Theory]
    [InlineData(@"D:\srv\runtime_descriptor.json.")]
    [InlineData(@"D:\srv\runtime_descriptor.json ")]
    [InlineData(@"D:\srv\harness_key.dat.")]
    [InlineData(@"D:\srv\AGENT_HARNESS.db")]
    public void Trailing_dots_and_spaces_cannot_alias_protected_names(string path)
    {
        // Win32 drops trailing dots and spaces when it opens an existing name, so
        // `runtime_descriptor.json.` is `runtime_descriptor.json`; the catalog
        // must normalize before matching.
        Assert.True(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Fact]
    public void Extra_paths_from_the_environment_join_the_hard_deny_list()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ProtectedPathCatalog.ExtraPathsEnvironmentVariable] =
                @"C:\secrets; D:\work\.env;",
        };

        Assert.True(ProtectedPathCatalog.IsProtected(
            @"C:\secrets\api.txt",
            name => environment.TryGetValue(name, out var value) ? value : null));
        Assert.True(ProtectedPathCatalog.IsProtected(
            @"D:\work\.env",
            name => environment.TryGetValue(name, out var value) ? value : null));
        Assert.False(ProtectedPathCatalog.IsProtected(
            @"C:\secrets-notes\readme.md",
            name => environment.TryGetValue(name, out var value) ? value : null));
        Assert.False(ProtectedPathCatalog.IsProtected(
            @"D:\work\src\program.cs",
            name => environment.TryGetValue(name, out var value) ? value : null));
    }

    [Fact]
    public void Extra_path_entries_that_are_not_paths_are_ignored()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ProtectedPathCatalog.ExtraPathsEnvironmentVariable] = "not a path\0;",
        };

        Assert.False(ProtectedPathCatalog.IsProtected(
            @"C:\ws\repo\src\main.cpp",
            name => environment.TryGetValue(name, out var value) ? value : null));
    }

    [Fact]
    public void Public_surface_without_environment_still_covers_name_rules()
    {
        Assert.True(ProtectedPathCatalog.IsProtected(@"D:\srv\harness_key.dat"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"D:\project\src\credentials.json"));
    }
}
