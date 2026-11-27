using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol;
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
    // A false entry means the step has streamed. A true entry means its narration is settled.
    private readonly BoundedInsertionMap<string, bool> _narrationRows = new(64, StringComparer.Ordinal);
    private readonly StringBuilder _stepText = new();
    private string? _streamingStepId;
    private string? _streamingNarrationId;

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
        _narrationRows.Clear();
        _stepText.Clear();
        _streamingStepId = null;
        _streamingNarrationId = null;
        if (_surface.Writer is ITranscriptTurnRewriter screen) screen.BeginAssistantTurn();
        _transcript.BeginTurn();
        _status.SetState(_transcript.FullScreenCards ? TuiRunState.WaitingModel : TuiRunState.Thinking);
    }

    public void EndTurn() => _transcript.EndTurn();

    public void EndTurn(TuiTurnOutcome outcome, string? errorCode) => _transcript.EndTurn(outcome, errorCode);

    public void Flush() => _transcript.Flush();

    public TuiActivityPresentation PresentActivity(int width)
    {
        if (_status.IsWorking)
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
            // would be guesswork. Name the mismatch in human terms; the versions stay in the
            // debug log rather than in the transcript.
            TuiDebug.Log("protocol",
                $"unsupported envelope version {envelope.ProtocolVersion} (expected {HarnessSafeEventEnvelope.CurrentProtocolVersion})");
            _transcript.Note(
                "this terminal is out of date with the running backend · update Ru, then retry",
                TuiToken.Err);
            return;
        }

        RenderEnvelope(envelope);
    }

    private void RenderTransient(SseFrame frame)
    {
        if (AgentAssistantDeltaModel.TryParse(frame, out var delta) && delta is not null)
        {
            if (_surface.Presentation is not null && !TrackAssistantDelta(delta)) return;
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
            if (_surface.Presentation is not null && activity.Kind == AgentActivityKind.Narration)
                RenderNarration(activity);
            else
                _transcript.ApplyActivity(activity);
            return;
        }

        if (TryParseRunSummary(frame, out var summary) && summary is not null)
        {
            _transcript.ApplySummary(summary);
        }
    }

    private bool TrackAssistantDelta(AgentAssistantDeltaModel delta)
    {
        if (_streamingStepId != delta.ModelStepId)
        {
            _streamingStepId = delta.ModelStepId;
            _streamingNarrationId = AgentActivityIdentity.NarrationForStep(delta.ModelStepId);
            _stepText.Clear();
        }

        var narrationId = _streamingNarrationId!;
        var tracked = _narrationRows.TryGetValue(narrationId, out var settled);
        if (settled) return false;
        if (delta.IsReset)
        {
            _narrationRows.Remove(narrationId);
            _stepText.Clear();
            return true;
        }

        if (!tracked) _narrationRows[narrationId] = false;
        var remaining = AgentActivityWireValidator.MaximumDetailLength - _stepText.Length;
        if (remaining > 0)
            _stepText.Append(delta.Text.AsSpan(0, Math.Min(remaining, delta.Text.Length)));
        return true;
    }

    private void RenderNarration(AgentActivityModel activity)
    {
        var text = TuiText.SanitizeUntrusted(activity.Detail ?? string.Empty).Trim();
        if (text.Length == 0) return;
        var streamed = _narrationRows.TryGetValue(activity.ActivityId, out var settled);
        if (settled) return;
        _narrationRows[activity.ActivityId] = true;
        if (streamed)
        {
            // Late activity for an earlier step cannot alter the current step's prose.
            if (activity.ActivityId != _streamingNarrationId) return;
            var received = TuiText.SanitizeUntrusted(_stepText.ToString()).TrimStart();
            if (received.TrimEnd() == text
                || text.EndsWith('…') && received.StartsWith(text[..^1].TrimEnd(), StringComparison.Ordinal))
            {
                _transcript.Flush();
                return;
            }

            if (received.Length > 0 && text.StartsWith(received, StringComparison.Ordinal))
            {
                _transcript.AppendAssistant(text[received.Length..]);
                _transcript.Flush();
                return;
            }

            // A gap in the middle of the stream leaves no safe suffix to append.
            // Replace this step's partial prose with the settled narration row.
            _surface.RewindAssistantStep(_streamingStepId);
            _transcript.DiscardAssistantTail();
        }
        _transcript.ApplyActivity(activity);
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
