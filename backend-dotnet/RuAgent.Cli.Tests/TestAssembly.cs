using Xunit;

// The TUI suite pins process-wide state (palette level, colour depth, console width),
// so the assembly runs its tests sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
