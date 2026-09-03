using Xunit;

namespace RuBackend.Tests.Harness;

[CollectionDefinition(nameof(TerminalProcessHeavyCollection), DisableParallelization = true)]
public sealed class TerminalProcessHeavyCollection;
