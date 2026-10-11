using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderKeyProbeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Superseded_edit_never_deletes_key_or_reports_another_providers_verdict(bool duringSave)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool owns = true;
        int fetches = 0, deletes = 0;
        var task = ProviderKeyProbe.RunAsync("models.dev:a", "synthetic-key", () => owns,
            async (_, _) => { if (duringSave) { entered.SetResult(true); await gate.Task; } return true; },
            async id => { fetches++; Assert.Equal("models.dev:a", id); if (!duringSave) { entered.SetResult(true); await gate.Task; } return Rejected(id); },
            _ => { deletes++; return Task.FromResult(true); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        owns = false;
        gate.SetResult(true);
        Assert.Equal("superseded", (await task).Code);
        Assert.Equal(0, deletes);
        Assert.Equal(duringSave ? 0 : 1, fetches);
    }

    [Theory]
    [InlineData("models.dev:a", "catalog_unauthorized", true, "invalid", 1)]
    [InlineData("models.dev:a", "catalog_unauthorized", false, "remove_failed", 1)]
    [InlineData("models.dev:b", "catalog_unauthorized", true, "unverified", 0)]
    [InlineData("models.dev:a", "catalog_timeout", true, "unverified", 0)]
    public async Task Only_the_owned_providers_rejection_can_remove_its_key(
        string backend, string error, bool removed, string expected, int expectedDeletes)
    {
        int deletes = 0;
        var result = await ProviderKeyProbe.RunAsync("models.dev:a", "synthetic-key", () => true,
            (_, _) => Task.FromResult(true),
            _ => Task.FromResult<ProviderCatalogSnapshot?>(Rejected(backend, error)),
            id => { Assert.Equal("models.dev:a", id); deletes++; return Task.FromResult(removed); });
        Assert.Equal(expected, result.Code);
        Assert.Equal(expectedDeletes, deletes);
    }

    private static ProviderCatalogSnapshot Rejected(string backend, string error = "catalog_unauthorized") =>
        new() { Backend = backend, ErrorCode = error };
}
