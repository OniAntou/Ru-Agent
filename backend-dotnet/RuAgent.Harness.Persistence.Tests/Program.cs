namespace RuAgent.Harness.Persistence.Tests;

// Test-only child-process entry point; VSTest still discovers this assembly's tests.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--schema-initialize") return 2;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(args[2])) await Task.Delay(10, deadline.Token);
        var health = await HarnessSqliteSchema.InitializeAsync(new(args[1]), deadline.Token);
        Console.WriteLine(health.IsAvailable ? "schema_ready" : health.ErrorCode);
        return health.IsAvailable ? 0 : 1;
    }
}
