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
    public void Python_compatibility_requires_an_explicit_selector()
    {
        Assert.Equal(RuntimeMigrationMode.PythonCompatibility, RuntimeMigrationModeParser.Parse("PythonCompatibility"));
        Assert.Throws<ArgumentException>(() => RuntimeMigrationModeParser.Parse("automatic"));
    }
}
