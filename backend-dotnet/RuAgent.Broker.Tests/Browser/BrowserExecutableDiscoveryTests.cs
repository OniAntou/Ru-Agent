using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

public sealed class BrowserExecutableDiscoveryTests
{
    [Theory]
    [InlineData("firefox.exe", BrowserFamily.Firefox)]
    [InlineData("Firefox.EXE", BrowserFamily.Firefox)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", BrowserFamily.Firefox)]
    [InlineData("chrome.exe", BrowserFamily.Chromium)]
    [InlineData("msedge.exe", BrowserFamily.Chromium)]
    [InlineData("brave.exe", BrowserFamily.Chromium)]
    [InlineData("vivaldi.exe", BrowserFamily.Chromium)]
    [InlineData("opera.exe", BrowserFamily.Chromium)]
    [InlineData("chromium.exe", BrowserFamily.Chromium)]
    public void Classifies_by_file_name(string path, BrowserFamily family) =>
        Assert.Equal(family, BrowserExecutableDiscovery.Classify(path));

    [Fact]
    public void An_explicit_installed_executable_is_resolved_and_classified()
    {
        var chromium = BrowserExecutableDiscovery.FindChromiumExecutable();
        if (chromium is not null)
        {
            Assert.Equal(
                new BrowserTarget(Path.GetFullPath(chromium), BrowserFamily.Chromium),
                BrowserExecutableDiscovery.Resolve(chromium));
        }

        var firefox = BrowserExecutableDiscovery.FindFirefoxExecutable();
        if (firefox is not null)
        {
            Assert.Equal(BrowserFamily.Firefox, BrowserExecutableDiscovery.Resolve(firefox)!.Family);
        }
    }

    [Fact]
    public void Installed_candidates_are_real_files_and_chromium_wins_over_firefox()
    {
        var installed = BrowserExecutableDiscovery.Installed();
        Assert.All(installed, target => Assert.True(File.Exists(target.ExecutablePath), target.ExecutablePath));
        var firstFirefox = installed.ToList().FindIndex(target => target.Family == BrowserFamily.Firefox);
        if (firstFirefox >= 0)
        {
            Assert.DoesNotContain(installed.Skip(firstFirefox), target => target.Family == BrowserFamily.Chromium);
        }

        var resolved = BrowserExecutableDiscovery.Resolve();
        if (resolved is not null && installed.Any(target => target.Family == BrowserFamily.Chromium))
        {
            Assert.Equal(BrowserFamily.Chromium, resolved.Family);
        }
    }

    [Fact]
    public void A_missing_preferred_path_falls_back_to_a_real_install_or_null()
    {
        var resolved = BrowserExecutableDiscovery.Resolve(@"C:\this\does\not\exist\firefox.exe");
        Assert.True(resolved is null || File.Exists(resolved.ExecutablePath));
    }
}
