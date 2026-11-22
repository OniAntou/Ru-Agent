using RuAgent.Broker.Tools.Browser;
using Xunit;

namespace RuAgent.Broker.Tests.Browser;

public sealed class AutoBrowserEngineTests
{
    [AnyBrowserFact]
    public async Task Auto_engine_opens_a_real_browser_and_evaluates()
    {
        await using var engine = new AutoBrowserEngine();
        Assert.True(engine.IsAvailable);
        var page = await engine.OpenPageAsync(headless: true, CancellationToken.None);
        try
        {
            Assert.Equal("2", await engine.EvaluateAsync(page, "1+1", CancellationToken.None));
        }
        finally
        {
            await engine.ClosePageAsync(page, CancellationToken.None);
        }
    }
}

/// <summary>A fact that skips when neither a Chromium browser nor Firefox is installed.</summary>
public sealed class AnyBrowserFactAttribute : FactAttribute
{
    public AnyBrowserFactAttribute()
    {
        if (BrowserExecutableDiscovery.Resolve() is null)
        {
            Skip = "No supported Chromium or Firefox installation is available.";
        }
    }
}
