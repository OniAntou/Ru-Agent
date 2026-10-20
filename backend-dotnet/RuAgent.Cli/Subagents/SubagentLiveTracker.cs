using RuAgent.Cli.Tui;
using RuAgent.Client;

namespace RuAgent.Cli.Subagents;

public enum SubagentPhase
{
    Running,
    Waiting,
    Done,
    Failed,
    Stopped,
}

/// <summary>One child as the header strip shows it.</summary>
public sealed record SubagentChip(string AgentId, SubagentPhase Phase);

/// <summary>A change worth a transcript line: <see cref="From"/> is null when the child first appears.</summary>
public sealed record SubagentTransition(string AgentId, SubagentPhase? From, SubagentPhase To);

/// <summary>
/// Follows the live <c>subagent.changed</c> hints of one root run. Every hint carries the whole
/// child set, so the tracker only compares it with the previous one: a missed hint costs a missed
/// intermediate line, never a wrong strip. The first hint after attach or a new root is a baseline:
/// children that already finished are shown but not announced again.
/// </summary>
public sealed class SubagentLiveTracker
{
    private readonly Dictionary<string, SubagentPhase> _phases = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private string? _root;

    public IReadOnlyList<SubagentChip> Chips => _order.Select(id => new SubagentChip(id, _phases[id])).ToArray();

    public bool IsEmpty => _order.Count == 0;

    public int ActiveCount => _phases.Values.Count(phase => phase is SubagentPhase.Running or SubagentPhase.Waiting);

    public void Reset()
    {
        _phases.Clear();
        _order.Clear();
        _root = null;
    }

    public static SubagentPhase Classify(AgentSubagentState state)
    {
        if (state.Canceled)
        {
            return SubagentPhase.Stopped;
        }

        return state.Status.ToLowerInvariant() switch
        {
            "completed" => SubagentPhase.Done,
            "failed" or "interrupted" => SubagentPhase.Failed,
            "cancelled" => SubagentPhase.Stopped,
            "waiting_approval" or "waiting_user" or "waiting_input" => SubagentPhase.Waiting,
            _ => SubagentPhase.Running,
        };
    }

    public static bool IsActive(SubagentPhase phase) => phase is SubagentPhase.Running or SubagentPhase.Waiting;

    public IReadOnlyList<SubagentTransition> Apply(AgentSubagentChangeModel change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var baseline = !string.Equals(_root, change.RootRunId, StringComparison.Ordinal);
        if (baseline)
        {
            Reset();
            _root = change.RootRunId;
        }

        var transitions = new List<SubagentTransition>();
        foreach (var agent in change.Agents)
        {
            var phase = Classify(agent);
            if (_phases.TryGetValue(agent.AgentId, out var previous))
            {
                if (previous != phase)
                {
                    transitions.Add(new SubagentTransition(agent.AgentId, previous, phase));
                }
            }
            else
            {
                _order.Add(agent.AgentId);
                if (!baseline || IsActive(phase))
                {
                    transitions.Add(new SubagentTransition(agent.AgentId, null, phase));
                }
            }

            _phases[agent.AgentId] = phase;
        }

        return transitions;
    }

    /// <summary>
    /// Host-generated ids are <c>agent_</c> plus 32 hex digits, too long for a header chip.
    /// They show as <c>agent·893a67</c>; every other id (a name the model chose) shows as is.
    /// </summary>
    public static string DisplayName(string agentId)
    {
        const string prefix = "agent_";
        var safe = ChatDisplayText.Sanitize(agentId, 64);
        return safe.Length == prefix.Length + 32
            && safe.StartsWith(prefix, StringComparison.Ordinal)
            && safe.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0
                ? "agent·" + safe.Substring(prefix.Length, 6)
                : safe;
    }

    public static string Glyph(SubagentPhase phase) => phase switch
    {
        SubagentPhase.Running => "◐",
        SubagentPhase.Waiting => "◇",
        SubagentPhase.Done => "✓",
        SubagentPhase.Failed => "✗",
        _ => "○",
    };

    public static TuiToken Tone(SubagentPhase phase) => phase switch
    {
        SubagentPhase.Running => TuiToken.Brand,
        SubagentPhase.Waiting => TuiToken.Warn,
        SubagentPhase.Done => TuiToken.Ok,
        SubagentPhase.Failed => TuiToken.Err,
        _ => TuiToken.Meta,
    };

    public static string Verb(SubagentPhase phase) => phase switch
    {
        SubagentPhase.Running => "running",
        SubagentPhase.Waiting => "waiting",
        SubagentPhase.Done => "finished",
        SubagentPhase.Failed => "failed",
        _ => "stopped",
    };

    /// <summary>
    /// The transcript line for a transition, painted for the colour level in force. A finished
    /// child points at the command that reads its result.
    /// </summary>
    public static string TransitionLine(SubagentTransition transition)
    {
        var glyph = TuiPalette.Paint(Glyph(transition.To), Tone(transition.To));
        var name = TuiPalette.Panel(DisplayName(transition.AgentId));
        var verb = transition.From is null && transition.To == SubagentPhase.Running
            ? "started"
            : Verb(transition.To);
        var line = $"{glyph} subagent {name} {TuiPalette.Quiet(verb)}";
        return transition.To == SubagentPhase.Done
            ? line + TuiPalette.Quiet($"  ·  /subagents {ChatDisplayText.Sanitize(transition.AgentId, 64)} result")
            : line;
    }

    /// <summary>
    /// Header chips that fit <paramref name="width"/> cells. Active children come first, then a
    /// "+N" tail for those that do not fit, so the strip never overflows its row.
    /// </summary>
    public static IReadOnlyList<SubagentChipSpan> Fit(IReadOnlyList<SubagentChip> chips, int width)
    {
        if (chips.Count == 0 || width < 6)
        {
            return [];
        }

        var ordered = chips
            .OrderBy(chip => IsActive(chip.Phase) ? 0 : 1)
            .ToArray();
        var spans = new List<SubagentChipSpan>();
        var everything = ordered.Sum(chip => TuiText.VisibleWidth(Glyph(chip.Phase) + " " + DisplayName(chip.AgentId)))
            + 2 * (ordered.Length - 1);
        var used = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var chip = ordered[index];
            var text = Glyph(chip.Phase) + " " + DisplayName(chip.AgentId);
            var cost = TuiText.VisibleWidth(text) + (spans.Count > 0 ? 2 : 0);
            var remaining = ordered.Length - index;
            var tail = remaining > 1 ? TuiText.VisibleWidth($"  +{remaining - 1}") : 0;
            if (used + cost + (everything <= width || remaining == 1 ? 0 : tail) > width)
            {
                var hidden = ordered.Length - index;
                var more = $"+{hidden}";
                if (spans.Count > 0 && used + 2 + more.Length <= width)
                {
                    spans.Add(new SubagentChipSpan("  " + more, TuiToken.Meta));
                }
                else if (spans.Count == 0)
                {
                    var fitted = TuiText.Truncate(text, width);
                    spans.Add(new SubagentChipSpan(fitted, Tone(chip.Phase)));
                }

                return spans;
            }

            if (spans.Count > 0)
            {
                spans.Add(new SubagentChipSpan("  ", TuiToken.Meta));
            }

            spans.Add(new SubagentChipSpan(text, Tone(chip.Phase)));
            used += cost;
        }

        return spans;
    }
}

public sealed record SubagentChipSpan(string Text, TuiToken Tone);
