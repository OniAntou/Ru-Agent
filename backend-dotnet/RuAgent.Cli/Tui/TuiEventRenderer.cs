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

    public TuiSessionRenderer(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _surface = new TuiSurface(writer);
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

    public void Reset()
    {
        _status.Reset();
        _status.SetState(TuiRunState.Idle);
    }

    public void BeginTurn()
    {
        if (_surface.Writer is ITranscriptTurnRewriter screen) screen.BeginAssistantTurn();
        _transcript.BeginTurn();
        _status.SetState(TuiRunState.Thinking);
    }

    public void EndTurn() => _transcript.EndTurn();

    public void EndTurn(TuiTurnOutcome outcome, string? errorCode) => _transcript.EndTurn(outcome, errorCode);

    public void Flush() => _transcript.Flush();

    public TuiActivityPresentation PresentActivity(int width)
    {
        if (_status.State is TuiRunState.Thinking or TuiRunState.Tool)
        {
            _status.SetElapsed(_transcript.ElapsedSeconds);
        }
        return _status.Presentation(width);
    }

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

        if (envelope.ProtocolVersion != HarnessSafeEventEnvelope.CurrentProtocolVersion)
        {
            // The Host and CLI agree on one envelope version; rendering an unknown shape
            // would be guesswork. Name the mismatch instead of silently dropping the turn.
            _transcript.Note(
                $"unsupported_protocol_version: {envelope.ProtocolVersion} (expected {HarnessSafeEventEnvelope.CurrentProtocolVersion})",
                TuiToken.Err);
            return;
        }

        RenderEnvelope(envelope);
    }

    private void RenderTransient(SseFrame frame)
    {
        if (AgentAssistantDeltaModel.TryParse(frame, out var delta) && delta is not null)
        {
            if (delta.IsReset)
            {
                // A retried attempt supersedes the text this step already streamed: the
                // rows it painted leave the screen before the retry lands (a row-marking
                // writer only — a raw console cannot unprint).
                _surface.RewindAssistantStep(delta.ModelStepId);
                _transcript.ResetAssistant();
                return;
            }

            _surface.BeginAssistantStep(delta.ModelStepId);
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
