using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class CredentialStoreTests
{
    [Fact]
    public async Task Get_uses_the_service_target_when_its_username_matches()
    {
        var native = new FakeWindowsCredentialApi();
        native.Values["mini_gpt"] = new WindowsCredentialEntry("api_key", "secret-1");
        var store = new WindowsCredentialStore(native);

        var value = await store.GetAsync("mini_gpt", "api_key", CancellationToken.None);

        Assert.Equal("secret-1", value);
    }

    [Fact]
    public async Task Get_falls_back_to_the_keyring_compound_target_on_username_collision()
    {
        var native = new FakeWindowsCredentialApi();
        native.Values["mini_gpt"] = new WindowsCredentialEntry("different", "other-secret");
        native.Values["api_key@mini_gpt"] = new WindowsCredentialEntry("api_key", "secret-2");
        var store = new WindowsCredentialStore(native);

        var value = await store.GetAsync("mini_gpt", "api_key", CancellationToken.None);

        Assert.Equal("secret-2", value);
    }

    [Fact]
    public async Task Set_moves_a_service_collision_before_writing_the_requested_account()
    {
        var native = new FakeWindowsCredentialApi();
        native.Values["mini_gpt"] = new WindowsCredentialEntry("existing", "existing-secret");
        var store = new WindowsCredentialStore(native);

        await store.SetAsync("mini_gpt", "api_key", "  new-secret  ", CancellationToken.None);

        Assert.Equal(
            new WindowsCredentialEntry("existing", "existing-secret"),
            native.Values["existing@mini_gpt"]);
        Assert.Equal(new WindowsCredentialEntry("api_key", "  new-secret  "), native.Values["mini_gpt"]);
    }

    [Fact]
    public async Task Delete_removes_only_matching_service_and_compound_entries()
    {
        var native = new FakeWindowsCredentialApi();
        native.Values["mini_gpt"] = new WindowsCredentialEntry("different", "keep-me");
        native.Values["api_key@mini_gpt"] = new WindowsCredentialEntry("api_key", "delete-me");
        var store = new WindowsCredentialStore(native);

        await store.DeleteAsync("mini_gpt", "api_key", CancellationToken.None);

        Assert.True(native.Values.ContainsKey("mini_gpt"));
        Assert.False(native.Values.ContainsKey("api_key@mini_gpt"));
    }

    [Fact]
    public async Task Set_rejects_blank_secret_before_calling_Windows()
    {
        var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAsync("mini_gpt", "api_key", "   ", CancellationToken.None));

        Assert.Empty(native.Values);
    }

    [Fact]
    public async Task Set_rejects_a_secret_over_the_windows_blob_limit_before_calling_Windows()
    {
        var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SetAsync("mini_gpt", "api_key", new string('x', 1281), CancellationToken.None));

        Assert.Empty(native.Values);
    }

    [Fact]
    public async Task Set_accepts_the_maximum_windows_blob_secret()
    {
        var native = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(native);
        var secret = new string('x', 1280);

        await store.SetAsync("mini_gpt", "api_key", secret, CancellationToken.None);

        Assert.Equal(new WindowsCredentialEntry("api_key", secret), native.Values["mini_gpt"]);
    }

    [Fact]
    public async Task Native_failures_are_redacted_and_do_not_include_the_secret()
    {
        var native = new FakeWindowsCredentialApi { ThrowOnWrite = true };
        var store = new WindowsCredentialStore(native);

        var error = await Assert.ThrowsAsync<CredentialStoreException>(() =>
            store.SetAsync("mini_gpt", "api_key", "do-not-leak", CancellationToken.None));

        Assert.DoesNotContain("do-not-leak", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("Could not save the credential.", error.Message);
    }

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi
    {
        public Dictionary<string, WindowsCredentialEntry> Values { get; } =
            new(StringComparer.Ordinal);

        public bool ThrowOnWrite { get; init; }

        public WindowsCredentialEntry? Read(string targetName)
        {
            return Values.GetValueOrDefault(targetName);
        }

        public void Write(string targetName, string userName, string secret)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("native failure with do-not-leak");
            }

            Values[targetName] = new WindowsCredentialEntry(userName, secret);
        }

        public void Delete(string targetName)
        {
            Values.Remove(targetName);
        }
    }
}
