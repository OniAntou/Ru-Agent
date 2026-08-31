using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class StartupCancellationScopeTests
{
    [Fact]
    public async Task Close_during_ignored_cancellation_prevents_later_startup_steps()
    {
        using var scope = new StartupCancellationScope();
        var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterSteps = 0;
        CancellationToken observed = default;
        async Task Startup()
        {
            await scope.RunAsync(token => { observed = token; return response.Task; });
            laterSteps++;
        }
        var startup = Startup();
        scope.Cancel();
        Assert.True(observed.IsCancellationRequested);
        response.SetResult(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(0, laterSteps);
    }

    [Fact]
    public async Task Closed_scope_never_invokes_a_new_startup_operation()
    {
        using var scope = new StartupCancellationScope();
        scope.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.RunAsync(_ => { calls++; return Task.CompletedTask; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Successful_startup_preserves_result_and_real_errors()
    {
        using var scope = new StartupCancellationScope();
        Assert.Equal(42, await scope.RunAsync(_ => Task.FromResult(42)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RunAsync(_ => Task.FromException(new InvalidOperationException("fixture"))));
    }
}
