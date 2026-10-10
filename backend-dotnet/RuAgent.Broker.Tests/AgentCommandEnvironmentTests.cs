using System.Text.Json;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class AgentCommandEnvironmentTests
{
    private static Dictionary<string, string> SnapshotWith(params (string Key, string Value)[] entries)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            snapshot[key] = value;
        }

        return snapshot;
    }

    [Fact]
    public void Copies_required_windows_and_developer_values_from_the_parent_snapshot()
    {
        var snapshot = SnapshotWith(
            ("SystemRoot", @"C:\WINDOWS"),
            ("WINDIR", @"C:\WINDOWS"),
            ("TEMP", @"C:\Temp"),
            ("TMP", @"C:\Temp"),
            ("USERPROFILE", @"C:\Users\dev"),
            ("HOME", @"C:\Users\dev"),
            ("LOCALAPPDATA", @"C:\Users\dev\AppData\Local"),
            ("APPDATA", @"C:\Users\dev\AppData\Roaming"),
            ("ProgramData", @"C:\ProgramData"),
            ("ProgramFiles", @"C:\Program Files"),
            ("ProgramFiles(x86)", @"C:\Program Files (x86)"),
            ("ProgramW6432", @"C:\Program Files"),
            ("PATH", @"C:\Tools;C:\Windows"),
            ("PATHEXT", ".COM;.EXE;.BAT;.CMD"),
            ("COMSPEC", @"C:\WINDOWS\system32\cmd.exe"),
            ("PROCESSOR_ARCHITECTURE", "AMD64"),
            ("NUMBER_OF_PROCESSORS", "8"),
            ("DOTNET_ROOT", @"C:\dotnet"),
            ("NUGET_PACKAGES", @"C:\packages"),
            ("PIP_CACHE_DIR", @"C:\pip-cache"),
            ("PYTHONHOME", @"C:\python"),
            ("PYTHONPATH", @"C:\python\Lib"),
            ("VIRTUAL_ENV", @"C:\venv"));

        var environment = new AgentCommandEnvironment(snapshot);

        foreach (var (key, value) in snapshot)
        {
            Assert.True(environment.ChildEnvironment.TryGetValue(key, out var copied), $"missing {key}");
            Assert.Equal(value, copied);
        }
    }

    [Fact]
    public void Strips_bearer_bearing_and_secret_named_entries_case_insensitively()
    {
        var secretValue = "super-confidential-value";
        var snapshot = SnapshotWith(
            ("PATH", @"C:\Windows"),
            ("RU_BEARER_TOKEN", secretValue),
            ("ru_session_token", secretValue),
            ("WORKER_BEARER", secretValue),
            ("BROKER_AUTH", secretValue),
            ("DESKTOP_GATEWAY_TOKEN", secretValue),
            ("MY_API_KEY", secretValue),
            ("provider_key", secretValue),
            ("DB_CONNECTION_STRING", secretValue),
            ("SERVICE_CREDENTIAL", secretValue),
            ("CLIENT_PASSWD", secretValue),
            ("MY_PASSWORD", secretValue),
            ("PRIVATE_KEY_PEM", secretValue),
            ("OLD_ACCESS_KEY", secretValue),
            ("REFRESH_KEY_ID", secretValue),
            ("AUTH_KEY_VALUE", secretValue),
            ("OPENROUTER_API_KEY", secretValue));

        var environment = new AgentCommandEnvironment(snapshot);

        foreach (var key in snapshot.Keys.Where(key => !key.Equals("PATH", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.False(
                environment.ChildEnvironment.ContainsKey(key),
                $"{key} must be stripped from the child environment");
        }

        Assert.Contains("PATH", environment.ChildEnvironment.Keys);
        Assert.Equal(16, environment.StrippedNames.Count);
    }

    [Fact]
    public void Adds_fixed_non_interactive_child_values_without_touching_the_parent_process()
    {
        var userprofileBefore = Environment.GetEnvironmentVariable("USERPROFILE");
        var homeBefore = Environment.GetEnvironmentVariable("HOME");
        try
        {
            var snapshot = SnapshotWith(("PATH", @"C:\Windows"));
            var environment = new AgentCommandEnvironment(snapshot);

            Assert.Equal("0", environment.ChildEnvironment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("Never", environment.ChildEnvironment["GCM_INTERACTIVE"]);
            Assert.Equal("1", environment.ChildEnvironment["PYTHONUTF8"]);
            Assert.Equal("utf-8", environment.ChildEnvironment["PYTHONIOENCODING"]);
            Assert.False(environment.ChildEnvironment.ContainsKey("USERPROFILE"));
            Assert.False(environment.ChildEnvironment.ContainsKey("HOME"));

            Assert.Equal(userprofileBefore, Environment.GetEnvironmentVariable("USERPROFILE"));
            Assert.Equal(homeBefore, Environment.GetEnvironmentVariable("HOME"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", userprofileBefore);
            Environment.SetEnvironmentVariable("HOME", homeBefore);
        }
    }

    [Fact]
    public void Parent_supplied_fixed_values_cannot_weaken_non_interactive_defaults()
    {
        var snapshot = SnapshotWith(
            ("PATH", @"C:\Windows"),
            ("GIT_TERMINAL_PROMPT", "1"),
            ("GCM_INTERACTIVE", "Always"),
            ("PYTHONUTF8", "0"));

        var environment = new AgentCommandEnvironment(snapshot);

        Assert.Equal("0", environment.ChildEnvironment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("Never", environment.ChildEnvironment["GCM_INTERACTIVE"]);
        Assert.Equal("1", environment.ChildEnvironment["PYTHONUTF8"]);
    }

    [Fact]
    public void Values_are_captured_at_construction_not_read_lazily_after_execution_begins()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = @"C:\original-temp",
        };
        var environment = new AgentCommandEnvironment(snapshot);
        snapshot["TEMP"] = @"C:\mutated-after-capture";

        Assert.Equal(@"C:\original-temp", environment.ChildEnvironment["TEMP"]);
    }

    [Fact]
    public void Diagnostics_expose_names_and_counts_but_never_values()
    {
        var secretValue = "super-confidential-value";
        var snapshot = SnapshotWith(
            ("PATH", @"C:\Windows"),
            ("RU_BEARER_TOKEN", secretValue),
            ("MY_API_KEY", secretValue));
        var environment = new AgentCommandEnvironment(snapshot);

        var diagnostics = environment.Describe();

        Assert.Equal(5, diagnostics.AllowedCount);
        Assert.Equal(2, diagnostics.StrippedCount);
        var serialized = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain(secretValue, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redaction_seeds_cover_all_stripped_secret_values_without_inheriting_them()
    {
        var ruBearer = "ru-bearer-123";
        var workerToken = "worker-token-456";
        var thirdPartyKey = "third-party-key-789";
        var openRouterKey = "openrouter-key-012";
        var snapshot = SnapshotWith(
            ("PATH", @"C:\Windows"),
            ("RU_BEARER_TOKEN", ruBearer),
            ("WORKER_TOKEN", workerToken),
            ("THIRD_PARTY_API_KEY", thirdPartyKey),
            ("OPENROUTER_API_KEY", openRouterKey));
        var environment = new AgentCommandEnvironment(snapshot);

        var seeds = environment.GetRedactionSeeds();

        Assert.Contains(ruBearer, seeds);
        Assert.Contains(workerToken, seeds);
        Assert.Contains(thirdPartyKey, seeds);
        Assert.Contains(openRouterKey, seeds);
        Assert.DoesNotContain("OPENROUTER_API_KEY", environment.ChildEnvironment.Keys);
        Assert.DoesNotContain(openRouterKey, environment.ChildEnvironment.Values);
    }
}
