using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceRestoreConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_preserves_changes_arriving_during_journal_decryption(bool createMissingTarget)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = Path.Combine(fixture.RootPath, "restore-race-workspace");
        Directory.CreateDirectory(root);
        const string runId = "run_" + "c" + "ccccccccccccccccccccccccccccccc";
        const string sessionId = "abababababababababababababababab";
        var seeded = await fixture.SeedRunWithEncryptedWorkspaceAsync(
            new HarnessRunStatus(HarnessRunState.Created), runId, sessionId, root);
        var workspaceId = seeded.Run.WorkspaceId;
        var workspaceStore = new SqliteWorkspaceStore(fixture.Options);
        var trust = new WorkspaceTrustService(
            new WindowsWorkspaceIdentityResolver(),
            workspaceStore,
            new WorkspaceTrustRequestStore(fixture.Options),
            fixture.KeyStore);
        var coordinator = new WorkspaceCoordinatorStore(fixture.Options);
        await coordinator.GetOrCreateAsync(workspaceId, CancellationToken.None);
        var realProtector = new WorkspaceJournalProtector(fixture.KeyStore);
        const string relativePath = "edited-during-restore.txt";
        var changeId = "chg_" + Guid.NewGuid().ToString("N");
        var target = Path.Combine(root, relativePath);
        var journalBytes = Encoding.UTF8.GetBytes("journaled original bytes");
        var journalPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            content_base64 = Convert.ToBase64String(journalBytes),
        });
        var encrypted = await realProtector.EncryptAsync(
            workspaceId, changeId, relativePath, journalPayload, CancellationToken.None);
        await coordinator.AppendChangeAsync(
            workspaceId,
            null,
            changeId,
            relativePath,
            Convert.ToHexString(SHA256.HashData(journalBytes)).ToLowerInvariant(),
            null,
            encrypted,
            "committed",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        byte[]? editorBytes = null;
        string expectedCurrentSha;
        if (createMissingTarget)
        {
            expectedCurrentSha = new string('0', 64);
            editorBytes = Encoding.UTF8.GetBytes("editor created this while decrypting");
        }
        else
        {
            var currentBytes = Encoding.UTF8.GetBytes("current version before restore");
            await File.WriteAllBytesAsync(target, currentBytes);
            expectedCurrentSha = Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant();
            editorBytes = Encoding.UTF8.GetBytes("editor replaced this while decrypting");
        }

        var mutatingProtector = new MutatingJournalProtector(realProtector, async () =>
        {
            await File.WriteAllBytesAsync(target, editorBytes!);
        });
        var service = new AgentWorkspaceService(
            fixture.Options,
            workspaceStore,
            trust,
            new SystemTimeProvider(),
            coordinator,
            mutatingProtector);

        var result = await service.RestoreWorkspaceChangeAsync(
            workspaceId.Value,
            changeId,
            new WorkspaceChangeRestoreRequest(expectedCurrentSha),
            CancellationToken.None);

        Assert.Equal(409, result.StatusCode);
        Assert.Equal("workspace_restore_stale", System.Text.Json.JsonSerializer.SerializeToElement(result.Body)
            .GetProperty("error_code").GetString());
        Assert.Equal(editorBytes, await File.ReadAllBytesAsync(target));
    }

    private sealed class MutatingJournalProtector(
        IWorkspaceJournalProtector inner,
        Func<Task> afterDecrypt) : IWorkspaceJournalProtector
    {
        public ValueTask<EncryptedWorkspaceJournal> EncryptAsync(
            WorkspaceId workspaceId,
            string changeId,
            string normalizedPath,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken) =>
            inner.EncryptAsync(workspaceId, changeId, normalizedPath, plaintext, cancellationToken);

        public async ValueTask<ReadOnlyMemory<byte>> DecryptAsync(
            WorkspaceId workspaceId,
            string changeId,
            string normalizedPath,
            EncryptedWorkspaceJournal encrypted,
            CancellationToken cancellationToken)
        {
            var plaintext = await inner.DecryptAsync(
                workspaceId, changeId, normalizedPath, encrypted, cancellationToken);
            await afterDecrypt();
            return plaintext;
        }
    }
}
