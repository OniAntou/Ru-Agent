using RuAgent.Broker.Tools.LanguageServers;
using Xunit;

namespace RuAgent.Broker.Tests.LanguageServers;

public sealed class LanguageServerConfigurationTests
{
    [Fact]
    public void ParsesOnlyExplicitSupportedServers()
    {
        var config = LanguageServerConfiguration.Parse("""{"servers":{"csharp":{"command":"csharp-ls","args":["--stdio"]},"typescript":{"command":"typescript-language-server","args":["--stdio"]}}}""");

        Assert.Equal("csharp-ls", config.Find("csharp")?.Command);
        Assert.Equal("--stdio", config.Find("csharp")?.Arguments[0]);
        Assert.Null(config.Find("python"));
    }

    [Theory]
    [InlineData("""{"servers":{"python":{"command":"x","args":[]}}}""")]
    [InlineData("""{"servers":{"csharp":{"command":"x","args":[1]}}}""")]
    [InlineData("""{"servers":{"csharp":{"command":"x","args":[]},"csharp":{"command":"y","args":[]}}}""")]
    public void InvalidOrDuplicateConfigurationFailsClosed(string json)
    {
        Assert.Empty(LanguageServerConfiguration.Parse(json).Servers);
    }

    [Fact]
    public void ConfigurationPathIsUserOwned()
    {
        Assert.EndsWith(Path.Combine(".ru", "lsp.json"), LanguageServerConfiguration.PathFor("C:\\Users\\sample"));
    }
}
