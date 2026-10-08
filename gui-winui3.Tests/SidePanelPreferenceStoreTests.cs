using System;
using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidePanelPreferenceStoreTests
{
    [Fact]
    public void Save_then_load_round_trips_the_width()
    {
        var path = TempPath();
        try
        {
            Assert.True(new SidePanelPreferenceStore(path).TrySave(420));
            Assert.Equal(420, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_missing_file_reads_as_the_default()
    {
        var path = TempPath();
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
    }

    [Fact]
    public void A_garbage_file_reads_as_the_default()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void An_oversized_file_reads_as_the_default()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string(' ', 65 * 1024));
            Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Stored_widths_are_clamped_on_save_and_load()
    {
        var path = TempPath();
        try
        {
            Assert.True(new SidePanelPreferenceStore(path).TrySave(9_999));
            Assert.Equal(AgentSidePanelPolicy.MaximumWidth, new SidePanelPreferenceStore(path).Load());

            File.WriteAllText(path, "{\"width\":10}");
            Assert.Equal(AgentSidePanelPolicy.MinimumWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Non_finite_widths_are_refused()
    {
        var store = new SidePanelPreferenceStore(TempPath());
        Assert.False(store.TrySave(double.NaN));
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ru-side-panel-tests", Guid.NewGuid().ToString("N"), "side-panel.json");

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
