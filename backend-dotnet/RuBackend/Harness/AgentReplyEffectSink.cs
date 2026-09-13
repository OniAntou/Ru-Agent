using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuBackend.Business.Activity;

namespace RuBackend.Harness;

/// <summary>
/// Applies non-critical effects for a committed Agent reply. The bounded in-process
/// key set prevents reconnect/retry duplicates without making the activity log a
/// second source of truth for the durable run transition.
/// </summary>
public sealed class AgentReplyEffectSink : IAgentReplyEffectSink
{
    private const int MaximumAppliedKeys = 4_096;
    private readonly IActivityLogStore _activity;
    private readonly ConcurrentDictionary<string, byte> _applied = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _appliedOrder = new();

    public AgentReplyEffectSink(IActivityLogStore activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    public async ValueTask ApplyAsync(
        AgentRun run,
        AgentReplyPresentation reply,
        ModelStepId modelStepId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reply);
        _ = modelStepId.Value;
        var key = $"{run.RunId.Value}:{modelStepId.Value}";
        if (!_applied.TryAdd(key, 0))
        {
            return;
        }

        _appliedOrder.Enqueue(key);
        TrimAppliedKeys();
        try
        {
            await _activity.AppendAsync(
                    BuildActivity(run, reply, modelStepId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            _applied.TryRemove(key, out _);
            throw;
        }
    }

    private void TrimAppliedKeys()
    {
        while (_applied.Count > MaximumAppliedKeys
            && _appliedOrder.TryDequeue(out var oldest))
        {
            _applied.TryRemove(oldest, out _);
        }
    }

    private static ActivityEvent BuildActivity(
        AgentRun run,
        AgentReplyPresentation reply,
        ModelStepId modelStepId)
    {
        var data = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = reply.VisibleText,
        };
        if (!string.IsNullOrWhiteSpace(reply.Emotion))
        {
            data["emotion"] = reply.Emotion;
        }

        return new ActivityEvent(
            $"agent:{run.RunId.Value}:{modelStepId.Value}:assistant",
            0,
            string.Empty,
            "agent",
            run.SessionId.Value,
            data);
    }
}
