using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class HarnessEventSinkTests
{
    [Fact]
    public void Event_sink_rejects_audio_transcript_and_secret_fields()
    {
        using var events = new InMemoryHarnessEventSink();

        Assert.Throws<ArgumentException>(() => events.Write("voice", new { audio = "bytes" }));
        Assert.Throws<ArgumentException>(() => events.Write("voice", new { transcript = "private" }));
        Assert.Throws<ArgumentException>(() => events.Write("voice", new { token = "secret" }));
    }

    [Fact]
    public void Event_sink_accepts_only_allowlisted_scalar_events()
    {
        using var events = new InMemoryHarnessEventSink();

        events.Write("voice.capture.started", new { count = 1 });

        Assert.Equal(new[] { "voice.capture.started" }, events.Codes);
    }
}
