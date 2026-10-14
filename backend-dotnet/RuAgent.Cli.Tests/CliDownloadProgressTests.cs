using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class CliDownloadProgressTests
{
    [Fact]
    public void Redirected_output_has_no_frames_or_control_characters()
    {
        using var writer = new StringWriter();
        using (var progress = new CliDownloadProgress(writer, 10_000, false))
        { progress.Report(0); progress.Report(0.5); progress.Report(1); }
        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void Progress_is_bounded_monotonic_and_finishes_on_its_own_line()
    {
        using var writer = new StringWriter();
        using (var progress = new CliDownloadProgress(writer, 10 * 1024 * 1024, true))
        {
            progress.Report(double.NaN); progress.Report(0.5);
            progress.Report(0.2); progress.Report(2);
        }
        Assert.Contains("50%", writer.ToString());
        Assert.Contains("100%", writer.ToString());
        Assert.DoesNotContain("20%", writer.ToString());
        Assert.EndsWith(Environment.NewLine, writer.ToString());
    }
}
