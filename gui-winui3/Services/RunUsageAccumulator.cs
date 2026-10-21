using System;
using System.Collections.Generic;
using System.Linq;
using RuApp.Models;

namespace RuApp.Services;

internal static class RunUsageAccumulator
{
    public static AgentModelUsage? Sum(IReadOnlyList<AgentModelUsage> reports)
    {
        if (reports.Count == 0) return null;
        int? SumField(Func<AgentModelUsage, int?> field) => reports.All(report => field(report) is not null)
            ? checked((int)reports.Sum(report => (long)field(report)!.Value)) : null;
        var models = reports.Select(report => report.Model).Distinct(StringComparer.Ordinal).ToArray();
        return new AgentModelUsage(SumField(report => report.InputTokensIncludeCache == true && report.CacheReadInputTokens is null
                ? null : report.UncachedInputTokens), SumField(report => report.OutputTokens),
            SumField(report => report.TotalTokens), SumField(report => report.CacheReadInputTokens),
            SumField(report => report.InputTokensIncludeCache == true ? report.CacheWriteInputTokens ?? 0 : report.CacheWriteInputTokens),
            models.Length == 1 ? models[0] : null,
            reports.All(report => report.InputTokensIncludeCache is not null) ? false : null)
        {
            PeakStepTokens = reports.Max(report => report.ResolvedTotal),
        };
    }
}
