using System.Text;
using System.Security.Cryptography;
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

public sealed class RestoreContentTests
{
    [Fact]
    public async Task Restore_deleted_file_with_missing_marker_restores_the_journaled_bytes()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = Path.Combine(fixture.RootPath, "restore-workspace");
        Directory.CreateDirectory(root);
        const string runId = "run_" + "a" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sessionId = "77777777777777777777777777777777";
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
        var protector = new WorkspaceJournalProtector(fixture.KeyStore);
        const string relativePath = "deleted.txt";
        const string changeId = "chg_" + "b" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var target = Path.Combine(root, relativePath);
        var originalBytes = Encoding.UTF8.GetBytes("restore this deleted file");
        await File.WriteAllBytesAsync(target, originalBytes);
        var beforeSha = Convert.ToHexString(SHA256.HashData(originalBytes)).ToLowerInvariant();
        var journalPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            content_base64 = Convert.ToBase64String(originalBytes),
        });
        var encrypted = await protector.EncryptAsync(workspaceId, changeId, relativePath, journalPayload, CancellationToken.None);
        await coordinator.AppendChangeAsync(
            workspaceId, null, changeId, relativePath, beforeSha, null, encrypted,
            "committed", DateTimeOffset.UtcNow, CancellationToken.None);
        File.Delete(target);

        var service = new AgentWorkspaceService(
            fixture.Options,
            workspaceStore,
            trust,
            new SystemTimeProvider(),
            coordinator,
            protector);
        var result = await service.RestoreWorkspaceChangeAsync(
            workspaceId.Value,
            changeId,
            new WorkspaceChangeRestoreRequest(new string('0', 64)),
            CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public void Empty_base64_restores_an_originally_empty_file()
    {
        var ok = AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes("""{"content_base64":""}"""), out var content);

        Assert.True(ok);
        Assert.Empty(content);
    }

    [Theory]
    [InlineData("""{"content_base64":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"content_base64":"!!not-base64"}""")]
    public void Missing_null_or_invalid_content_is_unavailable(string json)
    {
        Assert.False(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void Non_empty_content_round_trips()
    {
        var payload = $$"""{"content_base64":"{{Convert.ToBase64String("abc"u8)}}"}""";

        Assert.True(AgentV1Shared.TryReadRestoreContent(Encoding.UTF8.GetBytes(payload), out var content));
        Assert.Equal("abc"u8.ToArray(), content);
    }

    [Theory]
    [InlineData("aa", "aa", true)]
    [InlineData("aa", "ab", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000",
        "0000000000000000000000000000000000000000000000000000000000000000", true)]
    [InlineData("aa", "0000000000000000000000000000000000000000000000000000000000000000", false)]
    public void Restore_target_comparison_is_exact(string current, string expected, bool matches)
    {
        Assert.Equal(matches, AgentWorkspaceService.RestoreTargetMatches(current, expected));
    }

    [Fact]
    public void Restore_checks_and_writes_existing_files_through_a_write_excluding_handle()
    {
        // An open Windows handle with FileShare.Read blocks competing writers and
        // rename-over until the content hash and replacement are complete.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend-dotnet", "RuBackend", "Harness", "AgentWorkspaceService.Restore.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!.FullName, "backend-dotnet", "RuBackend", "Harness", "AgentWorkspaceService.Restore.cs"));

        var apply = text.IndexOf("TryApplyRestoreAsync", StringComparison.Ordinal);
        var heldHandle = text.IndexOf("FileShare.Read", StringComparison.Ordinal);
        var directoryLease = text.IndexOf("DirectoryHandleLease.Open(parent, requireCanonicalPath: true)", StringComparison.Ordinal);

        Assert.True(apply >= 0, "restore must use the guarded apply path");
        Assert.True(heldHandle > apply, "the existing target must deny concurrent writes");
        Assert.True(directoryLease > apply, "the parent path must remain canonical during restore");
        Assert.DoesNotContain("File.Move(temporary, target, overwrite: true)", text, StringComparison.Ordinal);
    }
}
