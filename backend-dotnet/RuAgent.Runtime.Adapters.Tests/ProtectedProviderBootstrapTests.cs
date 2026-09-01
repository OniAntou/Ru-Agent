using RuAgent.Runtime.Adapters.Bootstrap;
using System.Text.Json.Nodes;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ProtectedProviderBootstrapTests
{
    [Fact]
    public void Write_read_and_single_use_delete_protects_the_provider_credential()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        try
        {
            var data = new ProviderBootstrapData("OpenCode Zen", "chat_completions", new Uri("https://provider.invalid/v1"),
                "test-model",
                "provider-secret");

            var path = ProtectedProviderBootstrap.Write(root.FullName, data);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain("provider-secret", File.ReadAllText(path));

            var restored = ProtectedProviderBootstrap.ReadAndDelete(root.FullName, path);
            Assert.Equal(data, restored);
            Assert.False(File.Exists(path));
            Assert.ThrowsAny<Exception>(() => ProtectedProviderBootstrap.ReadAndDelete(root.FullName, path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Delete_rejects_paths_outside_the_owned_session_root()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        var outside = Path.Combine(Path.GetTempPath(), "ru-bootstrap-outside-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(outside, [1, 2, 3]);
        try
        {
            Assert.Throws<ArgumentException>(() => ProtectedProviderBootstrap.Delete(root.FullName, outside));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            root.Delete(recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void Delete_is_idempotent_for_a_failed_startup_cleanup()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        try
        {
            var path = Path.Combine(root.FullName, "missing.bin");
            ProtectedProviderBootstrap.Delete(root.FullName, path);
            ProtectedProviderBootstrap.Delete(root.FullName, path);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Request_options_are_allowlisted_and_cloned()
    {
        var requestOptions = new JsonObject
        {
            ["reasoning"] = new JsonObject
            {
                ["effort"] = "high",
                ["enabled"] = true,
                ["max_tokens"] = 4096,
            },
        };

        var data = new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            requestOptions);

        requestOptions["reasoning"]!["effort"] = "low";

        Assert.Equal("high", data.RequestOptions!["reasoning"]!["effort"]!.GetValue<string>());
        Assert.NotSame(requestOptions, data.RequestOptions);
    }

    [Fact]
    public void Request_options_round_trip_through_dpapi()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        try
        {
            var path = ProtectedProviderBootstrap.Write(
                root.FullName,
                new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
                    "model-a",
                    string.Empty,
                    new JsonObject { ["reasoning_effort"] = "xhigh" }));

            var restored = ProtectedProviderBootstrap.ReadAndDelete(root.FullName, path);

            Assert.Equal(
                "{\"reasoning_effort\":\"xhigh\"}",
                restored.RequestOptions!.ToJsonString());
            Assert.False(File.Exists(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Empty_request_options_mean_provider_default()
    {
        var data = new ProviderBootstrapData("Zen", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject());

        Assert.Null(data.RequestOptions);
    }

    [Theory]
    [InlineData("effort")]
    [InlineData("enabled")]
    [InlineData("max_tokens")]
    public void Reasoning_options_accept_a_nonempty_validated_subset(string field)
    {
        var reasoning = new JsonObject();
        reasoning[field] = field switch
        {
            "effort" => "high",
            "enabled" => true,
            "max_tokens" => 256,
            _ => throw new InvalidOperationException(),
        };

        var data = new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["reasoning"] = reasoning });

        Assert.NotNull(data.RequestOptions);
        Assert.Equal(reasoning.ToJsonString(), data.RequestOptions!["reasoning"]!.ToJsonString());
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("default")]
    public void Request_options_reject_unknown_reasoning_efforts(string effort)
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["reasoning_effort"] = effort }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void Request_options_reject_out_of_range_reasoning_tokens(int maxTokens)
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject
            {
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = "high",
                    ["enabled"] = true,
                    ["max_tokens"] = maxTokens,
                },
            }));
    }

    [Fact]
    public void Request_options_reject_arbitrary_provider_body_fields()
    {
        Assert.Throws<ArgumentException>(() => new ProviderBootstrapData("Kilo", "chat_completions", new Uri("https://provider.invalid/v1"),
            "model-a",
            string.Empty,
            new JsonObject { ["model"] = "other-model" }));
    }
}
