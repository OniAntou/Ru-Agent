using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ComposerLocalStatusTests
{
    [Fact]
    public void Local_status_survives_empty_run_projection_until_explicitly_cleared()
    {
        var status = new ComposerLocalStatus();
        status.Set("Attachment was rejected");

        Assert.True(status.IsVisible);
        Assert.Equal("Attachment was rejected", status.Text);

        status.Clear();

        Assert.False(status.IsVisible);
        Assert.Equal(string.Empty, status.Text);
    }
}
