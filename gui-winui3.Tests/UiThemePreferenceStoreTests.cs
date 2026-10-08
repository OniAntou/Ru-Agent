using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UiThemePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RuAppTests",
        $"theme-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_light_when_preference_file_is_missing()
    {
        var store = new UiThemePreferenceStore(Path.Combine(_root, "ui-preferences.json"));

        Assert.Equal(UiTheme.Light, store.Load());
    }

    [Fact]
    public void TrySave_and_load_round_trip_dark_theme()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        var store = new UiThemePreferenceStore(path);

        Assert.True(store.TrySave(UiTheme.Dark));
        Assert.Equal(UiTheme.Dark, store.Load());
        Assert.Contains("\"theme\":\"Dark\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TrySave_and_load_round_trip_meteor_shower_theme()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        var store = new UiThemePreferenceStore(path);

        Assert.True(store.TrySave(UiTheme.DarkMeteorShower));
        Assert.Equal(UiTheme.DarkMeteorShower, store.Load());
    }

    [Fact]
    public void TrySave_and_load_round_trip_starry_night_theme()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        var store = new UiThemePreferenceStore(path);

        Assert.True(store.TrySave(UiTheme.StarryNight));
        Assert.Equal(UiTheme.StarryNight, store.Load());
    }

    [Fact]
    public void Load_folds_the_removed_light_meteor_theme_to_light()
    {
        // The Light Meteor Shower theme was removed by owner direction; a
        // preference saved while it existed must not resurrect it.
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "{\"theme\":\"light-meteor\"}");

        Assert.Equal(UiTheme.Light, new UiThemePreferenceStore(path).Load());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"theme\":\"Sepia\"}")]
    [InlineData("{\"other\":true}")]
    public void Load_returns_light_for_invalid_or_unknown_preference(string content)
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, content);

        Assert.Equal(UiTheme.Light, new UiThemePreferenceStore(path).Load());
    }

    [Fact]
    public void Load_returns_light_without_allocating_an_oversized_preference_file()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, new byte[64 * 1024 + 1]);

        Assert.Equal(UiTheme.Light, new UiThemePreferenceStore(path).Load());
    }

    [Fact]
    public void TrySave_returns_false_when_target_path_is_a_directory()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(path);

        Assert.False(new UiThemePreferenceStore(path).TrySave(UiTheme.Dark));
    }
}
