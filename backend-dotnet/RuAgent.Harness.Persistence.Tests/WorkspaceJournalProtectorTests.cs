using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceJournalProtectorTests
{
    [Fact]
    public async Task Encrypt_and_decrypt_round_trip_and_detect_tampering()
    {
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");
        var encrypted = await protector.EncryptAsync(workspaceId, "chg_01", "src/app.cs", Encoding.UTF8.GetBytes("one"), CancellationToken.None);

        var plaintext = await protector.DecryptAsync(workspaceId, "chg_01", "src/app.cs", encrypted, CancellationToken.None);
        Assert.Equal("one", Encoding.UTF8.GetString(plaintext.Span));

        var tamperedBytes = encrypted.Ciphertext.ToArray();
        tamperedBytes[0] ^= 0x01;
        var tampered = encrypted with { Ciphertext = tamperedBytes };
        var error = await Assert.ThrowsAsync<HarnessStoreException>(async () =>
            await protector.DecryptAsync(workspaceId, "chg_01", "src/app.cs", tampered, CancellationToken.None));
        Assert.Equal("journal_authentication_failed", error.Code);
    }

    [Fact]
    public async Task Oversized_journal_is_rejected_before_decryption_allocation()
    {
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");
        var oversized = new EncryptedWorkspaceJournal(
            WorkspaceJournalProtector.CurrentProtectionVersion,
            new byte[12],
            new byte[WorkspaceJournalProtector.MaximumJournalBytes + 1],
            new byte[16]);

        var error = await Assert.ThrowsAsync<HarnessStoreException>(async () =>
            await protector.DecryptAsync(workspaceId, "chg_01", "src/app.cs", oversized, CancellationToken.None));

        Assert.Equal("journal_too_large", error.Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../outside.txt")]
    [InlineData("./app.cs")]
    public async Task Journal_metadata_rejects_traversal_segments(string path)
    {
        var protector = new WorkspaceJournalProtector(new FakeHarnessKeyStore());
        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            protector.EncryptAsync(
                workspaceId,
                "chg_01",
                path,
                Encoding.UTF8.GetBytes("one"),
                CancellationToken.None).AsTask());
    }
}
