using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Broker.Tools.Mcp;
using RuAgent.Protocol;
using RuBackend.Harness.Hooks;

namespace RuBackend.Harness.Plugins;

/// <summary>Whether a plugin's hooks and MCP servers may run (ADR 0044).</summary>
public enum PluginConsentState
{
    /// <summary>The plugin declares nothing that runs.</summary>
    NotNeeded,
    /// <summary>It declares commands the user has not approved yet.</summary>
    NeedsApproval,
    /// <summary>The user approved exactly the commands it declares now.</summary>
    Approved,
    /// <summary>The user approved an earlier set of commands; the plugin changed since.</summary>
    Changed,
}

/// <summary>
/// The commands a plugin would run: its hooks and MCP servers, parsed with the same rules
/// as the user's own <c>hooks.json</c> and <c>mcp.json</c>, the lines shown to the user
/// when asking for consent, and the SHA-256 of their canonical form. The digest covers
/// every field that changes what runs (environment values included); the lines show
/// environment variable names only, since values may be secrets.
/// </summary>
public sealed record PluginExecutableSurface(
    UserPlugin Plugin,
    IReadOnlyList<HookDefinition> Hooks,
    IReadOnlyList<McpServerDefinition> Servers,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> Problems,
    string? Digest,
    PluginConsentState Consent)
{
    public bool IsEmpty => Hooks.Count == 0 && Servers.Count == 0;

    public static PluginExecutableSurface Read(UserPlugin plugin, IReadOnlyDictionary<string, string> approvals)
    {
        var problems = new List<string>();
        var hooks = HookConfiguration.LoadFile(Path.Combine(plugin.Directory, "hooks.json"))
            .Select(hook => hook with { PluginRoot = plugin.Directory })
            .ToArray();
        var servers = new List<McpServerDefinition>();
        foreach (var server in McpConfiguration.LoadFile(Path.Combine(plugin.Directory, "mcp.json")).Servers)
        {
            var name = $"{plugin.Id}-{server.Name}";
            if (!McpConfiguration.IsValidServerName(name))
            {
                problems.Add($"MCP server {server.Name}: the name {name} is longer than 32 characters, so it is not loaded.");
                continue;
            }

            servers.Add(server with { Name = name, WorkingDirectory = ResolveDirectory(plugin.Directory, server.WorkingDirectory) });
        }

        if (hooks.Length == 0 && servers.Count == 0)
            return new(plugin, hooks, servers, [], problems, null, PluginConsentState.NotNeeded);

        var digest = DigestOf(hooks, servers);
        var consent = !approvals.TryGetValue(plugin.Id, out var approved) ? PluginConsentState.NeedsApproval
            : string.Equals(approved, digest, StringComparison.Ordinal) ? PluginConsentState.Approved
            : PluginConsentState.Changed;
        return new(plugin, hooks, servers, Describe(hooks, servers), problems, digest, consent);
    }

    /// <summary>Every plugin with its surface, as the Customize page shows them.</summary>
    public static IReadOnlyList<PluginExecutableSurface> ForAll(
        string? localApplicationDataRoot = null, string? userProfileRoot = null, List<string>? skipped = null)
    {
        var approvals = UserPlugins.ReadApprovals(localApplicationDataRoot);
        return UserPlugins.Discover(localApplicationDataRoot, userProfileRoot, skipped)
            .Select(plugin => Read(plugin, approvals))
            .ToArray();
    }

    /// <summary>Hooks of enabled plugins whose current surface the user approved; everything else is suspended.</summary>
    public static IReadOnlyList<HookDefinition> ApprovedHooks(string? localApplicationDataRoot = null, string? userProfileRoot = null) =>
        Approved(localApplicationDataRoot, userProfileRoot).SelectMany(surface => surface.Hooks).ToArray();

    /// <summary>MCP servers of enabled plugins whose current surface the user approved.</summary>
    public static IReadOnlyList<McpServerDefinition> ApprovedServers(string? localApplicationDataRoot = null, string? userProfileRoot = null) =>
        Approved(localApplicationDataRoot, userProfileRoot).SelectMany(surface => surface.Servers).ToArray();

    private static IEnumerable<PluginExecutableSurface> Approved(string? localApplicationDataRoot, string? userProfileRoot)
    {
        try
        {
            return ForAll(localApplicationDataRoot, userProfileRoot)
                .Where(surface => surface.Plugin.Enabled && surface.Consent == PluginConsentState.Approved)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unreadable plugin folder runs nothing; it never blocks the user's own hooks or servers.
            return [];
        }
    }

    private static string? ResolveDirectory(string pluginDirectory, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return pluginDirectory;
        return Path.IsPathFullyQualified(configured) ? configured : Path.GetFullPath(Path.Combine(pluginDirectory, configured));
    }

    private static string DigestOf(IReadOnlyList<HookDefinition> hooks, IReadOnlyList<McpServerDefinition> servers)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            hooks = hooks.Select(hook => new
            {
                @event = hook.Event.ToString(),
                matcher = hook.ToolPattern,
                command = hook.Command,
                timeout_seconds = (int)hook.Timeout.TotalSeconds,
            }),
            servers = servers.Select(server => new
            {
                name = server.Name,
                transport = server.Transport.ToString(),
                command = server.Command,
                args = server.Arguments,
                cwd = server.WorkingDirectory,
                url = server.Endpoint?.ToString(),
                bearer_credential = server.BearerCredential,
                timeout_seconds = (int)server.CallTimeout.TotalSeconds,
                env = server.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { name = pair.Key, literal = pair.Value.Literal, credential = pair.Value.Credential }),
            }),
        });
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static IReadOnlyList<string> Describe(IReadOnlyList<HookDefinition> hooks, IReadOnlyList<McpServerDefinition> servers)
    {
        var lines = new List<string>();
        foreach (var hook in hooks)
        {
            var matcher = string.IsNullOrEmpty(hook.ToolPattern) ? "every tool" : $"tools matching {hook.ToolPattern}";
            var scope = hook.Event is HookEvent.PreToolUse or HookEvent.PostToolUse ? $" for {matcher}" : string.Empty;
            lines.Add($"Hook {hook.Event}{scope}: {hook.Command} (up to {(int)hook.Timeout.TotalSeconds} s)");
        }

        foreach (var server in servers)
        {
            var target = server.Transport == McpServerTransport.StreamableHttp
                ? $"connects to {server.Endpoint}"
                : $"runs {string.Join(' ', new[] { server.Command }.Concat(server.Arguments))} in {server.WorkingDirectory}";
            var environment = server.Environment.Count == 0 ? string.Empty
                : " · environment: " + string.Join(", ", server.Environment.Keys.Order(StringComparer.Ordinal));
            lines.Add($"MCP server {server.Name}: {target}{environment}");
        }

        return lines;
    }
}
