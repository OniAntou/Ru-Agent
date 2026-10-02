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
        using var starter = new PublicHostStarter();
        using var httpClient = new HttpClient();
        // The API client keeps its request timeout; an SSE connection is intentionally
        // long-lived and is bounded by the caller token instead, so it gets its own
        // client with no timeout rather than inheriting the API one.
        using var sseHttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        AgentCommands commands;
        try
        {
            // Use starter to get authenticated session; include cancellation for Ctrl+C
            using var starterCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var session = await starter.GetOrStartAsync(projectRoot, starterCts.Token).ConfigureAwait(false);
            commands = new AgentCommands(session, httpClient, Console.Out, Console.Error, sseHttpClient: sseHttpClient);
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
        var userCancelled = false;
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            userCancelled = true;
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
                RpcCommand rpc => await commands.RpcAsync(rpc, cts.Token).ConfigureAwait(false),
                SessionListCommand slist => await commands.SessionListAsync(slist, cts.Token).ConfigureAwait(false),
                SessionRewindCommand srewind => await commands.SessionRewindAsync(srewind, cts.Token).ConfigureAwait(false),
                SessionDeleteCommand sdelete => await commands.SessionDeleteAsync(sdelete, cts.Token).ConfigureAwait(false),
                SessionExportCommand sexport => await commands.SessionExportAsync(sexport, cts.Token).ConfigureAwait(false),
                SessionImportCommand simport => await commands.SessionImportAsync(simport, cts.Token).ConfigureAwait(false),
                _ => 1
            };
        }
        catch (AgentApiException ex)
        {
            Console.Error.WriteLine(CliErrorText.Format(ex));
            return 1;
        }
        catch (OperationCanceledException) when (userCancelled)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (OperationCanceledException)
        {
            // TaskCanceledException from an HttpClient timeout derives from
            // OperationCanceledException; only the operator's own Ctrl+C is a cancel.
            Console.Error.WriteLine("request_timeout: the request did not complete in time.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected_error: {ex.Message}");
            return 1;
        }
        finally
        {
            commands.Dispose();
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
        Console.Error.WriteLine("  agent run [--preset plan|standard|full_access] --workspace PATH \"prompt\" [--session <id>] [--image PATH]... [--json] [--trust-workspace] [--confirm-full-access]");
        Console.Error.WriteLine("  agent chat [--preset plan|standard|full_access] [--workspace PATH] [--session <id>] [--image PATH]... [--json] [--classic] [--trust-workspace] [--confirm-full-access]");
        Console.Error.WriteLine("  --image: attach an image file (repeatable, max 4); also /attach, /attachments, /detach and ctrl+v in chat.");
        Console.Error.WriteLine("  --confirm-full-access: terminal runs as the current Windows user and may access data outside the workspace.");
        Console.Error.WriteLine("  --trust-workspace: explicitly approve the returned workspace identity; failed rollback stops before streaming.");
        Console.Error.WriteLine("  agent session list [--json]");
        Console.Error.WriteLine("  agent session rewind <session-id> --index <n> [--json]");
        Console.Error.WriteLine("  agent session delete <session-id> [--yes]");
        Console.Error.WriteLine("  agent attach <run-id> [--json]");
        Console.Error.WriteLine("  agent trust <run-id> --approve|--deny");
        Console.Error.WriteLine("  agent approve <run-id> <approval-id>");
        Console.Error.WriteLine("  agent deny <run-id> <approval-id>");
        Console.Error.WriteLine("  agent cancel <run-id>");
        Console.Error.WriteLine("  agent continue <run-id>");
        Console.Error.WriteLine("  agent status <run-id> [--json]");
        Console.Error.WriteLine("  agent policy list [--global] [--json]");
        Console.Error.WriteLine("  agent policy import --global FILE");
        Console.Error.WriteLine("  agent policy remove <rule-id>");
        Console.Error.WriteLine("  agent command <run-id> \"/help|/skills|/compact ...|/skill ...\" [--json]");
        Console.Error.WriteLine("  agent rpc # stdio JSON-RPC 2.0 bridge for external tools");
    }
}
