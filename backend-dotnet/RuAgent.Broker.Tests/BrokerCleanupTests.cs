using RuAgent.Broker.Runtime;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BrokerCleanupTests
{
    [Fact]
    public async Task Cleanup_is_idempotent_and_removes_only_owned_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-agent-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var descriptorPath = Path.Combine(root, "broker.json");
        File.WriteAllText(descriptorPath, "descriptor");
        File.WriteAllText(Path.Combine(root, "owned.tmp"), "owned");
        var listenerStops = 0;
        var cancellations = 0;
        var childKills = 0;

        try
        {
            var cleanup = new BrokerCleanup(
                stopListener: _ =>
                {
                    listenerStops++;
                    return ValueTask.CompletedTask;
                },
                cancelRequests: () => cancellations++,
                killChildren: new[] { (Action)(() => childKills++) },
                descriptorPath,
                root);

            await cleanup.RunAsync();
            await cleanup.RunAsync();

            Assert.Equal(1, listenerStops);
            Assert.Equal(1, cancellations);
            Assert.Equal(1, childKills);
            Assert.False(File.Exists(descriptorPath));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Safe_artifact_writer_rejects_private_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), "ru-agent-artifact-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var writer = new SafeAgentArtifactWriter();

            Assert.Throws<ArgumentException>(() => writer.Write(
                path,
                new Dictionary<string, object?> { ["token"] = "must-not-write" }));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
