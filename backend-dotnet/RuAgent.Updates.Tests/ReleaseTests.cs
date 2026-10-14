using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Updates;
using Xunit;

namespace RuAgent.Updates.Tests;

public sealed class ReleaseTests
{
    [Theory]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-preview.01")]
    [InlineData("1.2")]
    [InlineData("1.2.3\n")]
    public void Rejects_invalid_versions(string value) => Assert.Null(ReleaseVersion.Parse(value));

    [Fact]
    public void Orders_semver_and_ignores_build_metadata()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1"];
        for (int i = 1; i < ordered.Length; i++) Assert.True(ReleaseVersion.Parse(ordered[i])!.CompareTo(ReleaseVersion.Parse(ordered[i-1])) > 0);
        Assert.Equal(0, ReleaseVersion.Parse("v1.0.0+build")!.CompareTo(ReleaseVersion.Parse("1.0.0")));
    }

    [Fact]
    public void Stable_skips_preview_and_drafts_and_preview_picks_highest_version()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("2.0.0-preview.1", true), Release("1.1.0"), Release("3.0.0", draft:true), Release("1.0.1") }));
        var current = ReleaseVersion.Parse("1.0.0")!;
        Assert.Equal("1.1.0", ReleaseUpdateClient.Select(json.RootElement, UpdateProduct.Cli, current, UpdateChannel.Stable)!.Version);
        Assert.Equal("2.0.0-preview.1", ReleaseUpdateClient.Select(json.RootElement, UpdateProduct.Cli, current, UpdateChannel.Preview)!.Version);
        Assert.Null(ReleaseUpdateClient.Select(json.RootElement, UpdateProduct.Desktop, current, UpdateChannel.Preview));
    }

    [Theory]
    [InlineData("", "update_digest_missing")]
    [InlineData("md5:fake", "update_digest_missing")]
    public void Missing_digest_fails_closed(string digest, string code)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("1.1.0", digest:digest) }));
        Assert.Equal(code, Assert.Throws<UpdateException>(() => ReleaseUpdateClient.Select(json.RootElement, UpdateProduct.Cli, ReleaseVersion.Parse("1.0.0")!, UpdateChannel.Stable)).Code);
    }

    [Theory]
    [InlineData("http://github.com/OniAntou/Ru-Agent/releases/download/v1.0.0/a.exe")]
    [InlineData("https://github.com/other/Ru/releases/download/v1.0.0/a.exe")]
    [InlineData("https://github.com.evil.test/a.exe")]
    [InlineData("https://user@github.com/OniAntou/Ru-Agent/releases/download/v1.0.0/a.exe")]
    public void Download_rejects_untrusted_origins(string value) => Assert.False(ReleaseUpdateClient.IsTrustedDownloadUri(new Uri(value)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_verifies_digest_and_never_leaves_partial_files(bool corrupt)
    {
        byte[] bytes = [1,2,3,4];
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var client = new ReleaseUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? [4,3,2,1] : bytes) }));
        var directory = Path.Combine(Path.GetTempPath(), "ru-update-test-" + Guid.NewGuid().ToString("N"));
        var update = new ReleaseUpdate("1.1.0", "Ru-CLI-1.1.0-win-x64-Setup.exe", new Uri("https://github.com/OniAntou/Ru-Agent/releases/download/v1.1.0/Ru-CLI-1.1.0-win-x64-Setup.exe"), hash, 4, new Uri("https://github.com/OniAntou/Ru-Agent/releases/tag/v1.1.0"));
        try
        {
            if (corrupt) Assert.Equal("update_digest_mismatch", (await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(update, directory, null, default))).Code);
            else Assert.Equal(bytes, await File.ReadAllBytesAsync((await client.DownloadAsync(update, directory, null, default)).InstallerPath));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
            if (corrupt) Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Preferences_default_to_stable_and_persist_preview()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-update-pref-" + Guid.NewGuid().ToString("N"));
        try { var preferences = new UpdatePreferences(root); Assert.Equal(UpdateChannel.Stable, preferences.LoadChannel()); preferences.SaveChannel(UpdateChannel.Preview); Assert.Equal(UpdateChannel.Preview, new UpdatePreferences(root).LoadChannel()); File.WriteAllText(Path.Combine(root,"channel.txt"), "unexpected"); Assert.Equal(UpdateChannel.Stable, preferences.LoadChannel()); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static object Release(string version, bool preview = false, bool draft = false, string? digest = null) => new {
        tag_name = "v" + version, prerelease = preview, draft,
        assets = new[] { new { name = $"Ru-CLI-{version}-win-x64-Setup.exe", size = 4,
            digest = digest ?? "sha256:" + new string('a',64), browser_download_url = $"https://github.com/OniAntou/Ru-Agent/releases/download/v{version}/Ru-CLI-{version}-win-x64-Setup.exe" } }
    };
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request)); }
}
