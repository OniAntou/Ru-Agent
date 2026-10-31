using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityDisplayPreferenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ru-activity-display-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_to_compact_and_remembers_the_choice_across_instances()
    {
        var preferences = new AgentActivityDisplayPreferences(new(_path));
        Assert.True(preferences.Compact);
        int changes = 0;
        preferences.PropertyChanged += (_, _) => changes++;
        preferences.Compact = false;
        preferences.Compact = false;
        Assert.Equal(1, changes);
        Assert.False(new AgentActivityDisplayPreferences(new(_path)).Compact);
        preferences.Compact = true;
        Assert.True(new AgentActivityDisplayPreferences(new(_path)).Compact);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"compact\":\"true\"}")]
    public void Invalid_preferences_fall_back_to_compact(string content)
    {
        File.WriteAllText(_path, content);
        Assert.True(new AgentActivityDisplayPreferenceStore(_path).LoadCompact());
    }

    [Fact]
    public void An_explicit_false_is_kept()
    {
        File.WriteAllText(_path, """{"compact":false}""");
        Assert.False(new AgentActivityDisplayPreferenceStore(_path).LoadCompact());
    }

    [Fact]
    public void Oversized_preferences_are_bounded_and_fall_back_to_compact()
    {
        File.WriteAllText(_path, "{\"compact\":true,\"ignored\":\"" + new string('x', 70 * 1024) + "\"}");
        Assert.True(new AgentActivityDisplayPreferenceStore(_path).LoadCompact());
    }

    public void Dispose() => File.Delete(_path);
}
