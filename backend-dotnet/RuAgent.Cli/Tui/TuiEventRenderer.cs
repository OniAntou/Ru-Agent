using System.Diagnostics;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Frame entry point for one interactive session. Parses SSE frames, hands them to the
/// transcript, and owns the live-row timer so the spinner never writes from its own
/// uncoordinated task.
/// </summary>
public sealed class TuiSessionRenderer
{
    private readonly TuiSurface _surface;
    private readonly TuiStatusLine _status;
    private readonly TuiTranscript _transcript;
    private CancellationTokenSource? _liveCancellation;
    private Task? _liveTask;
    private Stopwatch? _liveWatch;

    public TuiSessionRenderer(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _surface = new TuiSurface(writer)
        {
            SuppressRepaints = Console.IsOutputRedirected || !ReferenceEquals(writer, Console.Out),
        };
        _status = new TuiStatusLine(_surface);
        _transcript = new TuiTranscript(_surface, _status);
    }

    public TuiSurface Surface => _surface;

    public TuiStatusLine Status => _status;

    public TuiTranscript Transcript => _transcript;

    /// <summary>Current viewport width, refreshed from the console when one is attached.</summary>
    public int Width
    {
        get => _transcript.Width;
        set => _transcript.Width = value;
    }

    /// <summary>
    /// When set, the ticker asks the caller to repaint the live row (sticky footer) instead of
    /// repainting the row at the transcript cursor.
    /// </summary>
    public Action? LiveRowRepaintHook { get; set; }

    public void Reset()
    {
        StopLiveRow();
        _status.Reset();
        _status.SetState(TuiRunState.Idle);
    }

    public void BeginTurn()
    {
        _transcript.BeginTurn();
        _status.SetState(TuiRunState.Thinking);
        StartLiveRow();
    }

    public void EndTurn()
    {
        StopLiveRow();
        _transcript.EndTurn();
    }

    public void EndTurn(TuiTurnOutcome outcome, string? errorCode)
    {
        StopLiveRow();
        _transcript.EndTurn(outcome, errorCode);
    }

    public void Flush() => _transcript.Flush();

    public void ToggleExpand() => _transcript.ToggleExpand();

    /// <summary>Prints an operator-facing one-liner into the transcript.</summary>
    public void Note(string text, TuiToken token) => _transcript.Note(text, token);

    public void RenderFrame(SseFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.IsTransient)
        {
            RenderTransient(frame);
            return;
        }

        if (frame.DurableSequence is null)
        {
            return;
        }

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch (JsonException)
        {
            return;
        }

        if (envelope is null)
        {
            return;
        }

        RenderEnvelope(envelope);
    }

    private void RenderTransient(SseFrame frame)
    {
        if (AgentAssistantDeltaModel.TryParse(frame, out var delta) && delta is not null)
        {
            _transcript.AppendAssistant(delta.Text);
            return;
        }

        if (AgentActivityModel.TryParse(frame, out var activity) && activity is not null)
        {
            _transcript.ApplyActivity(activity);
            return;
        }

        if (TryParseRunSummary(frame, out var summary) && summary is not null)
        {
            _transcript.ApplySummary(summary);
        }
    }

    public void RenderEnvelope(HarnessSafeEventEnvelope envelope) => _transcript.ApplyDurable(envelope);

    /// <summary>Parses the transient <c>agent.summary</c> frame into its typed wire contract.</summary>
    public static bool TryParseRunSummary(SseFrame frame, out AgentRunSummaryWire? summary)
    {
        summary = null;
        if (!frame.IsTransient || string.IsNullOrWhiteSpace(frame.Data))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "agent.summary", StringComparison.Ordinal)
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var payload = data.GetString();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            summary = AgentRunSummaryWireCodec.Parse(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void StartLiveRow()
    {
        StopLiveRow();
        if (_surface.SuppressRepaints)
        {
            return;
        }

        _status.IsLive = true;
        _liveWatch = Stopwatch.StartNew();
        _liveCancellation = new CancellationTokenSource();
        var token = _liveCancellation.Token;

        _liveTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _status.Tick();
                    _status.SetElapsed(_liveWatch.Elapsed.TotalSeconds);
                    if (LiveRowRepaintHook is { } hook)
                    {
                        hook();
                    }
                    else
                    {
                        _status.Render(Width);
                    }

                    await Task.Delay(120, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Turn ended; the row is cleared by StopLiveRow.
            }
        }, token);
    }

    private void StopLiveRow()
    {
        if (_liveCancellation is null)
        {
            return;
        }

        _liveCancellation.Cancel();
        try
        {
            _liveTask?.Wait(200);
        }
        catch (AggregateException)
        {
            // Cancellation raced the loop; the row state below is authoritative.
        }

        _liveCancellation.Dispose();
        _liveCancellation = null;
        _liveTask = null;
        _liveWatch = null;
        _status.Clear(Width);
    }
}

public static class TuiEventRenderer
{
    public static void RenderFrame(SseFrame frame, TextWriter writer)
    {
        var renderer = new TuiSessionRenderer(writer);
        renderer.RenderFrame(frame);
    }

    public static void RenderEnvelope(HarnessSafeEventEnvelope envelope, TextWriter writer)
    {
        var renderer = new TuiSessionRenderer(writer);
        renderer.RenderEnvelope(envelope);
    }
}
