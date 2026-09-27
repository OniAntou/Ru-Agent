using System.Text.RegularExpressions;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Every resource key a view reads from <c>Application.Current.Resources</c> has to
/// exist in App.xaml.
/// </summary>
/// <remarks>
/// A view-scoped key read from the application dictionary throws
/// <see cref="KeyNotFoundException"/>, and where that happens decides how much the
/// app breaks. PickerItemButtonStyle is declared in ProviderModelPicker.xaml's own
/// resources; reading it from the application dictionary threw during construction,
/// so the control failed to load and the whole chat surface came up blank while the
/// sidebar kept rendering. No test caught it because the test project exercises the
/// projection, not the live control tree.
///
/// A key declared in a control's own ResourceDictionary has to be read from that
/// control's <c>Resources</c>. This test closes the application-dictionary half of
/// that seam.
/// </remarks>
public sealed class GuiResourceKeyTests
{
    private static readonly Regex ApplicationLookup =
        new(@"Application\.Current\.Resources\[""([^""]+)""\]", RegexOptions.Compiled);

    [Fact]
    public void Every_application_resource_key_read_in_code_is_declared_in_app_xaml()
    {
        string appXaml = GuiSource.Text("App.xaml");
        var declared = ResourceKeys.From(appXaml);

        var root = GuiSource.Path();
        var sources = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (string source in sources)
        {
            foreach (Match match in ApplicationLookup.Matches(File.ReadAllText(source)))
            {
                string key = match.Groups[1].Value;
                if (declared.Contains(key))
                {
                    continue;
                }

                missing.Add(key);
                offenders.Add(
                    $"{Path.GetFileName(source)} reads \"{key}\" from Application.Current.Resources");
            }
        }

        Assert.True(
            missing.Count == 0,
            "not declared in App.xaml, so reading them throws KeyNotFoundException:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_picker_item_style_is_view_scoped_not_application_scoped()
    {
        // Pins the specific mistake: this style belongs to the control that uses it.
        string pickerXaml = GuiSource.Text("Views", "ProviderModelPicker.xaml");
        string pickerCode = GuiSource.Text("Views", "ProviderModelPicker.xaml.cs");

        Assert.Contains("x:Key=\"PickerItemButtonStyle\"", pickerXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"PickerItemButtonStyle\"",
            GuiSource.Text("App.xaml"),
            StringComparison.Ordinal);
        Assert.Contains("controlResources[\"PickerItemButtonStyle\"]", pickerCode, StringComparison.Ordinal);
    }

    private static class ResourceKeys
    {
        public static SortedSet<string> From(string xaml)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(xaml, "x:Key=\"([^\"]+)\""))
            {
                keys.Add(match.Groups[1].Value);
            }

            return keys;
        }
    }
}
