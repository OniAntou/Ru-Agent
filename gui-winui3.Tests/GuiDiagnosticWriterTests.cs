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
    public void A_crash_names_its_exception_type_and_frames_in_the_trace_but_never_its_message()
    {
        // Fifty E_INVALIDARG fail-fasts were logged as "unknown" with nothing else, so
        // the throwing layout handler could not be found. The event stays content-free;
        // the trace line carries the type and the code frames, written synchronously.
        ArgumentException thrown;
        try
        {
            ThrowFromNamedFrame();
            throw new InvalidOperationException("unreachable");
        }
        catch (ArgumentException error)
        {
            thrown = error;
        }

        Assert.True(GuiDiagnosticWriter.WriteCrashEvent(thrown, _logRoot));

        var events = File.ReadAllText(Assert.Single(Directory.GetFiles(_logRoot, "gui-2*.jsonl")));
        Assert.Contains("\"exception_kind\":\"argument\"", events, StringComparison.Ordinal);
        Assert.DoesNotContain("ArgumentException", events, StringComparison.Ordinal);

        var trace = File.ReadAllText(Path.Combine(_logRoot, "gui-trace.jsonl"));
        Assert.Contains("[Crash] System.ArgumentException hresult=0x80070057", trace, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowFromNamedFrame), trace, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", trace, StringComparison.Ordinal);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowFromNamedFrame() =>
        throw new ArgumentException(@"bad path C:\Users\someone\secret.txt");
}
