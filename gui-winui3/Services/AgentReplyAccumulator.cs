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

    public AgentReplySnapshot? TakeSnapshot()
    {
        lock (_gate)
        {
            if (!_hasNewText || _sessionId is null || _runId is null) return null;
            _hasNewText = false;
            return new AgentReplySnapshot(_sessionId, _runId, _generation, _builder.ToString());
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
