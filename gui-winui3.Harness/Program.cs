using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RuApp.Harness;

internal static class Program
{
    private static readonly string[] Scenarios =
    [
        "turn",
        "stop",
        "cancel",
        "stale-session",
        "agent-volume-contract",
        "agent-cancel",
        "agent-stale-result",
        "agent-private-metadata",
    ];

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = HarnessOptions.Parse(args);
            if (!File.Exists(options.AppPath) || !File.Exists(options.SessionPath))
            {
                return WriteResult(new HarnessGuiResult(
                    2,
                    options.Scenario,
                    [new HarnessGuiCheck("gui.input", "failed", 0, "app or session descriptor is missing")],
                    new Dictionary<string, int>(),
                    false,
                    false));
            }

            string processName = Path.GetFileNameWithoutExtension(options.AppPath);
            if (Process.GetProcessesByName(processName).Length > 0)
            {
                return WriteResult(new HarnessGuiResult(
                    2,
                    options.Scenario,
                    [new HarnessGuiCheck("gui.existing_instance", "failed", 0, "existing RuApp instance detected")],
                    new Dictionary<string, int>(),
                    true,
                    false));
            }

            string eventsPath = ReadEventsPath(options.SessionPath);
            var startInfo = new ProcessStartInfo(options.AppPath)
            {
                WorkingDirectory = Path.GetDirectoryName(options.AppPath)!,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--ru-harness-session");
            startInfo.ArgumentList.Add(options.SessionPath);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("RuApp could not be started.");
            using var scenario = new UiAutomationScenario(process, eventsPath);
            var checks = new List<HarnessGuiCheck>
            {
                await scenario.WaitForBackendReadyAsync(options.Timeout),
            };

            if (checks[^1].Status == "passed")
            {
                checks.Add(await RunScenarioAsync(scenario, options));
                checks.Add(await scenario.AssertPrivateMetadataAsync());
            }

            await scenario.CloseAsync(options.Timeout);
            return WriteResult(new HarnessGuiResult(
                2,
                options.Scenario,
                checks,
                scenario.ReadEventCounts(),
                false,
                process.HasExited));
        }
        catch (Exception ex)
        {
            return WriteResult(new HarnessGuiResult(
                2,
                "unknown",
                [new HarnessGuiCheck("gui.driver", "failed", 0, ex.GetType().Name)],
                new Dictionary<string, int>(),
                false,
                false));
        }
    }

    private static async Task<HarnessGuiCheck> RunScenarioAsync(
        UiAutomationScenario scenario,
        HarnessOptions options)
    {
        if (options.Scenario.StartsWith("agent-", StringComparison.Ordinal))
        {
            return await scenario.RunAgentScenarioAsync(options.Scenario, options.Timeout);
        }

        HarnessGuiCheck start = await scenario.StartVoiceAsync(options.Timeout);
        if (start.Status != "passed")
        {
            return start;
        }

        return options.Scenario switch
        {
            "turn" => await RunTurnAsync(scenario, options.Timeout),
            "stop" => await scenario.StopVoiceAsync(options.Timeout),
            "cancel" => await scenario.CancelVoiceAsync(options.Timeout),
            "stale-session" => await RunStaleSessionAsync(scenario, options.Timeout),
            _ => new HarnessGuiCheck("gui.scenario", "failed", 0, "unsupported scenario"),
        };
    }

    private static async Task<HarnessGuiCheck> RunTurnAsync(
        UiAutomationScenario scenario,
        TimeSpan timeout)
    {
        HarnessGuiCheck first = await scenario.WaitForVoiceTurnAsync(timeout);
        if (first.Status != "passed")
        {
            return first;
        }

        return await scenario.RequestSecondTurnAsync(timeout);
    }

    private static async Task<HarnessGuiCheck> RunStaleSessionAsync(
        UiAutomationScenario scenario,
        TimeSpan timeout)
    {
        HarnessGuiCheck replaced = await scenario.ReplaceSessionAsync(timeout);
        if (replaced.Status != "passed")
        {
            return replaced;
        }

        return await scenario.AssertNoLateUpdateAsync(TimeSpan.FromMilliseconds(500));
    }

    private static string ReadEventsPath(string sessionPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sessionPath));
        string? eventsPath = document.RootElement.GetProperty("events_path").GetString();
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            throw new InvalidDataException("Harness session does not define an event path.");
        }

        string descriptorRoot = Path.GetDirectoryName(Path.GetFullPath(sessionPath))!;
        string fullEventsPath = Path.GetFullPath(eventsPath);
        string relative = Path.GetRelativePath(descriptorRoot, fullEventsPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Harness event path escapes the session root.");
        }

        return fullEventsPath;
    }

    private static int WriteResult(HarnessGuiResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }));
        return result.Checks.All(check => check.Status == "passed") ? 0 : 1;
    }

    private sealed record HarnessOptions(
        string AppPath,
        string SessionPath,
        string Scenario,
        TimeSpan Timeout)
    {
        public static HarnessOptions Parse(string[] args)
        {
            string? app = null;
            string? session = null;
            string scenario = "turn";
            TimeSpan timeout = TimeSpan.FromSeconds(30);
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--app":
                        app = Next(args, ref i);
                        break;
                    case "--session":
                        session = Next(args, ref i);
                        break;
                    case "--scenario":
                        scenario = Next(args, ref i).ToLowerInvariant();
                        break;
                    case "--timeout":
                        if (!double.TryParse(Next(args, ref i), out double seconds)
                            || seconds <= 0 || seconds > 300)
                        {
                            throw new ArgumentException("--timeout must be between 0 and 300 seconds.");
                        }

                        timeout = TimeSpan.FromSeconds(seconds);
                        break;
                    default:
                        throw new ArgumentException("Unknown Harness argument.");
                }
            }

            if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(session))
            {
                throw new ArgumentException("--app and --session are required.");
            }

            if (!Scenarios.Contains(scenario, StringComparer.Ordinal))
            {
                throw new ArgumentException("Harness scenario is not allowed.");
            }

            return new HarnessOptions(
                Path.GetFullPath(app),
                Path.GetFullPath(session),
                scenario,
                timeout);
        }

        private static string Next(string[] args, ref int index)
        {
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("Harness argument value is missing.");
            }

            return args[index];
        }
    }
}
