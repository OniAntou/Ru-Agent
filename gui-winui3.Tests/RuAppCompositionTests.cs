using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class RuAppCompositionTests
{
    [Fact]
    public async Task Harness_composition_returns_external_session_without_starting_children()
    {
        var provider = new FakeRuBackendSessionProvider(
            BackendSession.FromLoopback(18001, "secret"));
        var composition = RuAppComposition.ForHarness(provider, "events.jsonl");

        var session = await composition.BackendProvider.StartAsync(
            "D:/LLM",
            CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(18001, session!.HttpBaseUri.Port);
        Assert.Equal(0, provider.StartManagedBackendCalls);
        Assert.False(composition.EnableSubtitleOverlay);
    }

    private sealed class FakeRuBackendSessionProvider : IRuBackendSessionProvider
    {
        private readonly BackendSession _session;

        public FakeRuBackendSessionProvider(BackendSession session)
        {
            _session = session;
        }

        public int StartManagedBackendCalls { get; private set; }
        public string DisplayName => "fake";
        public BackendSession? Session => _session;
        public event EventHandler? BackendExited
        {
            add { }
            remove { }
        }

        public Task<BackendSession?> StartAsync(
            string projectRoot,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<BackendSession?>(_session);
        }

        public void Dispose()
        {
        }
    }
}
