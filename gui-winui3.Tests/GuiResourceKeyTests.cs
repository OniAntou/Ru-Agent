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
    public void The_option_row_style_is_application_scoped_because_two_views_use_it()
    {
        // This overturns an earlier decision, so the reason is worth stating.
        //
        // PickerItemButtonStyle was deliberately view-scoped, and the rule behind it was
        // sound: a style with one user belongs to that user, and reading a view-scoped key
        // from the application dictionary is what took the whole chat surface down once.
        // The reason no longer applies. The reasoning list is the second user, it is
        // built in code rather than markup, and leaving the style view-scoped would mean
        // the two lists are the same design only until someone edits one of them.
        //
        // The crash half of the old hazard is still covered: the test above walks every
        // Application.Current.Resources lookup in code and fails on a key App.xaml does
        // not declare, so "put it in the view and read it from the app" cannot come back.
        string appXaml = GuiSource.Text("App.xaml");

        Assert.Contains("x:Key=\"RuOptionRowStyle\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "x:Key=\"PickerItemButtonStyle\"",
            appXaml,
            StringComparison.Ordinal);

        // Both users read it from the application dictionary, and neither keeps a private
        // copy that could drift.
        Assert.Contains("resources[\"RuOptionRowStyle\"]", GuiSource.Text("Views", "ProviderModelPicker.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("resources[\"RuOptionRowStyle\"]", GuiSource.Text("Views", "ChatView.Models.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PickerItemButtonStyle",
            GuiSource.Text("Views", "ProviderModelPicker.xaml.cs"),
            StringComparison.Ordinal);
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
