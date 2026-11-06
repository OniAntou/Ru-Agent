extern alias Broker;

using System.Text;
using RuAgent.Protocol;
using RuBackend.Harness.Hooks;
using RuBackend.Harness.Plugins;
using Xunit;
using McpConfiguration = Broker::RuAgent.Broker.Tools.Mcp.McpConfiguration;

namespace RuBackend.Tests.Harness;

/// <summary>ADR 0044: a plugin's hooks and MCP servers run only for the exact commands the user approved.</summary>
public sealed class PluginExecutableSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-plugin-surface-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "profile");
    private string AppData => Path.Combine(_root, "appdata");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_plugin_without_hooks_or_servers_needs_no_consent()
    {
        UserPlugins.Create("guide", "Guide", null, withInstructions: true, withSkill: false, AppData, Profile);

        var surface = Assert.Single(PluginExecutableSurface.ForAll(AppData, Profile));

        Assert.Equal(PluginConsentState.NotNeeded, surface.Consent);
        Assert.Null(surface.Digest);
        Assert.Empty(surface.Lines);
    }

    [Fact]
    public void Commands_run_only_while_enabled_and_approved_and_stop_when_they_change()
    {
        var plugin = PluginWithHook("check.cmd");
        Assert.Equal(PluginConsentState.NeedsApproval, Single().Consent);
        UserPlugins.SetEnabled(plugin, true, AppData);
        Assert.Empty(PluginExecutableSurface.ApprovedHooks(AppData, Profile));

        UserPlugins.SetApproval(plugin, Single().Digest, AppData);
        var hook = Assert.Single(PluginExecutableSurface.ApprovedHooks(AppData, Profile));
        Assert.Equal("check.cmd", hook.Command);
        Assert.Equal(Path.Combine(UserPlugins.LocalRoot(Profile), plugin), hook.PluginRoot);

        UserPlugins.SetEnabled(plugin, false, AppData);
        Assert.Empty(PluginExecutableSurface.ApprovedHooks(AppData, Profile));
        UserPlugins.SetEnabled(plugin, true, AppData);

        // The plugin edits its command: the approval no longer matches, so nothing runs.
        WriteHooks(plugin, "evil.cmd");
        Assert.Equal(PluginConsentState.Changed, Single().Consent);
        Assert.Empty(PluginExecutableSurface.ApprovedHooks(AppData, Profile));
    }

    [Fact]
    public void Formatting_does_not_change_the_digest_but_any_command_field_does()
    {
        var plugin = PluginWithHook("check.cmd");
        var original = Single().Digest;

        File.WriteAllText(HooksPath(plugin), """
            {
              "hooks": { "PreToolUse": [ { "command": "check.cmd", "matcher": "edit" } ] }
            }
            """);
        Assert.Equal(original, Single().Digest);

        File.WriteAllText(HooksPath(plugin), """{"hooks":{"PreToolUse":[{"command":"check.cmd","matcher":"edit|write"}]}}""");
        Assert.NotEqual(original, Single().Digest);
    }

    [Fact]
    public void Mcp_servers_are_renamed_into_the_plugin_and_environment_values_count_but_are_not_shown()
    {
        var plugin = PluginWithMcp("""{"servers":{"docs":{"command":"node.exe","args":["server.js"],"env":{"API_MODE":"read"}}}}""");
        var surface = Single();

        var server = Assert.Single(surface.Servers);
        Assert.Equal($"{plugin}-docs", server.Name);
        Assert.Equal(Path.Combine(UserPlugins.LocalRoot(Profile), plugin), server.WorkingDirectory);
        var line = Assert.Single(surface.Lines);
        Assert.Contains("node.exe server.js", line, StringComparison.Ordinal);
        Assert.Contains("API_MODE", line, StringComparison.Ordinal);
        Assert.DoesNotContain("read", line.Split("environment:")[1], StringComparison.Ordinal);

        PluginWithMcp("""{"servers":{"docs":{"command":"node.exe","args":["server.js"],"env":{"API_MODE":"write"}}}}""", plugin);
        Assert.NotEqual(surface.Digest, Single().Digest);
    }

    [Fact]
    public void A_server_name_too_long_once_prefixed_is_refused_with_a_reason()
    {
        PluginWithMcp("""{"servers":{"a-very-long-server-name-x":{"command":"x.exe"}}}""", "long-plugin-id-here");
        var surface = Single();

        Assert.Empty(surface.Servers);
        Assert.Contains(surface.Problems, problem => problem.Contains("longer than 32", StringComparison.Ordinal));
    }

    [Fact]
    public void The_users_own_server_wins_over_a_plugin_server_with_the_same_name()
    {
        var user = McpConfiguration.Parse("""{"servers":{"team-docs":{"command":"mine.exe"}}}""");
        PluginWithMcp("""{"servers":{"docs":{"command":"theirs.exe"},"extra":{"command":"extra.exe"}}}""", "team");

        var merged = McpConfiguration.Merge(user, Single().Servers);

        Assert.Equal("mine.exe", merged.Find("team-docs")!.Command);
        Assert.Equal("extra.exe", merged.Find("team-extra")!.Command);
    }

    [Fact]
    public void Approved_plugin_hooks_load_after_the_users_and_before_the_workspaces()
    {
        var plugin = PluginWithHook("plugin.cmd");
        UserPlugins.SetEnabled(plugin, true, AppData);
        UserPlugins.SetApproval(plugin, Single().Digest, AppData);
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        Write(Path.Combine(Profile, ".ru", "hooks.json"), """{"hooks":{"PreToolUse":[{"command":"user.cmd"}]}}""");
        Write(Path.Combine(workspace, ".ru", "hooks.json"), """{"hooks":{"PreToolUse":[{"command":"workspace.cmd"}]}}""");

        var trusted = HookConfiguration.Load(workspace, Profile, AppData).For(HookEvent.PreToolUse, "edit");
        var untrusted = HookConfiguration.Load(null, Profile, AppData).For(HookEvent.PreToolUse, "edit");

        Assert.Equal(["user.cmd", "plugin.cmd", "workspace.cmd"], trusted.Select(hook => hook.Command));
        // Plugins are user-level: they run before the workspace is trusted, like the user's own hooks.
        Assert.Equal(["user.cmd", "plugin.cmd"], untrusted.Select(hook => hook.Command));
    }

    private PluginExecutableSurface Single() => Assert.Single(PluginExecutableSurface.ForAll(AppData, Profile));

    private string PluginWithHook(string command, string id = "checks")
    {
        if (!Directory.Exists(Path.Combine(UserPlugins.LocalRoot(Profile), id)))
            UserPlugins.Create(id, "Checks", null, withInstructions: false, withSkill: false, AppData, Profile);
        WriteHooks(id, command);
        return id;
    }

    private void WriteHooks(string id, string command) =>
        File.WriteAllText(HooksPath(id), "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"edit\",\"command\":\"" + command + "\"}]}}");

    private string HooksPath(string id) => Path.Combine(UserPlugins.LocalRoot(Profile), id, "hooks.json");

    private string PluginWithMcp(string json, string id = "servers")
    {
        if (!Directory.Exists(Path.Combine(UserPlugins.LocalRoot(Profile), id)))
            UserPlugins.Create(id, "Servers", null, withInstructions: false, withSkill: false, AppData, Profile);
        File.WriteAllText(Path.Combine(UserPlugins.LocalRoot(Profile), id, "mcp.json"), json);
        return id;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
