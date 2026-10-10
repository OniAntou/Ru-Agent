using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Suites that stand up the production in-process execution authority and spawn real
/// child processes. They gate on shared resources (ports, process handles, CPU) and wait on
/// wall-clock deadlines, so running them beside each other starves those deadlines and
/// makes unrelated cases fail non-deterministically. Any new suite that constructs
/// <c>BackendWebApplicationFactory</c>, or that runs a terminal tool end to end,
/// belongs here.
/// </summary>
[CollectionDefinition(nameof(TerminalProcessHeavyCollection), DisableParallelization = true)]
public sealed class TerminalProcessHeavyCollection;
