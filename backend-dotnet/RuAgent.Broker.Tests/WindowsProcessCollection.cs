using Xunit;

namespace RuAgent.Broker.Tests;

// Real child processes and synchronous pipe drains must not compete with other
// collections for limited worker threads on Windows CI runners.
[CollectionDefinition(nameof(WindowsProcessCollection), DisableParallelization = true)]
public sealed class WindowsProcessCollection;
