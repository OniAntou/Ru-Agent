using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;

namespace RuApp.Services;

public sealed class AgentPolicyController : INotifyPropertyChanged, IDisposable
{
    private readonly IAgentPolicyClient _client;
    private readonly Action<Action>? _synchronize;
    private readonly OperationStatus _status;

    public AgentPolicyController(IAgentPolicyClient client, Action<Action>? synchronize = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _synchronize = synchronize;
        _status = new OperationStatus(RunOnUi, "Policies not loaded.");
        _status.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName!);
    }

    public ObservableCollection<AgentPolicyModel> Policies { get; } = new();

    // The busy flag and status line live in OperationStatus, shared with the run
    // controller so both present one shape to the view.
    public bool IsBusy => _status.IsBusy;

    public string StatusText => _status.StatusText;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _status.RunAsync(
            "Loading policies...",
            async () => await _client.ListPoliciesAsync(cancellationToken: cancellationToken).ConfigureAwait(false),
            items =>
            {
                var models = new List<AgentPolicyModel>();
                foreach (var element in items)
                {
                    if (AgentPolicyModel.TryParse(element, out var model))
                    {
                        models.Add(model);
                    }
                }

                RunOnUi(() =>
                {
                    Policies.Clear();
                    foreach (var model in models)
                    {
                        Policies.Add(model);
                    }
                });

                return $"Loaded {models.Count} rules.";
            },
            ex => $"Load error: {ex.Message}").ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _status.RunAsync(
            $"Deleting {id}...",
            async () =>
            {
                // A button click is an explicit interactive confirmation. Global policy
                // deletion is rejected by the API unless this bit is propagated.
                await _client.DeletePolicyAsync(id, interactive: true, cancellationToken).ConfigureAwait(false);
                return true;
            },
            _ =>
            {
                RunOnUi(() =>
                {
                    for (var i = 0; i < Policies.Count; i++)
                    {
                        if (Policies[i].Id == id)
                        {
                            Policies.RemoveAt(i);
                            break;
                        }
                    }
                });

                return "Rule deleted.";
            },
            ex => $"Delete error: {ex.Message}").ConfigureAwait(false);
    }

    public async Task CreateAsync(AgentPolicyModel model, CancellationToken cancellationToken = default)
    {
        var matcher = model.Matcher;
        if (matcher.ValueKind != JsonValueKind.Object || !matcher.EnumerateObject().Any())
        {
            // The compact panel has one free-form field. Treat a tool-shaped value
            // as tool_id and otherwise send an empty matcher (the API accepts a
            // wildcard rule); never send the obsolete legacy fields.
            matcher = string.IsNullOrWhiteSpace(model.Description)
                || !model.Description.Contains('.', StringComparison.Ordinal)
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : JsonDocument.Parse(JsonSerializer.Serialize(new { tool_id = model.Description })).RootElement.Clone();
        }

        var body = new Dictionary<string, object?>
        {
            ["scope"] = model.Scope,
            ["decision"] = model.Kind,
            ["matcher"] = matcher,
        };

        await _status.RunAsync(
            $"Creating {model.Id}...",
            async () =>
            {
                // Creating a rule is an explicit action in the policy panel. This is
                // required for global rules and is safe for project-scoped rules.
                await _client.CreatePolicyAsync(body, interactive: true, cancellationToken).ConfigureAwait(false);
                return true;
            },
            _ =>
            {
                RunOnUi(() => Policies.Add(model));
                return "Rule created.";
            },
            ex => $"Create error: {ex.Message}").ConfigureAwait(false);
    }

    private void RunOnUi(Action action)
    {
        if (_synchronize is not null)
        {
            _synchronize(action);
        }
        else
        {
            action();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
    }
}
