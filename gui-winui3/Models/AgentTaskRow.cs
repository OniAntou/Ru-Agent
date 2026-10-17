using System;
using Microsoft.UI.Xaml;

namespace RuApp.Models;

/// <summary>
/// One row of the side panel's background processes: a bounded projection of the
/// Host's <c>/tasks</c> command result. Rows are rebuilt on refresh, so the
/// properties are immutable.
/// </summary>
public sealed record AgentTaskRow
{
    private const int ShortIdLength = 18;

    public AgentTaskRow(string executionId, string state, int? exitCode, string command = "")
    {
        ExecutionId = executionId;
        State = state;
        ExitCode = exitCode;
        Command = command;
    }

    public string ExecutionId { get; }

    public string State { get; }

    public int? ExitCode { get; }
    public string Command { get; }

    public string Label => string.IsNullOrWhiteSpace(Command) ? ShortId(ExecutionId) + " · " + State : Command;

    public string DetailText => string.IsNullOrWhiteSpace(Command)
        ? ExitCode is { } exit ? $"exit {exit}" : string.Empty
        : State + " · " + ShortId(ExecutionId) + (ExitCode is { } code ? $" · exit {code}" : "");

    public Visibility StopVisibility => string.Equals(State, "running", StringComparison.Ordinal)
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static string ShortId(string executionId) =>
        executionId.Length <= ShortIdLength ? executionId : executionId[..ShortIdLength] + "…";
}
