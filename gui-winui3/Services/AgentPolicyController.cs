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
    private readonly object _gate = new();

    private bool _isBusy;
    private string _statusText = "Chưa tải chính sách.";

    public AgentPolicyController(IAgentPolicyClient client, Action<Action>? synchronize = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _synchronize = synchronize;
    }

    public ObservableCollection<AgentPolicyModel> Policies { get; } = new();

    public bool IsBusy
    {
        get { lock (_gate) return _isBusy; }
        private set { lock (_gate) { _isBusy = value; } OnPropertyChanged(nameof(IsBusy)); }
    }

    public string StatusText
    {
        get { lock (_gate) return _statusText; }
        private set { lock (_gate) { _statusText = value; } OnPropertyChanged(nameof(StatusText)); }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        RunOnUi(() => { IsBusy = true; StatusText = "Đang tải chính sách..."; });
        try
        {
            var items = await _client.ListPoliciesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
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

                StatusText = $"Đã tải {models.Count} quy tắc.";
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => { StatusText = $"Lỗi tải: {ex.Message}"; });
            throw;
        }
        finally
        {
            RunOnUi(() => { IsBusy = false; });
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        RunOnUi(() => { IsBusy = true; StatusText = $"Đang xóa {id}..."; });
        try
        {
            // A button click is an explicit interactive confirmation. Global policy
            // deletion is rejected by the API unless this bit is propagated.
            await _client.DeletePolicyAsync(id, interactive: true, cancellationToken).ConfigureAwait(false);
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

                StatusText = "Đã xóa quy tắc.";
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => { StatusText = $"Lỗi xóa: {ex.Message}"; });
            throw;
        }
        finally
        {
            RunOnUi(() => { IsBusy = false; });
        }
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

        RunOnUi(() => { IsBusy = true; StatusText = $"Đang tạo {model.Id}..."; });
        try
        {
            // Creating a rule is an explicit action in the policy panel. This is
            // required for global rules and is safe for project-scoped rules.
            await _client.CreatePolicyAsync(body, interactive: true, cancellationToken).ConfigureAwait(false);
            RunOnUi(() =>
            {
                Policies.Add(model);
                StatusText = "Đã tạo quy tắc.";
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => { StatusText = $"Lỗi tạo: {ex.Message}"; });
            throw;
        }
        finally
        {
            RunOnUi(() => { IsBusy = false; });
        }
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
