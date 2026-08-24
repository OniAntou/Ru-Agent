using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Tests;

internal sealed class FakeHarnessKeyStore : IHarnessKeyStore
{
    private readonly byte[] _key;

    public FakeHarnessKeyStore(byte fill = 0x5a) =>
        _key = Enumerable.Repeat(fill, 32).ToArray();

    public ValueTask<byte[]> GetOrCreateKeyAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_key.ToArray());
    }
}
