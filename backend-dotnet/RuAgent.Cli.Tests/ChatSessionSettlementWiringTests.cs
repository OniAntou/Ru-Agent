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
        var manager = File.ReadAllText(Path.Combine(root, "backend-dotnet", "RuAgent.Cli", "TuiSessionManager.cs"));

        var retained = prompt.IndexOf("runSettlement = runSession.RetainRunSettlement();", StringComparison.Ordinal);
        var create = prompt.IndexOf("_agentClient.CreateRunAsync(request, runSession.Token)", StringComparison.Ordinal);
        var finallyBlock = create < 0 ? -1 : prompt.IndexOf("finally", create, StringComparison.Ordinal);
        var released = prompt.IndexOf("runSettlement?.Dispose();", StringComparison.Ordinal);
        Assert.True(retained >= 0 && retained < create && create < finallyBlock && finallyBlock < released,
            "Run identity must be retained before create and released only from the prompt handler's finally path.");

        Assert.Contains("loop.SetShutdownSettlement(() => Task.WhenAll(", terminalGui, StringComparison.Ordinal);
        Assert.Contains("runSession.WaitForRunSettlementAsync(), attachmentTasks.StopAsync()));", terminalGui, StringComparison.Ordinal);
        var uiCompletion = manager.IndexOf("await Task.WhenAll(sessions.Select(session => session.tgLoop!.Completion))", StringComparison.Ordinal);
        var settlementWait = manager.IndexOf("await Task.WhenAll(sessions.Select(session => session.tgLoop!.WaitForShutdownSettlementAsync()))", StringComparison.Ordinal);
        Assert.True(uiCompletion >= 0 && uiCompletion < settlementWait,
            "The chat owner must await settlement after the bounded UI completion before returning.");
        Assert.True(manager.IndexOf("session.runSession.Dispose();", StringComparison.Ordinal) > settlementWait,
            "Background session owners must remain alive until every create settlement has completed.");
        Assert.True(manager.IndexOf("session.submissions.ClearPending();", StringComparison.Ordinal) > settlementWait,
            "Pending attachments must remain owned until all attachment handlers have finished.");
        Assert.Contains("&& !session.attachmentTasks.HasPending", manager, StringComparison.Ordinal);
        var stopping = manager.IndexOf("session.managedShutdown = true;", StringComparison.Ordinal);
        var stopAttachments = manager.IndexOf("session.attachmentTasks.StopAsync();", stopping, StringComparison.Ordinal);
        var cancelLifetime = manager.IndexOf("_lifetime.Cancel();", stopping, StringComparison.Ordinal);
        Assert.True(stopping >= 0 && stopping < stopAttachments && stopAttachments < cancelLifetime,
            "Shutdown must stop attachment admission before cancelling its existing handlers.");
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
