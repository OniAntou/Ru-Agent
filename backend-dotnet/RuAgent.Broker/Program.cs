using RuAgent.Broker.Runtime;

var descriptorPath = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR");
if (string.IsNullOrWhiteSpace(descriptorPath))
{
    return 2;
}

await using var host = await BrokerHost.StartAsync(descriptorPath);
await host.Session.Descriptor.ExpiresAt.WaitUntilAsync();
return 0;

internal static class DateTimeOffsetExtensions
{
    public static async Task WaitUntilAsync(this DateTimeOffset expiresAt)
    {
        var delay = expiresAt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }
    }
}
