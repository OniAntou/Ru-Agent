using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class RuPromptContextProviderTests
{
    [Fact]
    public async Task Project_memory_is_refreshed_across_context_builds_and_remains_untrusted_runtime_data()
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru", "memory"));
        var path = Path.Combine(folder.FullName, "MEMORY.md");
        await File.WriteAllTextAsync(path, "Use the approved fixture toolchain.\nSee build-notes.md.");
        var provider = new RuPromptContextProvider(fixture.Root, new RuInstructionLoader(),
            new StubSettingsStore(Settings()), new FixedTimeProvider(DateTimeOffset.UtcNow));
        var first = await provider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);
        var note = Assert.Single(first.RuntimeContext.References, x => x.Kind == "project_memory");
        Assert.Contains("build-notes.md", note.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(first.InstructionBlocks, x => x.Content.Contains("build-notes.md", StringComparison.Ordinal));
        var prompt = SystemPromptComposer.Compose(first, nativeToolCalling: true, deferRuntimeContext: true);
        Assert.DoesNotContain("build-notes.md", prompt.Text, StringComparison.Ordinal);
        Assert.Contains(SystemPromptComposer.UntrustedDataWarning, prompt.RuntimeContext, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, "A correction supersedes the old note.");
        var second = await provider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);
        Assert.Contains(second.RuntimeContext.References, x => x.Kind == "project_memory" && x.Content.Contains("correction", StringComparison.Ordinal));
        File.Delete(path);
        var third = await provider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);
        Assert.DoesNotContain(third.RuntimeContext.References, x => x.Kind == "project_memory");
    }

    [Fact]
    public async Task Project_memory_is_isolated_to_the_exact_workspace_root()
    {
        using var firstWorkspace = RuInstructionLoaderTestsRoot.Create();
        using var secondWorkspace = RuInstructionLoaderTestsRoot.Create();
        var memoryDirectory = Directory.CreateDirectory(Path.Combine(firstWorkspace.Root, ".ru", "memory"));
        await File.WriteAllTextAsync(Path.Combine(memoryDirectory.FullName, "MEMORY.md"), "First workspace only.");
        var settings = new StubSettingsStore(Settings());
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var firstProvider = new RuPromptContextProvider(firstWorkspace.Root, new RuInstructionLoader(), settings, time);
        var secondProvider = new RuPromptContextProvider(secondWorkspace.Root, new RuInstructionLoader(), settings, time);

        var first = await firstProvider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);
        var second = await secondProvider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);

        Assert.Contains(first.RuntimeContext.References, reference =>
            reference.Kind == "project_memory" && reference.Content.Contains("First workspace only", StringComparison.Ordinal));
        Assert.DoesNotContain(second.RuntimeContext.References, reference => reference.Kind == "project_memory");
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("encoding")]
    public async Task Invalid_optional_memory_is_reported_without_failing_the_run(string defect)
    {
        using var fixture = RuInstructionLoaderTestsRoot.Create();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, ".ru", "memory"));
        await File.WriteAllBytesAsync(Path.Combine(folder.FullName, "MEMORY.md"),
            defect == "oversize" ? new byte[8193] : [0xff]);
        var provider = new RuPromptContextProvider(fixture.Root, new RuInstructionLoader(),
            new StubSettingsStore(Settings()), new FixedTimeProvider(DateTimeOffset.UtcNow));
        var context = await provider.BuildAsync(TestRun(), new ModelContextSnapshot([], Array.Empty<byte>()), CancellationToken.None);
        Assert.Contains(context.RuntimeContext.References, x => x.Kind == "memory_skipped");
        Assert.DoesNotContain(context.RuntimeContext.References, x => x.Kind == "project_memory");
    }
}
