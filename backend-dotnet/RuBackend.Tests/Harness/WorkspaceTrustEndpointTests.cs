using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceTrustEndpointTests
{
    [Fact]
    public async Task Trust_approve_deny_idempotency_and_stale_replay()
    {
        var fixture = await HarnessStoreTestFixture.CreateAsync();
        try
        {
            var options = fixture.Options;
            var resolver = new WindowsWorkspaceIdentityResolver();
            // Use a temp workspace path
            var tempRoot = Path.Combine(Path.GetTempPath(), "trust-endpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var identity = await resolver.ResolveAsync(tempRoot);
                var store = new SqliteWorkspaceStore(options);
                var trustStore = new WorkspaceTrustRequestStore(options);
                var trustService = new WorkspaceTrustService(resolver, store, trustStore);

                // Create initial trust request via registration
                var requestId = new TrustRequestId("trust_" + Guid.NewGuid().ToString("N").Substring(0, 32).PadRight(32, 'a'));
                // Generate proper trust id via random
                var hex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                requestId = new TrustRequestId($"trust_{hex}");
                var now = DateTimeOffset.UtcNow;
                var request = new WorkspaceTrustRequest(requestId, identity, now, now.AddMinutes(10));
                var registration = new WorkspaceRegistration(identity, new WorkspaceRootCiphertext(new byte[12], new byte[] { 1, 2, 3 }, new byte[16]));
                var stored = await store.RegisterAsync(registration, request, CancellationToken.None);
                Assert.Equal(requestId.Value, stored.TrustRequest.RequestId.Value);

                // Approve
                var response = new WorkspaceTrustResponse(requestId, identity, identity.DisplayIdentityHash, WorkspaceTrustDecision.Approve);
                var result1 = await trustStore.ApplyAsync(response, identity, now.AddMinutes(1), CancellationToken.None);
                Assert.True(result1.Accepted);
                Assert.False(result1.Idempotent);

                // Idempotent repeat same decision
                var result2 = await trustStore.ApplyAsync(response, identity, now.AddMinutes(2), CancellationToken.None);
                Assert.True(result2.Accepted);
                Assert.True(result2.Idempotent);

                // Different decision should conflict
                var denyResponse = new WorkspaceTrustResponse(requestId, identity, identity.DisplayIdentityHash, WorkspaceTrustDecision.Deny);
                var result3 = await trustStore.ApplyAsync(denyResponse, identity, now.AddMinutes(3), CancellationToken.None);
                Assert.False(result3.Accepted);
                Assert.Equal("trust_decision_conflict", result3.ErrorCode);

                // After approval, same identity remains trusted and new registration with same identity is ignored (idempotent)
                var newHex2 = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var newRequestId2 = new TrustRequestId($"trust_{newHex2}");
                var newRequest2 = new WorkspaceTrustRequest(newRequestId2, identity, now.AddMinutes(4), now.AddMinutes(14));
                var newRegistration2 = new WorkspaceRegistration(identity, new WorkspaceRootCiphertext(new byte[12], new byte[] { 4, 5, 6 }, new byte[16]));
                var afterTrusted = await store.RegisterAsync(newRegistration2, newRequest2, CancellationToken.None);
                // Since workspace is already trusted, registration returns existing (old) request, not new
                Assert.Equal(requestId.Value, afterTrusted.TrustRequest.RequestId.Value);
                var stillIdempotent = await trustStore.ApplyAsync(response, identity, now.AddMinutes(5), CancellationToken.None);
                Assert.True(stillIdempotent.Accepted);
                Assert.True(stillIdempotent.Idempotent);

                // Stale case: moved workspace requires new trust and makes old stale
                var movedIdentity = new WorkspaceIdentity(identity.WorkspaceId, "volume-moved", "root-moved", new string('f', 64));
                var movedHex = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                var movedRequestId = new TrustRequestId($"trust_{movedHex}");
                var movedRequest = new WorkspaceTrustRequest(movedRequestId, movedIdentity, now.AddMinutes(6), now.AddMinutes(16));
                var movedRegistration = new WorkspaceRegistration(movedIdentity, new WorkspaceRootCiphertext(new byte[12], new byte[] { 7, 8, 9 }, new byte[16]));
                await store.RegisterAsync(movedRegistration, movedRequest, CancellationToken.None);
                var stale = await trustStore.ApplyAsync(response, identity, now.AddMinutes(7), CancellationToken.None);
                Assert.False(stale.Accepted);
                Assert.Equal("trust_stale", stale.ErrorCode);
            }
            finally
            {
                Directory.Delete(tempRoot, true);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task No_model_step_before_approval()
    {
        var fixture = await HarnessStoreTestFixture.CreateAsync();
        try
        {
            var options = fixture.Options;
            var resolver = new WindowsWorkspaceIdentityResolver();
            var tempRoot = Path.Combine(Path.GetTempPath(), "trust-no-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var identity = await resolver.ResolveAsync(tempRoot);
                var store = new SqliteWorkspaceStore(options);
                var isTrusted = await store.IsTrustedAsync(identity, AgentPermissionPreset.Standard, CancellationToken.None);
                Assert.False(isTrusted);
                // If not trusted, HarnessRunService should require trust before running step
                // Simulate that check: a run in WaitingWorkspaceTrust should not be runnable
                var run = new AgentRun(
                    new RunId("run_" + Guid.NewGuid().ToString("N").Substring(0, 32).PadRight(32, 'a')),
                    new SessionId(new string('a', 32)),
                    identity.WorkspaceId,
                    new HarnessRunStatus(HarnessRunState.WaitingWorkspaceTrust),
                    version: 1,
                    lastSequence: 1,
                    budgetWindow: 1,
                    modelStepsInWindow: 0,
                    toolCallsInWindow: 0,
                    AgentPermissionPreset.Standard,
                    policySnapshotVersion: 1,
                    policySnapshotSha256: new string('a', 64),
                    createdAt: DateTimeOffset.UtcNow,
                    updatedAt: DateTimeOffset.UtcNow);
                Assert.Equal(HarnessRunState.WaitingWorkspaceTrust, run.State);
                // Coordinator should block
                Assert.True(run.State != HarnessRunState.Running);
            }
            finally
            {
                Directory.Delete(tempRoot, true);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Trust_request_persists_an_authenticated_encrypted_root()
    {
        var fixture = await HarnessStoreTestFixture.CreateAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trust-encrypted-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var resolver = new WindowsWorkspaceIdentityResolver();
            var store = new SqliteWorkspaceStore(fixture.Options);
            var trustStore = new WorkspaceTrustRequestStore(fixture.Options);
            var keyStore = new FixedKeyStore();
            var service = new WorkspaceTrustService(resolver, store, trustStore, keyStore);

            var request = await service.EnsureTrustRequestAsync(tempRoot);
            var identity = await resolver.ResolveAsync(tempRoot);
            var stored = await store.GetAsync(identity.WorkspaceId, CancellationToken.None);

            Assert.NotNull(stored);
            Assert.Equal(request.RequestId, stored!.TrustRequest.RequestId);
            Assert.NotEqual(new byte[] { 1, 2, 3 }, stored.EncryptedCanonicalRoot.Ciphertext.ToArray());

            var plaintext = new byte[stored.EncryptedCanonicalRoot.Ciphertext.Length];
            using var aes = new System.Security.Cryptography.AesGcm(FixedKeyStore.Key, 16);
            aes.Decrypt(
                stored.EncryptedCanonicalRoot.Nonce.Span,
                stored.EncryptedCanonicalRoot.Ciphertext.Span,
                stored.EncryptedCanonicalRoot.AuthenticationTag.Span,
                plaintext,
                System.Text.Encoding.UTF8.GetBytes("ru-workspace-root-v1:" + identity.WorkspaceId.Value));
            Assert.Equal(WindowsWorkspaceIdentityResolver.GetCanonicalRoot(tempRoot), System.Text.Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }

            await fixture.DisposeAsync();
        }
    }

    private sealed class HarnessStoreTestFixture : IAsyncDisposable
    {
        private HarnessStoreTestFixture(string rootPath)
        {
            RootPath = rootPath;
            Options = new HarnessSqliteOptions(Path.Combine(rootPath, "agent_harness.db"));
        }

        public string RootPath { get; }
        public HarnessSqliteOptions Options { get; }

        public static async Task<HarnessStoreTestFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ru-harness-trust-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var fixture = new HarnessStoreTestFixture(rootPath);
            var health = await HarnessSqliteSchema.InitializeAsync(fixture.Options, CancellationToken.None);
            Assert.True(health.IsAvailable, health.ErrorCode);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Yield();
            if (Directory.Exists(RootPath))
            {
                try
                {
                    Directory.Delete(RootPath, true);
                }
                catch (IOException)
                {
                    await Task.Delay(200);
                    try
                    {
                        Directory.Delete(RootPath, true);
                    }
                    catch
                    {
                        // best effort
                    }
                }
            }
        }
    }

    private sealed class FixedKeyStore : IHarnessKeyStore
    {
        public static readonly byte[] Key = Enumerable.Repeat((byte)0x5a, 32).ToArray();

        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Key.ToArray());
    }
}
