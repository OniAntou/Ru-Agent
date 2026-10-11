using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// WinUI allows one ContentDialog per XamlRoot at a time. A second ShowAsync
/// while another dialog is open — or while the previous one's close animation is
/// still running — throws "An async operation was not properly started. Only a
/// single ContentDialog can be open at any time.", which the workspace-trust
/// path reported as a start failure even though the run was parked and healthy.
/// Every dialog therefore goes through <c>ContentDialogGate</c>, which serializes
/// them and retries that one condition briefly.
/// </summary>
public sealed class ContentDialogGateContractTests
{
    [Fact]
    public void Every_content_dialog_is_shown_through_the_shared_gate()
    {
        var sources = ShippedSources();
        var dialogSites = sources
            .Where(file => File.ReadAllText(file).Contains("new ContentDialog", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(dialogSites);
        foreach (var file in dialogSites)
        {
            Assert.Contains(
                "ContentDialogGate.ShowAsync",
                File.ReadAllText(file),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_site_calls_show_async_directly()
    {
        // The gate is the only place allowed to call ContentDialog.ShowAsync:
        // every other site must go through ContentDialogGate, which serializes
        // the dialogs and retries the single-dialog condition.
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?<!ContentDialogGate)\.ShowAsync\(");
        var offenders = ShippedSources()
            .Where(file => !file.EndsWith("ContentDialogGate.cs", StringComparison.Ordinal))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(System.IO.Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static System.Collections.Generic.List<string> ShippedSources() =>
        Directory
            .EnumerateFiles(GuiSource.Path(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !file.Contains(
                    $"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToList();
}
