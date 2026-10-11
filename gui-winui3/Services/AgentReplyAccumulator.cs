using System;
using System.Collections.Generic;
using System.Text;

namespace RuApp.Services;

public sealed record AgentReplySnapshot(
    string SessionId, string RunId, long Generation, string Text);

public sealed class AgentReplyAccumulator
{
    private readonly object _gate = new();
    private string? _sessionId;
    private string? _runId;
    private long _generation;
    private readonly StringBuilder _builder = new();
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);
    private bool _hasNewText;

    public void Begin(string sessionId, string runId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _runId = runId;
            _generation++;
            _builder.Clear();
            _seenKeys.Clear();
            _hasNewText = false;
        }
    }

    public bool TryAppend(string runId, string modelStepId, long sequence, string text)
    {
        if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(modelStepId) || text is null) return false;
        lock (_gate)
        {
            if (_runId is null || _sessionId is null) return false;
            if (!string.Equals(_runId, runId, StringComparison.Ordinal)) return false;
            var key = $"{runId}:{modelStepId}:{sequence}";
            if (!_seenKeys.Add(key)) return false;
            _builder.Append(text);
            _hasNewText = true;
            return true;
        }
    }

    /// <summary>
    /// Drops the text streamed so far for the active run. A model step retried
    /// after a transient failure re-runs on a fresh socket, and its answer
    /// replaces the partial one that had already been rendered.
    /// </summary>
    public bool TryReset(string runId, string modelStepId, long sequence)
    {
        if (string.IsNullOrEmpty(runId) || string.IsNullOrEmpty(modelStepId)) return false;
        lock (_gate)
        {
            if (_runId is null || _sessionId is null) return false;
            if (!string.Equals(_runId, runId, StringComparison.Ordinal)) return false;
            var key = $"{runId}:{modelStepId}:{sequence}";
            if (!_seenKeys.Add(key)) return false;
            _builder.Clear();
            // Report the empty snapshot so the view clears the superseded text
            // instead of leaving it on screen until the retry writes again.
            _hasNewText = true;
            return true;
        }
    }

    public AgentReplySnapshot? TakeSnapshot()
    {
        lock (_gate)
        {
            if (!_hasNewText || _sessionId is null || _runId is null) return null;
            _hasNewText = false;
            return new AgentReplySnapshot(_sessionId, _runId, _generation, _builder.ToString());
        }
    }

    public string? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _sessionId;
            }
        }
    }

    public bool HasSession => SessionId is not null;

    public void Rebind(string sessionId, string runId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(runId))
        {
            return;
        }

        lock (_gate)
        {
            if (_sessionId is null || !string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            // The live view binds every frame. Rebinding the same run must not
            // forget delivered deltas or replayed reset frames.
            if (string.Equals(_runId, runId, StringComparison.Ordinal)) return;

            _runId = runId;
            _generation++;
            _seenKeys.Clear();
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _builder.Clear();
            _seenKeys.Clear();
            _hasNewText = false;
            _sessionId = null;
            _runId = null;
        }
    }
}
