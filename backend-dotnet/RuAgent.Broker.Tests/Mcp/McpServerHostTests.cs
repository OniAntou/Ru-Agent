using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Mcp;
using Xunit;

namespace RuAgent.Broker.Tests.Mcp;

public sealed class McpConfigurationTests
{
    [Fact]
    public void Reads_servers_and_skips_invalid_or_disabled_entries()
    {
        var configuration = McpConfiguration.Parse("""
            {"servers":{
              "github":{"command":"npx.cmd","args":["-y","server-github"],"env":{"GITHUB_TOKEN":{"credential":"ru-mcp-github"},"LEVEL":"info"},"description":"Issues","timeout_seconds":9999},
              "off":{"command":"x.exe","enabled":false},
              "Bad Name":{"command":"x.exe"},
              "nocommand":{"args":["a"]},
              "badenv":{"command":"x.exe","env":{"OK":42}},
              "relcwd":{"command":"x.exe","cwd":"relative\\path"}
            }}
            """);

        var server = Assert.Single(configuration.Servers);
        Assert.Equal("github", server.Name);
        Assert.Equal(["-y", "server-github"], server.Arguments);
        Assert.Equal("ru-mcp-github", server.Environment["GITHUB_TOKEN"].Credential);
        Assert.Equal("info", server.Environment["LEVEL"].Literal);
        Assert.Equal(McpConfiguration.MaxCallTimeout, server.CallTimeout);
    }

    [Fact]
    public void Reads_explicit_streamable_http_and_skips_ambiguous_or_unsafe_entries()
    {
        var configuration = McpConfiguration.Parse("""
            {"servers":{
              "remote":{"transport":"streamable_http","url":"https://mcp.example.com/mcp","bearer_credential":"ru-mcp-remote"},
              "loopback":{"transport":"streamable_http","url":"http://127.0.0.1:8080/mcp"},
              "plain-http":{"transport":"streamable_http","url":"http://mcp.example.com/mcp"},
              "userinfo":{"transport":"streamable_http","url":"https://user:pass@mcp.example.com/mcp"},
              "mixed":{"transport":"streamable_http","url":"https://mcp.example.com/mcp","command":"run.exe"},
              "bad-transport":{"transport":null,"command":"run.exe"},
              "bad-enabled":{"enabled":"true","command":"run.exe"},
              "duplicate":{"transport":"streamable_http","url":"https://mcp.example.com/one","url":"https://mcp.example.com/two"}
            }}
            """);

        Assert.Equal(["remote", "loopback"], configuration.Servers.Select(server => server.Name));
        Assert.Equal(McpServerTransport.StreamableHttp, configuration.Servers[0].Transport);
        Assert.Equal("https://mcp.example.com/mcp", configuration.Servers[0].Endpoint!.AbsoluteUri);
        Assert.Equal("ru-mcp-remote", configuration.Servers[0].BearerCredential);
        Assert.Null(configuration.Servers[1].BearerCredential);
    }

    [Fact]
    public void Duplicate_server_names_are_skipped_instead_of_selecting_one_definition()
    {
        var configuration = McpConfiguration.Parse("""
            {"servers":{"same":{"command":"one.exe"},"same":{"command":"two.exe"},"valid":{"command":"ok.exe"}}}
            """);

        Assert.Equal("valid", Assert.Single(configuration.Servers).Name);
    }

    [Fact]
    public void Only_the_users_own_file_is_read()
    {
        var home = Directory.CreateTempSubdirectory("ru-mcp-home").FullName;
        try
        {
            Assert.True(McpConfiguration.Load(home).IsEmpty);
            Directory.CreateDirectory(Path.Combine(home, ".ru"));
            File.WriteAllText(McpConfiguration.PathFor(home), """{"servers":{"local":{"command":"server.exe"}}}""");
            Assert.Equal("local", Assert.Single(McpConfiguration.Load(home).Servers).Name);
            Assert.True(McpConfiguration.Load(null).IsEmpty);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}

public sealed class McpServerHostTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("ru-mcp-host").FullName;

    public McpServerHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".ru"));
        File.WriteAllText(McpConfiguration.PathFor(_home), """
            {"servers":{"demo":{"command":"demo.exe","env":{"TOKEN":{"credential":"ru-mcp-demo"}},"description":"Demo","timeout_seconds":2}}}
            """);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private McpServerHost Host(FakeMcpServer server, Func<string, string?>? credentials = null, List<IReadOnlyDictionary<string, string>>? environments = null) =>
        new(() => _home, credentials ?? (_ => "s3cret-token"), (_, environment, _) =>
        {
            environments?.Add(environment);
            return server.Connect();
        });

    [Fact]
    public async Task Listing_servers_starts_nothing()
    {
        await using var server = new FakeMcpServer(FakeMcpServer.Standard);
        await using var host = Host(server);

        var listed = host.ListServers();

        Assert.Equal("demo", listed["servers"]![0]!["name"]!.GetValue<string>());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_call_starts_the_server_once_and_returns_bounded_untrusted_text()
    {
        await using var server = new FakeMcpServer(FakeMcpServer.Standard);
        var environments = new List<IReadOnlyDictionary<string, string>>();
        await using var host = Host(server, environments: environments);

        var tools = await host.ListToolsAsync("demo", CancellationToken.None);
        var result = await host.CallToolAsync("demo", "echo", """{"message":"hi"}""", CancellationToken.None);

        Assert.Equal("echo", tools["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("echo: hi", result["output"]!.GetValue<string>());
        Assert.True(result["untrusted"]!.GetValue<bool>());
        Assert.Equal("s3cret-token", Assert.Single(environments)["TOKEN"]);
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"],
            server.Requests.Select(request => request["method"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Credential_values_never_leave_the_host_in_output_or_errors()
    {
        await using var server = new FakeMcpServer((method, parameters) => method == "tools/call"
            ? new JsonObject { ["__error"] = "bad token s3cret-token" }
            : FakeMcpServer.Standard(method, parameters));
        await using var host = Host(server);

        var error = await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("demo", "echo", "{}", CancellationToken.None));

        Assert.Equal("mcp_error", error.Code);
        Assert.DoesNotContain("s3cret-token", error.Detail);
        Assert.Contains("[redacted]", error.Detail);
    }

    [Fact]
    public async Task A_missing_credential_fails_by_name_before_starting()
    {
        await using var server = new FakeMcpServer(FakeMcpServer.Standard);
        await using var host = Host(server, credentials: _ => null);

        var error = await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("demo", CancellationToken.None));

        Assert.Equal("mcp_credential_missing", error.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task An_unknown_server_and_malformed_arguments_fail_by_name()
    {
        await using var server = new FakeMcpServer(FakeMcpServer.Standard);
        await using var host = Host(server);

        Assert.Equal("mcp_server_unknown",
            (await Assert.ThrowsAsync<McpException>(() => host.ListToolsAsync("other", CancellationToken.None))).Code);
        Assert.Equal("mcp_arguments_invalid",
            (await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("demo", "echo", "[1,2]", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_tool_error_and_non_text_content_are_reported_without_raw_payloads()
    {
        await using var server = new FakeMcpServer((method, parameters) => method == "tools/call"
            ? new JsonObject
            {
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "done" },
                    new JsonObject { ["type"] = "image", ["data"] = "AAAA", ["mimeType"] = "image/png" }),
            }
            : FakeMcpServer.Standard(method, parameters));
        await using var host = Host(server);

        var result = await host.CallToolAsync("demo", "echo", "{}", CancellationToken.None);

        Assert.Equal("done", result["output"]!.GetValue<string>());
        Assert.Contains("1 non-text", result["omitted_content"]!.GetValue<string>());
        Assert.DoesNotContain("AAAA", result.ToJsonString());
    }

    [Fact]
    public async Task An_unanswered_call_times_out_by_name()
    {
        await using var server = new FakeMcpServer((method, parameters) => method == "tools/call" ? null : FakeMcpServer.Standard(method, parameters));
        await using var host = Host(server);

        var error = await Assert.ThrowsAsync<McpException>(() => host.CallToolAsync("demo", "echo", "{}", CancellationToken.None));

        Assert.Equal("mcp_timeout", error.Code);
    }
}

public sealed class McpStdioClientTests
{
    [Fact]
    public async Task Malformed_foreign_and_string_id_frames_do_not_end_the_read_loop()
    {
        await using var server = new FakeMcpServer(FakeMcpServer.Standard);
        await using var client = server.Connect();
        await server.SendRawAsync("not json");
        await server.SendRawAsync("""{"jsonrpc":"2.0","id":"unknown-id","result":{}}""");
        await server.SendRawAsync("""{"jsonrpc":"2.0","id":{"nested":true},"result":{}}""");

        await client.InitializeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        var tools = await client.ListToolsAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal("echo", Assert.Single(tools).Name);
    }

    [Fact]
    public async Task A_server_that_exits_at_once_is_unavailable_by_name()
    {
        var definition = new McpServerDefinition("exits", Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/c", "exit", "3"], null, new Dictionary<string, McpEnvironmentValue>(), null, TimeSpan.FromSeconds(5));
        await using var client = McpStdioClient.Start(definition, new Dictionary<string, string>
        {
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
        }, Path.GetTempPath());

        var error = await Assert.ThrowsAsync<McpException>(() => client.InitializeAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        Assert.Equal("mcp_server_unavailable", error.Code);
    }
}
