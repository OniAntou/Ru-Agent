using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Agent;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    // Policy endpoints
    public async Task<AgentServiceResult> ListPoliciesAsync(string? scopeRaw, string? workspaceIdRaw, CancellationToken cancellationToken)
    {
        var scope = scopeRaw?.Trim().ToLowerInvariant();
        if (scope is not null && scope != "global" && scope != "project" && scope != "run" && scope != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (string.Equals(scope, "global", StringComparison.Ordinal))
            {
                var globals = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
                var items = globals.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "project", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(workspaceIdRaw))
                {
                    return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
                }

                var trimmedWorkspaceId = workspaceIdRaw.Trim();
                if (!IsWorkspaceId(trimmedWorkspaceId))
                {
                    return BoundedError(400, "invalid_workspace", "workspace_id invalid.");
                }

                var wsId = new WorkspaceId(trimmedWorkspaceId);
                var list = await store.ListProjectAsync(wsId, cancellationToken).ConfigureAwait(false);
                var items = list.Select(MapPolicyRule).ToList();
                return new AgentServiceResult(200, new { items });
            }

            if (string.Equals(scope, "run", StringComparison.Ordinal) || string.Equals(scope, "once", StringComparison.Ordinal))
            {
                return BoundedError(400, "invalid_scope", "run/once listing not supported via this endpoint.");
            }

            // default: list global
            var g = await store.ListGlobalAsync(cancellationToken).ConfigureAwait(false);
            var defaultItems = g.Select(MapPolicyRule).ToList();
            return new AgentServiceResult(200, new { items = defaultItems });
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_list_failed", ex.Message);
        }
    }

    public async Task<AgentServiceResult> CreatePolicyAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_request", "Body must be object.");
        }

        // Older clients wrapped the rule in a `rule` property.  Accept that
        // shape only when it is the sole top-level property, otherwise a
        // caller could smuggle ignored fields alongside the actual rule.
        if (body.TryGetProperty("rule", out var wrappedRule))
        {
            if (wrappedRule.ValueKind != JsonValueKind.Object
                || body.EnumerateObject().Any(property => !string.Equals(property.Name, "rule", StringComparison.Ordinal)))
            {
                return BoundedError(400, "invalid_request", "rule must be the only top-level property.");
            }

            body = wrappedRule.Clone();
        }

        // Check for session_preset kind.  Unknown kinds must not silently fall
        // through to policy-rule parsing because that makes a typo change the
        // meaning of the request.
        if (body.TryGetProperty("kind", out var kindProp))
        {
            if (kindProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_kind", "kind must be policy_rule or session_preset.");
            }

            var kind = kindProp.GetString()?.Trim().ToLowerInvariant();
            if (kind == "session_preset")
            {
                return await HandleSessionPresetAsync(body, isInteractive, cancellationToken).ConfigureAwait(false);
            }

            if (kind != "policy_rule")
            {
                return BoundedError(400, "invalid_kind", "kind must be policy_rule or session_preset.");
            }
        }

        // Otherwise treat as policy rule creation (kind may be omitted for
        // compatibility with the original endpoint).
        // Validate scope
        if (!body.TryGetProperty("scope", out var scopeProp) || scopeProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_scope", "scope required.");
        }

        var scopeRaw = scopeProp.GetString()!.Trim().ToLowerInvariant();
        if (scopeRaw != "global" && scopeRaw != "project" && scopeRaw != "run" && scopeRaw != "once")
        {
            return BoundedError(400, "invalid_scope", "scope must be global, project, run, or once.");
        }

        if (scopeRaw == "global" && !isInteractive)
        {
            return BoundedError(403, "interactive_required", "Global rule requires interactive confirmation.");
        }

        if (!body.TryGetProperty("decision", out var decProp) || decProp.ValueKind != JsonValueKind.String)
        {
            return BoundedError(400, "invalid_decision", "decision required.");
        }

        var decRaw = decProp.GetString()!.Trim().ToLowerInvariant();
        if (decRaw != "allow" && decRaw != "ask" && decRaw != "deny")
        {
            return BoundedError(400, "invalid_decision", "decision must be allow, ask, or deny.");
        }

        if (!body.TryGetProperty("matcher", out var matcherProp) || matcherProp.ValueKind != JsonValueKind.Object)
        {
            return BoundedError(400, "invalid_matcher", "matcher required.");
        }

        var allowedMatcherFields = new HashSet<string>(StringComparer.Ordinal) { "tool_id", "tool_version", "canonical_path_pattern", "resolved_executable", "argv_pattern", "cwd_pattern", "operation_class", "network_intent" };
        foreach (var prop in matcherProp.EnumerateObject())
        {
            if (!allowedMatcherFields.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", "Unknown matcher field.");
            }
        }

        var allowedTop = new HashSet<string>(StringComparer.Ordinal) { "kind", "scope", "decision", "matcher", "workspace_id", "run_id", "creation_order", "expires_at" };
        foreach (var prop in body.EnumerateObject())
        {
            if (!allowedTop.Contains(prop.Name))
            {
                return BoundedError(400, "unknown_field", "Unknown policy field.");
            }
        }

        string? toolId = null;
        if (!TryReadPolicyString(matcherProp, "tool_id", 128, out toolId))
        {
            return BoundedError(400, "invalid_matcher", "tool_id must be a non-empty string or null.");
        }

        string? toolVersion = null;
        if (!TryReadPolicyString(matcherProp, "tool_version", 32, out toolVersion))
        {
            return BoundedError(400, "invalid_matcher", "tool_version must be a non-empty string or null.");
        }

        string? pathPattern = null;
        if (!TryReadPolicyString(matcherProp, "canonical_path_pattern", 4096, out pathPattern))
        {
            return BoundedError(400, "invalid_matcher", "canonical_path_pattern must be a non-empty string or null.");
        }

        string? resolvedExe = null;
        if (!TryReadPolicyString(matcherProp, "resolved_executable", 256, out resolvedExe))
        {
            return BoundedError(400, "invalid_matcher", "resolved_executable must be a non-empty string or null.");
        }

        IReadOnlyList<string>? argvPattern = null;
        if (matcherProp.TryGetProperty("argv_pattern", out var argvProp))
        {
            if (argvProp.ValueKind == JsonValueKind.Array)
            {
                if (argvProp.GetArrayLength() > 64)
                {
                    return BoundedError(400, "invalid_matcher", "argv_pattern has too many entries.");
                }

                var list = new List<string>();
                foreach (var el in argvProp.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String)
                    {
                        return BoundedError(400, "invalid_matcher", "argv_pattern must be string array.");
                    }

                    var value = el.GetString();
                    if (value is null || value.Length == 0 || value.Length > 4096 || string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
                    {
                        return BoundedError(400, "invalid_matcher", "argv_pattern contains an invalid entry.");
                    }

                    list.Add(value);
                }

                argvPattern = list;
            }
            else if (argvProp.ValueKind != JsonValueKind.Null)
            {
                return BoundedError(400, "invalid_matcher", "argv_pattern must be array or null.");
            }
        }

        string? cwdPattern = null;
        if (!TryReadPolicyString(matcherProp, "cwd_pattern", 4096, out cwdPattern))
        {
            return BoundedError(400, "invalid_matcher", "cwd_pattern must be a non-empty string or null.");
        }

        string? opClass = null;
        if (!TryReadPolicyString(matcherProp, "operation_class", 64, out opClass))
        {
            return BoundedError(400, "invalid_matcher", "operation_class must be a non-empty string or null.");
        }

        string? netIntent = null;
        if (!TryReadPolicyString(matcherProp, "network_intent", 64, out netIntent))
        {
            return BoundedError(400, "invalid_matcher", "network_intent must be a non-empty string or null.");
        }

        string? workspaceIdRaw = null;
        if (!TryReadPolicyString(body, "workspace_id", 64, out workspaceIdRaw))
        {
            return BoundedError(400, "invalid_workspace", "workspace_id must be a string or null.");
        }

        string? runIdRaw = null;
        if (!TryReadPolicyString(body, "run_id", 64, out runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id must be a string or null.");
        }

        long creationOrder = 0;
        if (body.TryGetProperty("creation_order", out var creationProp))
        {
            if (creationProp.ValueKind != JsonValueKind.Number
                || !creationProp.TryGetInt64(out creationOrder)
                || creationOrder < 0)
            {
                return BoundedError(400, "invalid_creation_order", "creation_order must be a non-negative integer.");
            }
        }

        DateTimeOffset? expiresAt = null;
        if (body.TryGetProperty("expires_at", out var expiresProp))
        {
            if (expiresProp.ValueKind == JsonValueKind.Null)
            {
                expiresAt = null;
            }
            else if (expiresProp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    expiresProp.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedExpiry)
                || parsedExpiry == default)
            {
                return BoundedError(400, "invalid_expiry", "expires_at must be an ISO-8601 timestamp or null.");
            }
            else
            {
                expiresAt = parsedExpiry.ToUniversalTime();
            }
        }

        PolicyRuleScope scopeEnum = scopeRaw switch
        {
            "global" => PolicyRuleScope.Global,
            "project" => PolicyRuleScope.Project,
            "run" => PolicyRuleScope.Run,
            "once" => PolicyRuleScope.Once,
            _ => throw new InvalidOperationException()
        };

        PolicyDecision decisionEnum = decRaw switch
        {
            "allow" => PolicyDecision.Allow,
            "ask" => PolicyDecision.Ask,
            "deny" => PolicyDecision.Deny,
            _ => throw new InvalidOperationException()
        };

        WorkspaceId? wsId = null;
        RunId? runId = null;
        if (scopeEnum == PolicyRuleScope.Project)
        {
            if (string.IsNullOrWhiteSpace(workspaceIdRaw)) return BoundedError(400, "invalid_workspace", "workspace_id required for project scope.");
            if (!IsWorkspaceId(workspaceIdRaw.Trim())) return BoundedError(400, "invalid_workspace", "workspace_id invalid.");
            if (runIdRaw is not null) return BoundedError(400, "invalid_scope", "project scope must not have run_id.");
            wsId = new WorkspaceId(workspaceIdRaw.Trim());
            var ws = await _workspaceStore.GetAsync(wsId.Value, cancellationToken).ConfigureAwait(false);
            if (ws is null) return BoundedError(404, "workspace_not_found", "Workspace not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Run || scopeEnum == PolicyRuleScope.Once)
        {
            if (string.IsNullOrWhiteSpace(runIdRaw)) return BoundedError(400, "invalid_run_id", "run_id required.");
            if (!IsRunId(runIdRaw.Trim())) return BoundedError(400, "invalid_run_id", "run_id invalid.");
            if (workspaceIdRaw is not null) return BoundedError(400, "invalid_scope", "run/once scope must not have workspace_id.");
            runId = new RunId(runIdRaw.Trim());
            var run = await _store.GetRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            if (run is null) return BoundedError(404, "run_not_found", "Run not found.");
        }
        else if (scopeEnum == PolicyRuleScope.Global)
        {
            if (workspaceIdRaw is not null || runIdRaw is not null) return BoundedError(400, "invalid_scope", "global scope must not have workspace/run.");
        }

        PolicyMatcher matcher;
        try
        {
            matcher = new PolicyMatcher(toolId, toolVersion, pathPattern, resolvedExe, argvPattern, cwdPattern, opClass, netIntent);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return BoundedError(400, "invalid_matcher", "Policy matcher is invalid.");
        }

        var ruleId = _ids.NewPolicyRuleId();
        var rule = new PolicyRule(ruleId, scopeEnum, decisionEnum, matcher, creationOrder, expiresAt);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);
        try
        {
            if (scopeEnum == PolicyRuleScope.Global) await store.CreateAsync(rule, null, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else if (scopeEnum == PolicyRuleScope.Project) await store.CreateAsync(rule, wsId, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
            else await store.CreateAsync(rule, null, runId, _time.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code, "Unable to create policy.");
        }

        var respMatcher = new
        {
            tool_id = toolId,
            tool_version = toolVersion,
            canonical_path_pattern = pathPattern,
            resolved_executable = resolvedExe,
            argv_pattern = argvPattern,
            cwd_pattern = cwdPattern,
            operation_class = opClass,
            network_intent = netIntent
        };
        return new AgentServiceResult(201, new { rule_id = ruleId.Value, scope = scopeRaw, decision = decRaw, matcher = respMatcher, creation_order = creationOrder, expires_at = expiresAt?.ToString("O", CultureInfo.InvariantCulture), version = 1 });
    }

    private async Task<AgentServiceResult> HandleSessionPresetAsync(JsonElement body, bool isInteractive, CancellationToken cancellationToken)
    {
        try
        {
            var allowedFields = new HashSet<string>(StringComparer.Ordinal)
            {
                "kind", "session_id", "preset", "expected_version"
            };
            foreach (var property in body.EnumerateObject())
            {
                if (!allowedFields.Contains(property.Name))
                {
                    return BoundedError(400, "unknown_field", "Unknown session preset field.");
                }
            }

            if (!body.TryGetProperty("session_id", out var sessProp) || sessProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_session_id", "session_id required.");
            }

            var sessionIdRaw = sessProp.GetString()!.Trim();
            if (!IsSessionId(sessionIdRaw))
            {
                return BoundedError(400, "invalid_session_id", "session_id invalid.");
            }

            if (!body.TryGetProperty("preset", out var presetProp) || presetProp.ValueKind != JsonValueKind.String)
            {
                return BoundedError(400, "invalid_preset", "preset required.");
            }

            var presetRaw = presetProp.GetString()!.Trim().ToLowerInvariant();
            AgentPermissionPreset preset;
            if (presetRaw == "standard") preset = AgentPermissionPreset.Standard;
            else if (presetRaw == "full_access" || presetRaw == "full") preset = AgentPermissionPreset.FullAccess;
            else if (presetRaw == "trusted") preset = AgentPermissionPreset.Trusted;
            else return BoundedError(400, "invalid_preset", "preset must be standard, full_access, or trusted.");

            long expectedVersion = 0;
            if (body.TryGetProperty("expected_version", out var verProp))
            {
                if (verProp.ValueKind == JsonValueKind.Number && verProp.TryGetInt64(out var v)) expectedVersion = v;
                else if (verProp.ValueKind == JsonValueKind.String && long.TryParse(verProp.GetString(), out var sv)) expectedVersion = sv;
                else if (verProp.ValueKind != JsonValueKind.Null) return BoundedError(400, "invalid_preset_version", "expected_version invalid.");
            }

            if (expectedVersion < 0)
            {
                return BoundedError(400, "invalid_preset_version", "expected_version must be non-negative.");
            }

            if ((preset == AgentPermissionPreset.FullAccess || preset == AgentPermissionPreset.Trusted) && !isInteractive)
            {
                return BoundedError(403, "interactive_required", "Full Access and Trusted presets require interactive confirmation.");
            }

            var sessionDoc = await _sessions.GetAsync(sessionIdRaw, cancellationToken).ConfigureAwait(false);
            if (sessionDoc is null)
            {
                return BoundedError(404, "session_not_found", "Session not found.");
            }

            var now = _time.UtcNow;
            var result = await _permissionStore.SetInteractivelyAsync(new SessionId(sessionIdRaw), preset, expectedVersion, now, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == SessionPermissionOutcome.VersionConflict)
            {
                return BoundedError(409, "preset_version_conflict", "preset_version_conflict");
            }

            if (result.Selection is null)
            {
                return BoundedError(500, "preset_update_failed", "Failed to update preset.");
            }

            var sel = result.Selection;
            var presetWire = sel.Preset switch
            {
                AgentPermissionPreset.Standard => "standard",
                AgentPermissionPreset.FullAccess => "full_access",
                AgentPermissionPreset.Trusted => "trusted",
                _ => "standard",
            };
            return new AgentServiceResult(200, new { session_id = sel.SessionId.Value, preset = presetWire, version = sel.Version, updated_at = sel.UpdatedAt.ToString("O") });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(400, ex.Code, "Unable to update session permissions.");
        }
        catch (Exception)
        {
            return BoundedError(500, "preset_internal_error", "Unable to update session permissions.");
        }
    }

    public async Task<AgentServiceResult> DeletePolicyAsync(string ruleIdRaw, bool isInteractive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ruleIdRaw) || !IsPolicyRuleId(ruleIdRaw.Trim()))
        {
            return BoundedError(400, "invalid_rule_id", "rule_id invalid.");
        }

        var trimmed = ruleIdRaw.Trim();
        var ruleId = new PolicyRuleId(trimmed);
        var store = new SqlitePolicyRuleStore(_options, _keyStore);

        PolicyDeleteResult deleteResult;
        try
        {
            // Atomic scope-resolve + delete in a single store transaction.
            // No separate check-then-delete, so a concurrent writer cannot slip
            // a global rule past the interactive gate (TOCTOU). Non-interactive
            // callers fail closed for globals without deleting.
            deleteResult = await store.DeleteByIdAtomicAsync(ruleId, isInteractive, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessStoreException ex)
        {
            return BoundedError(500, ex.Code ?? "policy_delete_failed", ex.Message);
        }

        return deleteResult.Outcome switch
        {
            PolicyDeleteOutcome.NotFound => BoundedError(404, "rule_not_found", "Rule not found."),
            PolicyDeleteOutcome.GlobalRequiresInteractive => BoundedError(403, "interactive_required", "Global rule deletion requires interactive confirmation."),
            PolicyDeleteOutcome.DeletedGlobal => new AgentServiceResult(200, new { rule_id = trimmed, deleted = true }),
            PolicyDeleteOutcome.DeletedProject => new AgentServiceResult(200, new { rule_id = trimmed, deleted = true }),
            PolicyDeleteOutcome.DeletedRun => new AgentServiceResult(200, new { rule_id = trimmed, deleted = true }),
            _ => BoundedError(500, "policy_delete_failed", "Unable to delete policy."),
        };
    }

    private static object MapPolicyRule(PolicyRule rule)
    {
        var scope = rule.Scope switch
        {
            PolicyRuleScope.Global => "global",
            PolicyRuleScope.Project => "project",
            PolicyRuleScope.Run => "run",
            PolicyRuleScope.Once => "once",
            _ => "unknown"
        };
        var decision = rule.Decision switch
        {
            PolicyDecision.Allow => "allow",
            PolicyDecision.Ask => "ask",
            PolicyDecision.Deny => "deny",
            _ => "unknown"
        };
        var m = rule.Matcher;
        return new
        {
            rule_id = rule.RuleId.Value,
            scope,
            decision,
            matcher = new
            {
                tool_id = m.ToolId,
                tool_version = m.ToolVersion,
                canonical_path_pattern = m.CanonicalPathPattern,
                resolved_executable = m.ResolvedExecutable,
                argv_pattern = m.ArgvPattern,
                cwd_pattern = m.CwdPattern,
                operation_class = m.OperationClass,
                network_intent = m.NetworkIntent
            },
            creation_order = rule.CreationOrder,
            expires_at = rule.ExpiresAt?.ToString("O"),
            version = 1
        };
    }

    // Host-side one-time approval ledger for HTTP approvals (first-writer wins).
    // Entries are only a retry guard; the durable approval event/run state remains
    // authoritative. Expire old entries so a host that processes many approvals
    // cannot retain an unbounded amount of memory until restart.

    private static bool TryReadPolicyString(JsonElement obj, string propertyName, int maxLength, out string? value) =>
        AgentV1Shared.TryReadPolicyString(obj, propertyName, maxLength, out value);
}
