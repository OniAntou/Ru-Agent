using Microsoft.Data.Sqlite;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class FreshSchemaConcurrencyTests
{
    [Fact]
    public async Task Concurrent_initializers_can_create_a_fresh_database()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        for (var round = 0; round < 10; round++)
        {
            var options = new HarnessSqliteOptions(Path.Combine(fixture.RootPath, $"fresh-{round}.db"));
            using var start = new Barrier(8);
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                return HarnessSqliteSchema.InitializeAsync(options, default).GetAwaiter().GetResult();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                var results = await Task.WhenAll(workers);
                Assert.All(results, result => Assert.True(result.IsAvailable, result.ErrorCode));
            }
            finally
            {
                await using var connection = await options.OpenConnectionAsync(default);
                SqliteConnection.ClearPool(connection);
            }
        }
    }
}
