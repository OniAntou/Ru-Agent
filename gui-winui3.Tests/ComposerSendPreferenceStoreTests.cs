using System;
using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>What Enter does while a run is working (ADR 0056): steer unless Settings says queue.</summary>
public sealed class ComposerSendPreferenceStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ru-composer-prefs").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_directory, "composer.json");

    [Fact]
    public void A_missing_file_steers_by_default()
    {
        var store = new ComposerSendPreferenceStore(FilePath);

        Assert.Equal(ComposerWhileRunning.Steer, store.Load().WhileRunning);
        Assert.Equal(ComposerWhileRunning.Steer, store.Current.WhileRunning);
    }

    [Fact]
    public void A_saved_choice_reads_back_and_updates_the_cached_value()
    {
        var store = new ComposerSendPreferenceStore(FilePath);
        Assert.Equal(ComposerWhileRunning.Steer, store.Current.WhileRunning);

        Assert.True(store.TrySave(new ComposerSendPreferences(ComposerWhileRunning.Queue)));

        Assert.Equal(ComposerWhileRunning.Queue, store.Current.WhileRunning);
        Assert.Equal(ComposerWhileRunning.Queue, new ComposerSendPreferenceStore(FilePath).Load().WhileRunning);
        Assert.Contains("\"queue\"", File.ReadAllText(FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"whileRunning\":\"sideways\"}")]
    [InlineData("{\"whileRunning\":7}")]
    public void An_unreadable_or_unknown_choice_falls_back_to_steer(string content)
    {
        File.WriteAllText(FilePath, content);

        Assert.Equal(ComposerWhileRunning.Steer, new ComposerSendPreferenceStore(FilePath).Load().WhileRunning);
    }
}
