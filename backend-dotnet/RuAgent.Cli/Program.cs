using RuAgent.Cli;
using RuAgent.Client;

namespace RuAgent.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Best effort when redirected
        }

        var parseResult = AgentCliParser.Parse(args);
        if (!parseResult.Success || parseResult.Command is null)
        {
            Console.Error.WriteLine(parseResult.ErrorCode is not null
                ? $"{parseResult.ErrorCode}: {parseResult.ErrorMessage}"
                : "parse_error: invalid arguments");
            PrintUsage();
            return 1;
        }

        var command = parseResult.Command;
        // PublicHostStarter ensures host running before any API call.
        // Use project root as workspace canonical parent or current directory for starter.
        var projectRoot = ResolveProjectRoot(command);
        var starter = new PublicHostStarter();
        var httpClient = new HttpClient();

        AgentCommands commands;
        try
        {
            // Use starter to get authenticated session; include cancellation for Ctrl+C
            using var starterCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var session = await starter.GetOrStartAsync(projectRoot, starterCts.Token).ConfigureAwait(false);
            commands = new AgentCommands(session, httpClient, Console.Out, Console.Error);
        }
        catch (AgentApiException ex)
        {
            Console.Error.WriteLine($"{ex.ErrorCode}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"public_host_start_failed: {ex.Message}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return command switch
            {
                RunCommand run => await commands.RunAsync(run, cts.Token).ConfigureAwait(false),
                AttachCommand attach => await commands.AttachAsync(attach, cts.Token).ConfigureAwait(false),
                TrustCommand trust => await commands.TrustAsync(trust, cts.Token).ConfigureAwait(false),
                CancelCommand cancel => await commands.CancelAsync(cancel, cts.Token).ConfigureAwait(false),
                ContinueCommand cont => await commands.ContinueAsync(cont, cts.Token).ConfigureAwait(false),
                StatusCommand status => await commands.StatusAsync(status, cts.Token).ConfigureAwait(false),
                ApproveCommand approve => await commands.ApproveAsync(approve, cts.Token).ConfigureAwait(false),
                DenyCommand deny => await commands.DenyAsync(deny, cts.Token).ConfigureAwait(false),
                PolicyListCommand list => await commands.PolicyListAsync(list, cts.Token).ConfigureAwait(false),
                PolicyImportCommand import => await commands.PolicyImportAsync(import, cts.Token).ConfigureAwait(false),
                PolicyRemoveCommand remove => await commands.PolicyRemoveAsync(remove, cts.Token).ConfigureAwait(false),
                ReconcileCommand reconcile => await commands.ReconcileAsync(reconcile, cts.Token).ConfigureAwait(false),
                OutputCommand output => await commands.OutputAsync(output, cts.Token).ConfigureAwait(false),
                DispatchCommandCommand dispatch => await commands.DispatchCommandAsync(dispatch, cts.Token).ConfigureAwait(false),
                ChatCommand chat => await commands.ChatAsync(chat, cts.Token).ConfigureAwait(false),
                SessionListCommand slist => await commands.SessionListAsync(slist, cts.Token).ConfigureAwait(false),
                SessionForkCommand sfork => await commands.SessionForkAsync(sfork, cts.Token).ConfigureAwait(false),
                SessionRewindCommand srewind => await commands.SessionRewindAsync(srewind, cts.Token).ConfigureAwait(false),
                _ => 1
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
    }

    private static string ResolveProjectRoot(AgentCommand command)
    {
        string? envRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot)
            && (File.Exists(Path.Combine(envRoot, "pyproject.toml")) || Directory.Exists(Path.Combine(envRoot, "backend-dotnet"))))
        {
            return Path.GetFullPath(envRoot);
        }

        var cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "pyproject.toml")) || Directory.Exists(Path.Combine(cwd, "backend-dotnet")))
        {
            return Path.GetFullPath(cwd);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml")) || Directory.Exists(Path.Combine(dir.FullName, "backend-dotnet")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  agent run [--preset standard|full_access] --workspace PATH \"prompt\" [--session <id>] [--json]");
        Console.Error.WriteLine("  agent chat [--preset standard|full_access] [--workspace PATH] [--session <id>] [--json]");
        Console.Error.WriteLine("  agent session list [--json]");
        Console.Error.WriteLine("  agent session fork <session-id> [--index <n>] [--title <title>] [--json]");
        Console.Error.WriteLine("  agent session rewind <session-id> --index <n> [--json]");
        Console.Error.WriteLine("  agent attach <run-id> [--json]");
        Console.Error.WriteLine("  agent trust <run-id> --approve|--deny");
        Console.Error.WriteLine("  agent approve <run-id> <approval-id>");
        Console.Error.WriteLine("  agent deny <run-id> <approval-id>");
        Console.Error.WriteLine("  agent cancel <run-id>");
        Console.Error.WriteLine("  agent continue <run-id>");
        Console.Error.WriteLine("  agent status <run-id> [--json]");
        Console.Error.WriteLine("  agent policy list [--global | --workspace PATH] [--json]");
        Console.Error.WriteLine("  agent policy import --global FILE");
        Console.Error.WriteLine("  agent policy remove <rule-id>");
        Console.Error.WriteLine("  agent command <run-id> \"/help|/skills|/compact ...|/skill ...\" [--json]");
    }
}
