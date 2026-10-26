using System.Security.Cryptography;
using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Workspace;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class WorkspaceRootCryptographyTests
{
    [Fact]
    public void Root_envelope_roundtrips_and_authenticates_workspace_id()
    {
        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");
        var otherWorkspaceId = new WorkspaceId("ws_22222222222222222222222222222222");
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var plaintext = Encoding.UTF8.GetBytes("C:\\work\\ru");
        var envelope = WorkspaceRootCryptography.Encrypt(workspaceId, key, plaintext);

        Assert.Equal(plaintext, WorkspaceRootCryptography.Decrypt(workspaceId, key, envelope));
        Assert.ThrowsAny<CryptographicException>(() =>
            WorkspaceRootCryptography.Decrypt(otherWorkspaceId, key, envelope));
    }
}
