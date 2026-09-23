using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProtectedPathCatalogTests
{
    [Fact]
    public void Catalog_version_is_locked_at_1()
    {
        Assert.Equal("1", ProtectedPathCatalog.Version);
    }

    [Theory]
    [InlineData(@"C:\ws\repo\.git")]
    [InlineData(@"C:\ws\repo\.git\config")]
    [InlineData(@"C:\ws\repo\.git\objects\ab\cd1234")]
    [InlineData(@"C:\ws\repo\.env")]
    [InlineData(@"C:\ws\repo\.env.local")]
    [InlineData(@"C:\ws\repo\sub\.env.production")]
    public void Git_and_env_paths_are_protected(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path));
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
        // The old rule blocked every path *containing* the substring
        // "credential", which stopped legitimate work and never covered the
        // stores that actually hold secrets.
        Assert.False(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Theory]
    [InlineData(@"C:\Users\u\.ssh\config")]
    [InlineData(@"C:\Users\u\.ssh\keys\work")]
    [InlineData(@"C:\Users\u\AppData\Roaming\Microsoft\Protect\S-1-5\AAB")]
    [InlineData(@"C:\Users\u\AppData\Local\Microsoft\Credentials\cvid\c")]
    [InlineData(@"C:\Users\u\.aws\credentials")]
    [InlineData(@"C:\Users\u\.kube\config")]
    public void Anchored_credential_stores_are_protected(string path)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = @"C:\Users\u",
            ["APPDATA"] = @"C:\Users\u\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local",
        };
        Assert.True(ProtectedPathCatalog.IsProtected(path, name =>
            env.TryGetValue(name, out var value) ? value : null));
    }

    [Theory]
    [InlineData(@"D:\notes\.netrc")]
    [InlineData(@"D:\srv\.git-credentials")]
    [InlineData(@"C:\deploy\id_ed25519")]
    public void Credential_bearing_file_names_are_protected_anywhere(string path)
    {
        Assert.True(ProtectedPathCatalog.IsProtected(path, _ => null));
    }

    [Fact]
    public void Public_surface_without_environment_still_covers_name_rules()
    {
        Assert.True(ProtectedPathCatalog.IsProtected(@"D:\srv\.git-credentials"));
        Assert.False(ProtectedPathCatalog.IsProtected(@"D:\project\src\credentials.json"));
    }
}
