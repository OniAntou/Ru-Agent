using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

/// <summary>
/// Ru's persistent browser profile (ADR 0055 amendment 2026-11-25) against real browsers: a
/// cookie set in one browser process is there after a clean exit and a new launch, and a second
/// Host cannot start the same profile at once. The profile root points at a temporary folder.
/// </summary>
[Collection(nameof(BrowserPersistentProfileTests))]
public sealed class BrowserPersistentProfileTests
{
    [BrowserFact]
    public Task Chromium_keeps_cookies_between_launches() =>
        KeepsCookiesAsync("chromium", () => new ChromiumBrowserEngine(persistentProfile: true));

    [FirefoxFact]
    public Task Firefox_keeps_cookies_between_launches() =>
        KeepsCookiesAsync("firefox", () => new FirefoxBrowserEngine(persistentProfile: true));

    [Fact]
    public async Task A_second_lease_on_the_same_profile_fails_by_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-profile-" + Guid.NewGuid().ToString("N"));
        BrowserProfileLease.PersistentRootOverride = root;
        try
        {
            await using var first = BrowserProfileLease.Persistent("chromium", ["DevToolsActivePort"]);
            Assert.True(first.IsPersistent);
            var error = Assert.Throws<BrowserEngineException>(() => BrowserProfileLease.Persistent("chromium", []));
            Assert.Equal("browser_profile_in_use", error.Code);
        }
        finally
        {
            BrowserProfileLease.PersistentRootOverride = null;
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task KeepsCookiesAsync(string family, Func<IBrowserEngine> create)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-profile-" + Guid.NewGuid().ToString("N"));
        BrowserProfileLease.PersistentRootOverride = root;
        await using var server = new BrowserPageActionsTests.PageServer();
        try
        {
            await using (var engine = create())
            {
                var page = await engine.OpenPageAsync(headless: true, CancellationToken.None);
                await engine.NavigateAsync(page, server.Url, CancellationToken.None);
                await engine.EvaluateAsync(page, "document.cookie = 'ru_keep=1; max-age=3600; path=/'", CancellationToken.None);
                Assert.Contains("ru_keep=1", await engine.EvaluateAsync(page, "document.cookie", CancellationToken.None), StringComparison.Ordinal);
            }

            Assert.True(Directory.Exists(Path.Combine(root, family)), "The persistent profile was deleted with the browser.");
            await using (var engine = create())
            {
                var page = await engine.OpenPageAsync(headless: true, CancellationToken.None);
                await engine.NavigateAsync(page, server.Url, CancellationToken.None);
                Assert.Contains("ru_keep=1", await engine.EvaluateAsync(page, "document.cookie", CancellationToken.None), StringComparison.Ordinal);
            }
        }
        finally
        {
            BrowserProfileLease.PersistentRootOverride = null;
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A browser helper may still hold a file for a moment; the temp folder is harmless.
            }
        }
    }
}

[CollectionDefinition(nameof(BrowserPersistentProfileTests), DisableParallelization = true)]
public sealed class BrowserPersistentProfileCollection;
