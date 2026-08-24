using System.Text;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Workspace;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public async Task Registration_persists_only_encrypted_root_and_identity_fields()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = HarnessStoreTestFixture.Workspace(1);
        var canonicalRoot = @"D:\private\workspace";
        var ciphertext = new WorkspaceRootCiphertext(
            Enumerable.Repeat((byte)1, 12).ToArray(),
            System.Text.Encoding.UTF8.GetBytes("ciphertext-not-the-root"),
            Enumerable.Repeat((byte)2, 16).ToArray());
        var registration = new WorkspaceRegistration(identity, ciphertext);
        var request = Request(identity, 1);
        var store = new SqliteWorkspaceStore(fixture.Options);

        var stored = await store.RegisterAsync(registration, request, CancellationToken.None);

        Assert.False(stored.IsTrusted);
        Assert.Equal(identity.VolumeSerialNumber, stored.Identity.VolumeSerialNumber);
        Assert.Equal(identity.RootFileId, stored.Identity.RootFileId);
        Assert.Equal(identity.DisplayIdentityHash, stored.Identity.DisplayIdentityHash);
        Assert.Equal(request.RequestId, stored.TrustRequest.RequestId);
        var storedCiphertext = await BlobAsync(
            fixture,
            "SELECT canonical_root_ciphertext FROM agent_workspaces");
        Assert.DoesNotContain(
            canonicalRoot,
            System.Text.Encoding.UTF8.GetString(storedCiphertext));
        Assert.Equal(1L, await ScalarAsync(
            fixture,
            "SELECT COUNT(*) FROM agent_workspaces WHERE canonical_root_ciphertext IS NOT NULL"));
    }

    [Fact]
    public async Task Full_access_does_not_bypass_untrusted_workspace()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(1);
        var store = new SqliteWorkspaceStore(fixture.Options);

        var trusted = await store.IsTrustedAsync(
            identity,
            AgentPermissionPreset.FullAccess,
            CancellationToken.None);

        Assert.False(trusted);
    }

    [Fact]
    public async Task Trust_response_is_hash_bound_and_same_response_is_idempotent()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(1);
        var requestStore = new WorkspaceTrustRequestStore(fixture.Options);
        var requestId = HarnessStoreTestFixture.Trust(1);
        var wrongHash = new WorkspaceTrustResponse(
            requestId,
            identity,
            HarnessStoreTestFixture.Sha('f'),
            WorkspaceTrustDecision.Approve);

        var rejected = await requestStore.ApplyAsync(
            wrongHash,
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var response = new WorkspaceTrustResponse(
            requestId,
            identity,
            identity.DisplayIdentityHash,
            WorkspaceTrustDecision.Approve);
        var accepted = await requestStore.ApplyAsync(
            response,
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var repeated = await requestStore.ApplyAsync(
            response,
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            CancellationToken.None);

        Assert.False(rejected.Accepted);
        Assert.Equal("trust_display_hash_mismatch", rejected.ErrorCode);
        Assert.True(accepted.Accepted);
        Assert.False(accepted.Idempotent);
        Assert.True(repeated.Accepted);
        Assert.True(repeated.Idempotent);
        Assert.True(await new SqliteWorkspaceStore(fixture.Options).IsTrustedAsync(
            identity,
            AgentPermissionPreset.Standard,
            CancellationToken.None));
    }

    [Fact]
    public async Task Moved_workspace_identity_requires_a_fresh_request_and_stales_the_old_one()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var original = await fixture.RegisterWorkspaceAsync(1);
        var requestStore = new WorkspaceTrustRequestStore(fixture.Options);
        var originalResponse = new WorkspaceTrustResponse(
            HarnessStoreTestFixture.Trust(1),
            original,
            original.DisplayIdentityHash,
            WorkspaceTrustDecision.Approve);
        await requestStore.ApplyAsync(
            originalResponse,
            original,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var moved = new WorkspaceIdentity(
            original.WorkspaceId,
            "volume-moved",
            "root-file-moved",
            HarnessStoreTestFixture.Sha('f'));
        var registration = new WorkspaceRegistration(
            moved,
            new WorkspaceRootCiphertext(new byte[12], new byte[] { 1, 2, 3 }, new byte[16]));
        var freshRequest = new WorkspaceTrustRequest(
            HarnessStoreTestFixture.Trust(2),
            moved,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            HarnessStoreTestFixture.Timestamp.AddMinutes(12));

        var stored = await new SqliteWorkspaceStore(fixture.Options).RegisterAsync(
            registration,
            freshRequest,
            CancellationToken.None);
        var stale = await requestStore.ApplyAsync(
            originalResponse,
            original,
            HarnessStoreTestFixture.Timestamp.AddMinutes(3),
            CancellationToken.None);

        Assert.False(stored.IsTrusted);
        Assert.Equal(freshRequest.RequestId, stored.TrustRequest.RequestId);
        Assert.False(stale.Accepted);
        Assert.Equal("trust_stale", stale.ErrorCode);
        Assert.False(await new SqliteWorkspaceStore(fixture.Options).IsTrustedAsync(
            moved,
            AgentPermissionPreset.FullAccess,
            CancellationToken.None));
    }

    [Fact]
    public async Task Denied_request_for_the_same_identity_is_replaced_by_a_fresh_request()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = await fixture.RegisterWorkspaceAsync(1);
        var requestStore = new WorkspaceTrustRequestStore(fixture.Options);
        var denial = new WorkspaceTrustResponse(
            HarnessStoreTestFixture.Trust(1),
            identity,
            identity.DisplayIdentityHash,
            WorkspaceTrustDecision.Deny);
        await requestStore.ApplyAsync(
            denial,
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(1),
            CancellationToken.None);
        var freshRequest = new WorkspaceTrustRequest(
            HarnessStoreTestFixture.Trust(2),
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(2),
            HarnessStoreTestFixture.Timestamp.AddMinutes(12));
        var registration = new WorkspaceRegistration(
            identity,
            new WorkspaceRootCiphertext(new byte[12], new byte[] { 4, 5, 6 }, new byte[16]));

        var stored = await new SqliteWorkspaceStore(fixture.Options).RegisterAsync(
            registration,
            freshRequest,
            CancellationToken.None);

        Assert.False(stored.IsTrusted);
        Assert.Equal(freshRequest.RequestId, stored.TrustRequest.RequestId);
        var stale = await requestStore.ApplyAsync(
            denial,
            identity,
            HarnessStoreTestFixture.Timestamp.AddMinutes(3),
            CancellationToken.None);
        Assert.False(stale.Accepted);
        Assert.Equal("trust_stale", stale.ErrorCode);
    }

    [Fact]
    public async Task Canonical_root_only_encrypted_volume_and_root_hash_stored_one_active_identity_per_workspace()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        var identity = HarnessStoreTestFixture.Workspace(9);
        var canonicalRoot = @"D:\private\root\secret-workspace-path";
        var ciphertext = new WorkspaceRootCiphertext(
            Enumerable.Repeat((byte)9, 12).ToArray(),
            Encoding.UTF8.GetBytes("encrypted-blob-not-plain-root"),
            Enumerable.Repeat((byte)8, 16).ToArray());
        var registration = new WorkspaceRegistration(identity, ciphertext);
        var request = Request(identity, 9);
        var store = new SqliteWorkspaceStore(fixture.Options);

        var stored = await store.RegisterAsync(registration, request, CancellationToken.None);

        // canonical root only in encrypted payload
        await using var conn = fixture.CreateRawConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT volume_serial_number, root_file_id, display_identity_hash, canonical_root_ciphertext FROM agent_workspaces WHERE workspace_id = $id";
        cmd.Parameters.AddWithValue("$id", identity.WorkspaceId.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(identity.VolumeSerialNumber, reader.GetString(0));
        Assert.Equal(identity.RootFileId, reader.GetString(1));
        Assert.Equal(identity.DisplayIdentityHash, reader.GetString(2));
        var storedCipher = (byte[])reader[3];
        Assert.DoesNotContain(canonicalRoot, Encoding.UTF8.GetString(storedCipher));
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_workspaces"));

        // one active trusted identity per workspace – second registration with same ID but moved identity replaces
        var moved = new WorkspaceIdentity(identity.WorkspaceId, "volume-serial-moved-9", "root-file-moved-9", HarnessStoreTestFixture.Sha('e'));
        var movedReg = new WorkspaceRegistration(moved, new WorkspaceRootCiphertext(new byte[12], new byte[] { 7, 8, 9 }, new byte[16]));
        var freshRequest = new WorkspaceTrustRequest(HarnessStoreTestFixture.Trust(10), moved, HarnessStoreTestFixture.Timestamp.AddMinutes(5), HarnessStoreTestFixture.Timestamp.AddMinutes(15));
        var movedStored = await store.RegisterAsync(movedReg, freshRequest, CancellationToken.None);
        Assert.Equal(1L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM agent_workspaces"));
        Assert.Equal(moved.VolumeSerialNumber, movedStored.Identity.VolumeSerialNumber);
        Assert.Equal(moved.RootFileId, movedStored.Identity.RootFileId);
        Assert.Equal(moved.DisplayIdentityHash, movedStored.Identity.DisplayIdentityHash);
        Assert.False(movedStored.IsTrusted);
        Assert.False(await store.IsTrustedAsync(identity, AgentPermissionPreset.Standard, CancellationToken.None));
        Assert.False(await store.IsTrustedAsync(moved, AgentPermissionPreset.Standard, CancellationToken.None));
    }

    private static WorkspaceTrustRequest Request(WorkspaceIdentity identity, int number) =>
        new(
            HarnessStoreTestFixture.Trust(number),
            identity,
            HarnessStoreTestFixture.Timestamp,
            HarnessStoreTestFixture.Timestamp.AddMinutes(10));

    private static async Task<long> ScalarAsync(
        HarnessStoreTestFixture fixture,
        string commandText)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<byte[]> BlobAsync(
        HarnessStoreTestFixture fixture,
        string commandText)
    {
        await using var connection = fixture.CreateRawConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (byte[])(await command.ExecuteScalarAsync() ?? Array.Empty<byte>());
    }
}
