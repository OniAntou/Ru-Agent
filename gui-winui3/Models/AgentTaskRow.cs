using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace RuApp.Models;

/// <summary>
/// One row of the side panel's background processes: a bounded projection of the
/// Host's <c>/tasks</c> command result, with local pending state for Stop.
/// </summary>
public sealed record AgentTaskRow : INotifyPropertyChanged
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

    public bool HasSameObservation(AgentTaskRow other) =>
        ExecutionId == other.ExecutionId && State == other.State && ExitCode == other.ExitCode && Command == other.Command;

    private bool _isStopping;
    public bool IsStopping
    {
        get => _isStopping;
        set
        {
            if (_isStopping == value) return;
            _isStopping = value;
            PropertyChanged?.Invoke(this, new(nameof(CanStop)));
            PropertyChanged?.Invoke(this, new(nameof(StopText)));
        }
    }
    public bool CanStop => State == "running" && !IsStopping;
    public string StopText => IsStopping ? "Stopping…" : "Stop";
    public event PropertyChangedEventHandler? PropertyChanged;

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
