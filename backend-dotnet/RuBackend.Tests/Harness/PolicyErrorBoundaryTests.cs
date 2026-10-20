using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuAgent.Harness.Persistence.Privacy;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class PolicyErrorBoundaryTests
{
    private const string SensitiveFailure = "fixture-secret C:\\private\\credentials.json ";
    private static readonly object Preset = new { kind = "session_preset", session_id = new string('a', 32), preset = "standard" };

    [Theory]
    [InlineData(false, "policy_create_failed")]
    [InlineData(true, "preset_internal_error")]
    public async Task Policy_failures_return_safe_bounded_messages(bool preset, string expectedCode)
    {
        using var owner = new BackendWebApplicationFactory();
        using var factory = owner.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (preset)
            {
                services.RemoveAll<ISessionStore>();
                services.AddSingleton<ISessionStore>(sp => new FaultingSessions(new SessionStore(sp.GetRequiredService<DataPaths>()),
                    new IOException(SensitiveFailure + new string('x', 4096))));
            }
            else
            {
                services.RemoveAll<IHarnessKeyStore>();
                services.AddSingleton<IHarnessKeyStore>(new FaultingKeyStore());
            }
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-secret");
        client.DefaultRequestHeaders.Add("X-Interactive-Confirmed", "true");
        object body = preset ? Preset : new { scope = "global", decision = "allow", matcher = new { tool_id = "files.read", canonical_path_pattern = "src/file.txt" } };
        using var response = await client.PostAsJsonAsync("/agent/v1/policies", body);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fixture-secret", text);
        Assert.DoesNotContain("credentials.json", text);
        Assert.True(text.Length < 256);
        var json = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(expectedCode, json.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task Cancelled_preset_request_is_not_converted_to_an_internal_error()
    {
        using var cancellation = new CancellationTokenSource();
        using var owner = new BackendWebApplicationFactory();
        using var factory = owner.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISessionStore>();
            services.AddSingleton<ISessionStore>(sp => new FaultingSessions(new SessionStore(sp.GetRequiredService<DataPaths>()),
                new OperationCanceledException(cancellation.Token)));
        }));
        var service = factory.Services.GetRequiredService<AgentPoliciesService>();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreatePolicyAsync(
            JsonSerializer.SerializeToElement(Preset), true, cancellation.Token));
    }

    private sealed class FaultingKeyStore : IHarnessKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken) => throw new IOException(SensitiveFailure + new string('x', 4096));
    }

    private sealed class FaultingSessions(ISessionStore inner, Exception failure) : ISessionStore
    {
        public Task<SessionDocument?> GetAsync(string id, CancellationToken ct) => Task.FromException<SessionDocument?>(failure);
        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task<string?> CreateAsync(CancellationToken ct) => inner.CreateAsync(ct);
        public Task<bool> DeleteAsync(string id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task<bool> AppendAsync(string id, IReadOnlyList<SessionMessage> messages, CancellationToken ct) => inner.AppendAsync(id, messages, ct);
        public Task<SessionAppendOutcome> AppendIfAbsentAsync(string id, SessionMessage message, CancellationToken ct) => inner.AppendIfAbsentAsync(id, message, ct);
        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(string id, int maxTurns, CancellationToken ct) => inner.GetContextTurnsAsync(id, maxTurns, ct);
    }
}
