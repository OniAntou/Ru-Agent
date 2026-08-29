using RuAgent.Runtime.Adapters.Bootstrap;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class ProtectedProviderBootstrapTests
{
    [Fact]
    public void Write_read_and_single_use_delete_protects_the_provider_credential()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        try
        {
            var data = new ProviderBootstrapData(
                "OpenCode Zen",
                new Uri("https://provider.invalid/v1"),
                "test-model",
                "provider-secret");

            var path = ProtectedProviderBootstrap.Write(root.FullName, data);
            Assert.True(File.Exists(path));
            Assert.DoesNotContain("provider-secret", File.ReadAllText(path));

            var restored = ProtectedProviderBootstrap.ReadAndDelete(root.FullName, path);
            Assert.Equal(data, restored);
            Assert.False(File.Exists(path));
            Assert.ThrowsAny<Exception>(() => ProtectedProviderBootstrap.ReadAndDelete(root.FullName, path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Delete_rejects_paths_outside_the_owned_session_root()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        var outside = Path.Combine(Path.GetTempPath(), "ru-bootstrap-outside-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(outside, [1, 2, 3]);
        try
        {
            Assert.Throws<ArgumentException>(() => ProtectedProviderBootstrap.Delete(root.FullName, outside));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            root.Delete(recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void Delete_is_idempotent_for_a_failed_startup_cleanup()
    {
        var root = Directory.CreateTempSubdirectory("ru-bootstrap-");
        try
        {
            var path = Path.Combine(root.FullName, "missing.bin");
            ProtectedProviderBootstrap.Delete(root.FullName, path);
            ProtectedProviderBootstrap.Delete(root.FullName, path);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
