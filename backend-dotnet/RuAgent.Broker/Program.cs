using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;

var descriptorPath = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_DESCRIPTOR");
if (string.IsNullOrWhiteSpace(descriptorPath))
{
    return 2;
}

var webOptions = BuildWebOptions();
await using var host = webOptions is null
    ? await BrokerHost.StartAsync(descriptorPath)
    : await BrokerHost.StartAsync(new BrokerOptions(descriptorPath, Web: webOptions));
await host.Session.Descriptor.ExpiresAt.WaitUntilAsync();
return 0;

static WebOptions? BuildWebOptions()
{
    var enabledRaw = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_WEB_ENABLED");
    if (!string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    Uri? searchEndpoint = null;
    var endpointRaw = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT");
    if (!string.IsNullOrWhiteSpace(endpointRaw)
        && !Uri.TryCreate(endpointRaw, UriKind.Absolute, out searchEndpoint))
    {
        Console.Error.WriteLine("broker web disabled: search endpoint invalid");
        return null;
    }

    var maxResponseBytes = WebOptions.DefaultMaxResponseBytes;
    var maxRaw = Environment.GetEnvironmentVariable("RU_AGENT_BROKER_WEB_MAX_RESPONSE_BYTES");
    if (!string.IsNullOrWhiteSpace(maxRaw))
    {
        if (!long.TryParse(maxRaw, out var parsed)
            || parsed is < 1 or > WebOptions.MaxConfiguredResponseBytes)
        {
            Console.Error.WriteLine("broker web disabled: max response bytes invalid");
            return null;
        }

        maxResponseBytes = (int)parsed;
    }

    var requireApproval = string.Equals(
        Environment.GetEnvironmentVariable("RU_AGENT_BROKER_WEB_REQUIRE_APPROVAL"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    try
    {
        return new WebOptions(
            enabled: true,
            searchEndpoint: searchEndpoint,
            maxResponseBytes: maxResponseBytes,
            requireApproval: requireApproval);
    }
    catch (ArgumentException error)
    {
        Console.Error.WriteLine($"broker web disabled: {error.Message}");
        return null;
    }
}

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
