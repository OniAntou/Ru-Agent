using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityDisplayPreferenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ru-activity-display-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_to_detailed_and_remembers_compact_across_instances()
    {
        var preferences = new AgentActivityDisplayPreferences(new(_path));
        Assert.False(preferences.Compact);
        int changes = 0;
        preferences.PropertyChanged += (_, _) => changes++;
        preferences.Compact = true;
        preferences.Compact = true;
        Assert.Equal(1, changes);
        Assert.True(new AgentActivityDisplayPreferences(new(_path)).Compact);
        preferences.Compact = false;
        Assert.False(new AgentActivityDisplayPreferences(new(_path)).Compact);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"compact\":\"true\"}")]
    public void Invalid_preferences_fall_back_to_detailed(string content)
    {
        File.WriteAllText(_path, content);
        Assert.False(new AgentActivityDisplayPreferenceStore(_path).LoadCompact());
    }

    [Fact]
    public void Oversized_preferences_are_bounded_and_fall_back_to_detailed()
    {
        File.WriteAllText(_path, "{\"compact\":true,\"ignored\":\"" + new string('x', 70 * 1024) + "\"}");
        Assert.False(new AgentActivityDisplayPreferenceStore(_path).LoadCompact());
    }

    public void Dispose() => File.Delete(_path);
}
