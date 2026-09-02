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
}
