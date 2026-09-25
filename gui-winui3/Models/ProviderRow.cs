using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace RuApp.Models;

/// <summary>
/// One row of the provider picker, projected into the list's item template.
/// </summary>
/// <remarks>
/// Rows are data, not elements. The catalog is ~190 entries, and rendering one Button
/// per entry in a StackPanel failed with 0x800F1000 partway through, which silently
/// truncated the picker in the C's. A virtualized ListView materializes only the rows
/// on screen, so the whole catalog is reachable.
///
/// The type is public and top-level on purpose: the item template binds with
/// <c>{Binding}</c>, which resolves through reflection at runtime and can only see
/// public members of a public type. Nested inside the view it bound to nothing and
/// every row rendered blank.
/// </remarks>
public sealed class ProviderRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _hasKey;

    public ProviderRow(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    /// <summary>The stored backend choice, e.g. <c>custom</c> or <c>models.dev:openai</c>.</summary>
    public string Value { get; }

    /// <summary>The name the picker shows for this provider.</summary>
    public string DisplayName { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value, nameof(IsVisible), nameof(RowVisibility));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(
            ref _isSelected,
            value,
            nameof(IsSelected),
            nameof(CheckVisibility),
            nameof(NameWeight),
            nameof(NameOpacity));
    }

    public bool HasKey
    {
        get => _hasKey;
        set => Set(ref _hasKey, value, nameof(HasKey), nameof(KeyDotVisibility));
    }

    public Visibility RowVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility KeyDotVisibility => HasKey ? Visibility.Visible : Visibility.Collapsed;

    public Windows.UI.Text.FontWeight NameWeight => IsSelected ? FontWeights.SemiBold : FontWeights.Normal;

    public double NameOpacity => IsSelected ? 1.0 : 0.88;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, params string[] changed)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        foreach (string property in changed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
