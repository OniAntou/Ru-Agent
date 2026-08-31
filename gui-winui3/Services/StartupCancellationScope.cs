using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services;

/// <summary>Checks both sides of each startup await, including operations that ignore cancellation.</summary>
public sealed class StartupCancellationScope : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    public CancellationToken Token { get; }

    public StartupCancellationScope() => Token = _source.Token;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        Token.ThrowIfCancellationRequested();
        var result = await operation(Token);
        Token.ThrowIfCancellationRequested();
        return result;
    }

    public Task RunAsync(Func<CancellationToken, Task> operation) => RunAsync(async token =>
    {
        await operation(token);
        return true;
    });

    public void Cancel() => _source.Cancel();
    public void Dispose() => _source.Dispose();
}
