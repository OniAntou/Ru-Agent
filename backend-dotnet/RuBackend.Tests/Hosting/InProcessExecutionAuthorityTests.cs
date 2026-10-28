using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RuBackend.Business;
using RuBackend.Hosting;
using Xunit;

namespace RuBackend.Tests.Hosting;

/// <summary>
/// The in-process execution authority's composition decisions: the session
/// root stays the Broker child's <c>&lt;log root&gt;/agent/session</c>, the web
/// options parse the child's environment with the same fail-closed rules, the
/// workspace-root fallback keeps the child's chain, and the runtime itself is
/// composed lazily.
/// </summary>
public sealed class InProcessExecutionAuthorityTests : IDisposable
{
    [Theory]
    [InlineData("configured_json", "https://search.example.com/search?format=json", true)]
    [InlineData("configured_json", null, false)]
    [InlineData("unknown", "https://search.example.com", false)]
    public void ConfiguredSearchModeIsExplicitAndInvalidConfigurationDisablesWeb(string mode, string? endpoint, bool expected)
    {
        var options = InProcessExecutionAuthority.BuildWebOptions(name => name switch
        {
            "RU_AGENT_BROKER_WEB_ENABLED" => "true",
            "RU_AGENT_BROKER_WEB_SEARCH_MODE" => mode,
            "RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT" => endpoint,
            _ => null,
        }, NullLogger.Instance);
        Assert.Equal(expected, options is not null);
        if (options is not null) Assert.Equal("ConfiguredJson", options.SearchMode.ToString());
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-authority-" + Guid.NewGuid().ToString("N"));

    public InProcessExecutionAuthorityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SessionRootIsTheChildsAgentSessionRootAndIsCreated()
    {
        var logRoot = Path.Combine(_root, "logs");
        using var authority = new InProcessExecutionAuthority(
            BuildPaths(logRoot),
            NullLogger.Instance,
            spoolRoot: Path.Combine(_root, "spool"));

        Assert.Equal(Path.Combine(logRoot, "agent", "session"), authority.SessionRoot);
        Assert.True(Directory.Exists(authority.SessionRoot));
    }

    [Fact]
    public void RuntimeIsNotComposedUntilFirstUse()
    {
        using var authority = new InProcessExecutionAuthority(
            BuildPaths(Path.Combine(_root, "logs")),
            NullLogger.Instance,
            spoolRoot: Path.Combine(_root, "spool"));

        Assert.False(authority.Runtime.IsValueCreated);

        // First use composes the session, the tool service and the runtime.
        Assert.NotNull(authority.Runtime.Value);
        Assert.True(authority.Runtime.IsValueCreated);
    }

    [Fact]
    public void InvalidTerminalCapacityFailsBeforeTheRuntimeIsCreated()
    {
        var logRoot = Path.Combine(_root, "invalid-config-logs");
        var error = Assert.Throws<InvalidOperationException>(() => new InProcessExecutionAuthority(
            BuildPaths(logRoot),
            NullLogger.Instance,
            spoolRoot: Path.Combine(_root, "invalid-config-spool"),
            readEnvironment: name => name == "RU_MAX_BACKGROUND_TERMINAL_TASKS" ? "0" : null));

        Assert.Contains("RU_MAX_BACKGROUND_TERMINAL_TASKS", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(logRoot, "agent", "session")));
        Assert.False(Directory.Exists(Path.Combine(_root, "invalid-config-spool")));
    }

    [Fact]
    public void ValidTerminalCapacityIsReadOnceBeforeLazyRuntimeComposition()
    {
        var terminalCapacityReads = 0;
        using var authority = new InProcessExecutionAuthority(
            BuildPaths(Path.Combine(_root, "valid-config-logs")),
            NullLogger.Instance,
            spoolRoot: Path.Combine(_root, "valid-config-spool"),
            readEnvironment: name =>
            {
                if (name != "RU_MAX_BACKGROUND_TERMINAL_TASKS") return null;
                terminalCapacityReads++;
                return "3";
            });

        Assert.Equal(1, terminalCapacityReads);
        Assert.Equal(3, authority.TerminalConcurrency.MaxConcurrentTasks);
        Assert.False(authority.Runtime.IsValueCreated);
        Assert.NotNull(authority.Runtime.Value);
        Assert.Equal(1, terminalCapacityReads);
    }

    [Fact]
    public void DisposedAuthorityCannotComposeANewRuntime()
    {
        var authority = new InProcessExecutionAuthority(
            BuildPaths(Path.Combine(_root, "logs")), NullLogger.Instance,
            spoolRoot: Path.Combine(_root, "spool"));
        authority.Dispose();

        Assert.Throws<ObjectDisposedException>(() => authority.Runtime.Value);
        Assert.False(Directory.Exists(authority.SpoolRoot));
    }

    [Fact]
    public void WebOptionsAreDisabledWhenTheChildWouldHaveDisabledThem()
    {
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(_ => null, NullLogger.Instance));
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(
            name => name == "RU_AGENT_BROKER_WEB_ENABLED" ? "yes" : null,
            NullLogger.Instance));
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(
            name => name switch
            {
                "RU_AGENT_BROKER_WEB_ENABLED" => "true",
                "RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT" => "not a uri",
                _ => null,
            },
            NullLogger.Instance));
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(
            name => name switch
            {
                "RU_AGENT_BROKER_WEB_ENABLED" => "true",
                "RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES" => "0",
                _ => null,
            },
            NullLogger.Instance));
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(
            name => name switch
            {
                "RU_AGENT_BROKER_WEB_ENABLED" => "true",
                "RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES" => (4 * 1024 * 1024 + 1).ToString(),
                _ => null,
            },
            NullLogger.Instance));
        // A loopback endpoint is refused by WebOptions itself; the authority
        // fails closed to web-disabled instead of surfacing the exception.
        Assert.Null(InProcessExecutionAuthority.BuildWebOptions(
            name => name switch
            {
                "RU_AGENT_BROKER_WEB_ENABLED" => "true",
                "RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT" => "http://127.0.0.1:9/search",
                _ => null,
            },
            NullLogger.Instance));
    }

    [Fact]
    public void WebOptionsParseTheChildsEnvironment()
    {
        var options = InProcessExecutionAuthority.BuildWebOptions(
            name => name switch
            {
                "RU_AGENT_BROKER_WEB_ENABLED" => "TRUE",
                "RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT" => "https://www.google.com/search",
                "RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES" => "65536",
                _ => null,
            },
            NullLogger.Instance);

        Assert.NotNull(options);
        Assert.True(options!.Enabled);
        Assert.Equal("https://www.google.com/search", options.SearchEndpoint!.AbsoluteUri);
        Assert.Equal(65536, options.MaxResponseBytes);

        // Enabled without an endpoint keeps the child's default primary engine.
        var defaulted = InProcessExecutionAuthority.BuildWebOptions(
            name => name == "RU_AGENT_BROKER_WEB_ENABLED" ? "true" : null,
            NullLogger.Instance);
        Assert.NotNull(defaulted);
        Assert.Equal("https://www.google.com/search", defaulted!.SearchEndpoint!.AbsoluteUri);
    }

    [Fact]
    public void WorkspaceRootPrefersEnvironmentThenWalksTheCheckout()
    {
        var envRoot = Path.Combine(_root, "env-root");
        var projectRoot = Path.Combine(_root, "project-root");
        Directory.CreateDirectory(envRoot);
        Directory.CreateDirectory(projectRoot);

        Assert.Equal(
            Path.GetFullPath(envRoot),
            InProcessExecutionAuthority.ResolveWorkspaceRoot(envRoot, projectRoot, _root));
        Assert.Equal(
            Path.GetFullPath(projectRoot),
            InProcessExecutionAuthority.ResolveWorkspaceRoot(null, projectRoot, _root));
        // The child's chain is `RU_WORKSPACE_ROOT ?? RU_PROJECT_ROOT`: a
        // configured workspace root that does not exist falls through to the
        // walk, never back to RU_PROJECT_ROOT.
        Assert.Equal(
            _root,
            InProcessExecutionAuthority.ResolveWorkspaceRoot(Path.Combine(_root, "missing"), projectRoot, _root));

        // The checkout walk does not depend on the retired Python src tree.
        var checkout = Path.Combine(_root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "backend-dotnet"));
        File.WriteAllText(Path.Combine(checkout, "CONTEXT.md"), "Ru checkout");
        var nested = Path.Combine(checkout, "backend-dotnet", "RuBackend");
        Directory.CreateDirectory(nested);
        Assert.Equal(checkout, InProcessExecutionAuthority.ResolveWorkspaceRoot(null, null, nested));

        var nowhere = Path.Combine(_root, "nowhere");
        Directory.CreateDirectory(nowhere);
        Assert.Equal(nowhere, InProcessExecutionAuthority.ResolveWorkspaceRoot(null, null, nowhere));
    }

    [Fact]
    public void ProductionCompositionResolvesTheInProcessFactoryWithAnUncomposedRuntime()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        var gateways = factory.Services.GetRequiredService<RuBackend.Harness.Broker.IToolGatewayFactory>();
        Assert.IsType<RuBackend.Harness.Broker.InProcessToolGatewayFactory>(gateways);
        Assert.True(gateways.IsConfigured);

        var authority = factory.Services.GetRequiredService<InProcessExecutionAuthority>();
        Assert.False(authority.Runtime.IsValueCreated);
    }

    private DataPaths BuildPaths(string logRoot) =>
        DataPaths.Resolve(_root, _root, _root, Path.Combine(_root, "data"), logRoot);
}
