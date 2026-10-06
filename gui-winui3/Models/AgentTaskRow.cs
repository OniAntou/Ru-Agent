using System;
using Microsoft.UI.Xaml;

namespace RuApp.Models;

/// <summary>
/// One row of the composer's background-task flyout: a bounded projection of the
/// Host's <c>/tasks</c> command result. Rows are rebuilt on refresh, so the
/// properties are immutable.
/// </summary>
public sealed class AgentTaskRow
{
    private const int ShortIdLength = 18;

    public AgentTaskRow(string executionId, string state, int? exitCode)
    {
        ExecutionId = executionId;
        State = state;
        ExitCode = exitCode;
    }

    public string ExecutionId { get; }

    public string State { get; }

    public int? ExitCode { get; }

    public string Label => ShortId(ExecutionId) + " · " + State;

    public string DetailText => ExitCode is { } exit ? $"exit {exit}" : string.Empty;

    public Visibility StopVisibility => string.Equals(State, "running", StringComparison.Ordinal)
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static string ShortId(string executionId) =>
        executionId.Length <= ShortIdLength ? executionId : executionId[..ShortIdLength] + "…";
}
