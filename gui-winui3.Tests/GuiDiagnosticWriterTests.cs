using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class GuiDiagnosticWriterTests : IDisposable
{
    private readonly string _logRoot = Path.Combine(
        Path.GetTempPath(),
        "ru-gui-diagnostic-" + Guid.NewGuid().ToString("N"));

    public GuiDiagnosticWriterTests()
    {
        Directory.CreateDirectory(_logRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logRoot))
            {
                Directory.Delete(_logRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Writer_persists_safe_crash_event_under_explicit_log_root()
    {
        using var writer = new GuiDiagnosticWriter(_logRoot, capacity: 4);

        Assert.True(writer.TryEnqueueCrash(new InvalidOperationException("secret prompt and stack")));
        writer.Flush(TimeSpan.FromSeconds(2));

        var file = Assert.Single(Directory.GetFiles(_logRoot, "gui-*.jsonl"));
        var content = File.ReadAllText(file);
        Assert.Contains("gui_crash", content, StringComparison.Ordinal);
        Assert.Contains("gui_exception", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret prompt", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", content, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writer_persists_safe_voice_capture_failure_stage()
    {
        using var writer = new GuiDiagnosticWriter(_logRoot, capacity: 4);

        Assert.True(writer.TryEnqueueVoiceCaptureFailure(
            "device_input_access_denied",
            new InvalidOperationException("secret microphone details")));
        Assert.True(writer.Flush(TimeSpan.FromSeconds(2)));

        var file = Assert.Single(Directory.GetFiles(_logRoot, "gui-*.jsonl"));
        var content = File.ReadAllText(file);
        Assert.Contains("gui_voice_capture", content, StringComparison.Ordinal);
        Assert.Contains("device_input_access_denied", content, StringComparison.Ordinal);
        Assert.Contains("unknown", content, StringComparison.Ordinal);
        Assert.DoesNotContain("secret microphone details", content, StringComparison.OrdinalIgnoreCase);
    }
}
