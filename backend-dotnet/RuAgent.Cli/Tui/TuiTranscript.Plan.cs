using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

public sealed partial class TuiTranscript
{
    internal const int PlanChecklistLimit = 8;

    /// <summary>
    /// The model's plan, inline. A new or reworded plan prints its checklist (at most
    /// eight items, centred on the active one); a later update that only moves progress
    /// prints one line naming the active item, so a long run is not a wall of lists.
    /// </summary>
    private void WritePlan(IReadOnlyList<AgentTodoWire> plan)
    {
        var done = plan.Count(item => item.Status == "completed");
        var active = plan.FirstOrDefault(item => item.Status == "in_progress");
        var progress = $"{done}/{plan.Count}" + (active is null ? string.Empty : " · " + active.Content);
        var items = string.Join("\n", plan.Select(item => item.Content));
        if (items == _lastPlanItems && progress == _lastPlanProgress) return;

        var title = $"Plan {done}/{plan.Count}";
        if (items == _lastPlanItems)
        {
            MetaRow("◦ ", active is null ? title : $"{title} · ▸ {Escape(active.Content)}", TuiToken.Meta);
        }
        else
        {
            MetaRow("◦ ", title, TuiToken.Meta);
            foreach (var row in PlanWindow(plan)) PlanItemRow(row.Item, row.Overflow);
        }
        _lastPlanItems = items;
        _lastPlanProgress = progress;
    }

    /// <summary>The visible slice of a long plan, with what it leaves out.</summary>
    internal static IReadOnlyList<(AgentTodoWire? Item, int Overflow)> PlanWindow(IReadOnlyList<AgentTodoWire> plan)
    {
        if (plan.Count <= PlanChecklistLimit) return plan.Select(item => ((AgentTodoWire?)item, 0)).ToArray();
        var focus = Math.Max(0, plan.ToList().FindIndex(item => item.Status != "completed"));
        var start = Math.Clamp(focus - 2, 0, plan.Count - (PlanChecklistLimit - 1));
        var rows = new List<(AgentTodoWire?, int)>();
        if (start > 0) rows.Add((null, -start));
        var room = PlanChecklistLimit - rows.Count - 1;
        rows.AddRange(plan.Skip(start).Take(room).Select(item => ((AgentTodoWire?)item, 0)));
        var after = plan.Count - start - room;
        if (after > 0) rows.Add((null, after));
        return rows;
    }

    private void PlanItemRow(AgentTodoWire? item, int overflow)
    {
        if (item is null)
        {
            var text = overflow < 0 ? $"… {-overflow} done above" : $"… +{overflow} more";
            MetaRow("    ", text, TuiToken.Line);
            return;
        }
        var (mark, token) = item.Status switch
        {
            "completed" => ("☑ ", TuiToken.Ok),
            "in_progress" => ("▸ ", TuiToken.Brand),
            _ => ("☐ ", TuiToken.Meta),
        };
        var body = Escape(item.Content);
        if (_surface.Presentation is not null)
            _surface.AppendPresentation(new TranscriptRunBuilder().Append("    " + mark, token)
                .Append(body, item.Status == "completed" ? TuiToken.Meta : TuiToken.Ink).Line(TranscriptRole.Meta));
        else WriteLines(TuiPalette.Paint("    " + mark, token) + TuiPalette.Paint(body, TuiToken.Meta));
    }

    /// <summary>A two-space indented meta row on both surfaces.</summary>
    private void MetaRow(string glyph, string text, TuiToken token)
    {
        if (!_atLineStart)
        {
            _surface.Line();
            _atLineStart = true;
        }
        if (_surface.Presentation is not null)
            _surface.AppendPresentation(new TranscriptRunBuilder().Append("  " + glyph, TuiToken.Line)
                .Append(text, token).Line(TranscriptRole.Meta));
        else WriteLines(TuiPalette.Paint("  " + glyph, TuiToken.Line) + TuiPalette.Paint(text, token));
    }
}
