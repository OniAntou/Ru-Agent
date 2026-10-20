using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RuApp.Models;

namespace RuApp.Services;

internal sealed record ProviderRowsProjection(
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<ProviderRow> Rows);

internal sealed record ProviderFilterProjection(
    IReadOnlyList<ProviderRow> VisibleRows,
    string HeaderText,
    bool HasNoMatches);

internal readonly record struct ProviderPickerLayout(double Height);

/// <summary>
/// Provider-list and provider-action decisions shared by the settings surface.
/// WinUI remains responsible for rendering rows and status feedback.
/// </summary>
internal static class SettingsProviderPresenter
{
    internal const string CustomChoice = "custom";
    internal const string CustomDisplayName = "Custom OpenAI Server";
    internal const double ProviderFlyoutBottomMargin = 24;
    internal const double ProviderFlyoutMaxHeight = 200;
    internal const double ProviderFlyoutAbsoluteMinHeight = 96;

    internal static ProviderRowsProjection BuildProviderRows(IEnumerable<(string Id, string Name)> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var names = ProviderSetupGuidance.OrderModelsDevOptions(providers)
            .ToDictionary(option => option.Id, option => option.Name, StringComparer.Ordinal);
        var rows = new List<ProviderRow>(names.Count + 1)
        {
            new(CustomChoice, CustomDisplayName),
        };
        rows.AddRange(names.Select(pair => new ProviderRow(
            ProviderSetupGuidance.ModelsDevPrefix + pair.Key,
            pair.Value)));
        rows.Sort(static (left, right) =>
        {
            int byName = string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
        });
        return new ProviderRowsProjection(names, rows);
    }

    internal static IReadOnlyList<ProviderRow> OrderSignedInFirst(IEnumerable<ProviderRow> rows) =>
        rows.OrderByDescending(row => row.HasKey)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static ProviderFilterProjection Filter(
        IReadOnlyList<ProviderRow> rows,
        string? filter,
        bool modelsDevLoadFailed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var visible = new List<ProviderRow>(rows.Count);
        foreach (var row in rows)
        {
            string id = ProviderSetupGuidance.IsModelsDevChoice(row.Value)
                ? row.Value[ProviderSetupGuidance.ModelsDevPrefix.Length..]
                : row.Value;
            if (ProviderSetupGuidance.MatchesProviderFilter(row.DisplayName, id, filter))
            {
                visible.Add(row);
            }
        }

        int matching = visible.Count;
        int total = rows.Count;
        string header = modelsDevLoadFailed && total <= 1
            ? $"PROVIDERS ({total}) - Models.dev list unavailable"
            : matching < total
                ? $"PROVIDERS ({matching} of {total})"
                : $"PROVIDERS ({total})";
        return new ProviderFilterProjection(visible, header, matching == 0);
    }

    internal static ProviderPickerLayout LayoutFor(double availableHeight)
    {
        double height = double.IsFinite(availableHeight)
            ? Math.Min(
                Math.Max(
                    availableHeight - ProviderFlyoutBottomMargin,
                    ProviderFlyoutAbsoluteMinHeight),
                ProviderFlyoutMaxHeight)
            : ProviderFlyoutMaxHeight;
        return new ProviderPickerLayout(height);
    }

    internal static bool IsSignedIn(string choice, bool customHasKey, IReadOnlyCollection<string> modelsDevKeyIds)
    {
        if (string.Equals(choice, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return customHasKey;
        }

        if (!ProviderSetupGuidance.IsModelsDevChoice(choice))
        {
            return false;
        }

        string id = choice[ProviderSetupGuidance.ModelsDevPrefix.Length..];
        return modelsDevKeyIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task<ProviderCatalogState?> TestConnectionAsync(
        Func<Task<bool>> savePendingSettings,
        Func<Task> refreshCatalog,
        Func<ProviderCatalogState?> readState)
    {
        if (!await savePendingSettings())
        {
            return null;
        }

        await refreshCatalog();
        return readState();
    }

    internal static async Task<bool> SignOutAsync(
        string choice,
        Func<string, Task<bool>> removeProvider,
        Action<string> clearKeyInput,
        Func<Task> reloadSettings)
    {
        if (string.IsNullOrWhiteSpace(choice) || !await removeProvider(choice))
        {
            return false;
        }

        clearKeyInput(choice);
        await reloadSettings();
        return true;
    }
}
