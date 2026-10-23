using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatSessionSettlementWiringTests
{
    [Fact]
    public void Chat_session_retains_run_settlement_across_create_and_waits_after_ui_completion()
    {
        var root = FindRepositoryRoot();
        var prompt = File.ReadAllText(Path.Combine(root, "backend-dotnet", "RuAgent.Cli", "ChatSession.Prompt.cs"));
        var terminalGui = File.ReadAllText(Path.Combine(root, "backend-dotnet", "RuAgent.Cli", "ChatSession.TerminalGui.cs"));

        var retained = prompt.IndexOf("runSettlement = runSession.RetainRunSettlement();", StringComparison.Ordinal);
        var create = prompt.IndexOf("_agentClient.CreateRunAsync(request, runSession.Token)", StringComparison.Ordinal);
        var finallyBlock = create < 0 ? -1 : prompt.IndexOf("finally", create, StringComparison.Ordinal);
        var released = prompt.IndexOf("runSettlement?.Dispose();", StringComparison.Ordinal);
        Assert.True(retained >= 0 && retained < create && create < finallyBlock && finallyBlock < released,
            "Run identity must be retained before create and released only from the prompt handler's finally path.");

        Assert.Contains("loop.SetShutdownSettlement(runSession.WaitForRunSettlementAsync);", terminalGui, StringComparison.Ordinal);
        var uiCompletion = terminalGui.IndexOf("await loop.Completion.ConfigureAwait(false);", StringComparison.Ordinal);
        var settlementWait = terminalGui.IndexOf("await loop.WaitForShutdownSettlementAsync().ConfigureAwait(false);", StringComparison.Ordinal);
        Assert.True(uiCompletion >= 0 && uiCompletion < settlementWait,
            "The chat owner must await settlement after the bounded UI completion before returning.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output directory.");
    }
}
