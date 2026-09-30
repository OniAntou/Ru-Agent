using RuAgent.Cli.Tui;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The free-tier rule exists twice: the TUI renderer's <c>TuiSurfaces.IsFreeModel</c>
/// and the GUI projection's <c>ProviderModelPickerProjection.IsFreeModel</c>. They are
/// separate code, so nothing but this test stops one from drifting; if they do, the
/// same model reads free on one surface and paid on the other.
/// </summary>
public sealed class FreeModelPredicateParityTests
{
    [Theory]
    [InlineData("x:free", true)]
    [InlineData("x:free ", true)]
    [InlineData("x:FREE", true)]
    [InlineData("x:Free", true)]
    [InlineData("  x:free  ", true)]
    [InlineData("x-model", false)]
    [InlineData("free", false)]
    [InlineData(":free ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void Tui_and_gui_classify_the_same_id_identically(string? id, bool expected)
    {
        Assert.Equal(expected, TuiSurfaces.IsFreeModel(id));
        Assert.Equal(expected, ProviderModelPickerProjection.IsFreeModel(id));
    }
}
