using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class RuntimeMigrationModeTests
{
    [Fact]
    public void Missing_trusted_configuration_defaults_to_dotnet()
    {
        Assert.Equal(RuntimeMigrationMode.Dotnet, RuntimeMigrationModeParser.Parse(null));
        Assert.Equal(RuntimeMigrationMode.Dotnet, RuntimeMigrationModeParser.Parse(""));
        Assert.Equal(RuntimeMigrationMode.Dotnet, RuntimeMigrationModeParser.Parse("dotnet"));
    }

    [Fact]
    public void Retired_python_selector_fails_closed_with_a_stable_code()
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => RuntimeMigrationModeParser.Parse("PythonCompatibility"));
        Assert.Equal("python_compatibility_retired", error.Message);
        Assert.Throws<ArgumentException>(() => RuntimeMigrationModeParser.Parse("automatic"));
    }

    [Fact]
    public void Dotnet_is_the_only_runtime_mode()
    {
        Assert.Equal(new[] { "Dotnet" }, Enum.GetNames<RuntimeMigrationMode>());
    }
}
