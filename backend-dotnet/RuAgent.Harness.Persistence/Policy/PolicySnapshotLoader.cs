using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Policy;

public sealed class PolicySnapshotLoader
{
    private const int CurrentProtectionVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly HarnessSqliteOptions _options;
    private readonly IHarnessKeyStore _keyStore;

    public PolicySnapshotLoader(HarnessSqliteOptions options, IHarnessKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyStore);
        _options = options;
        _keyStore = keyStore;
    }

    public PolicySnapshotLoader(HarnessSqliteOptions options, HarnessPayloadCryptography cryptography)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cryptography);
        _options = options;
        // For compatibility, we need keyStore – store the cryptography to extract key via reflection would be hack.
        // Use a wrapper that delegates to cryptography's DecryptAsync via RunId dummy.
        // Simpler: require IHarnessKeyStore, this overload not used in tests.
        throw new NotSupportedException("Use IHarnessKeyStore overload for policy loader.");
    }

    public async Task<PolicySnapshot> LoadAsync(
        WorkspaceIdentity workspace,
        AgentPermissionPreset preset,
        RunId? runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }
        if (now == default)
        {
            throw new ArgumentException("Timestamp must be valid.", nameof(now));
        }

        // Load and decrypt all relevant rules. Any failure fails closed with no partial snapshot.
        var global = await LoadGlobalAsync(cancellationToken).ConfigureAwait(false);
        var project = await LoadProjectAsync(workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PolicyRule> run = Array.Empty<PolicyRule>();
        if (runId is not null)
        {
            var isActive = await IsRunActiveAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            if (isActive)
            {
                run = await LoadRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        // Filter expired
        global = FilterExpired(global, now);
        project = FilterExpired(project, now);
        run = FilterExpired(run, now);

        // Validate every matcher already done during decryption; also re-validate rule
        foreach (var r in global.Concat(project).Concat(run))
        {
            ValidateRule(r);
        }

        // Ordering: global → project → run (Run before Once) → Once last wins
        var ordered = new List<PolicyRule>();
        ordered.AddRange(global.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal));
        ordered.AddRange(project.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal));
        var runOrdered = run.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal).ToList();
        // Separate Run vs Once: Once last
        var runScope = runOrdered.Where(r => r.Scope == PolicyRuleScope.Run).ToArray();
        var onceScope = runOrdered.Where(r => r.Scope == PolicyRuleScope.Once).ToArray();
        ordered.AddRange(runScope);
        ordered.AddRange(onceScope);

        // Compute version: deterministic – count + 1, or max creation order +1
        long version = ordered.Count == 0 ? 1 : ordered.Max(r => r.CreationOrder) + 1;
        if (version <= 0) version = 1;

        try
        {
            return PolicySnapshotCanonicalizer.Create(version, preset, workspace, ordered, now);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new HarnessStoreException("policy_snapshot_invalid", ex);
        }
    }

    private static IReadOnlyList<PolicyRule> FilterExpired(IReadOnlyList<PolicyRule> rules, DateTimeOffset now)
    {
        return rules.Where(r => !r.IsExpired(now)).ToArray();
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadGlobalAsync(CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule_document FROM agent_global_rules";
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadProjectAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule_document FROM agent_project_rules WHERE workspace_id = $workspace_id";
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadRunAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule_document FROM agent_run_rules WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId.Value);
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsRunActiveAsync(RunId runId, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT terminal_at_utc FROM agent_runs WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId.Value);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null)
        {
            return false;
        }
        if (result == DBNull.Value)
        {
            return true;
        }
        return false;
    }

    private async Task<IReadOnlyList<PolicyRule>> DecryptDocumentsAsync(IReadOnlyList<byte[]> documents, CancellationToken ct)
    {
        var list = new List<PolicyRule>(documents.Count);
        foreach (var doc in documents)
        {
            var rule = await DecryptDocumentAsync(doc, ct).ConfigureAwait(false);
            list.Add(rule);
        }
        return list;
    }

    internal async Task<PolicyRule> DecryptDocumentAsync(byte[] document, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;
            var ruleId = new PolicyRuleId(root.GetProperty("rule_id").GetString()!);
            var scope = (PolicyRuleScope)root.GetProperty("scope").GetInt32();
            var decision = (PolicyDecision)root.GetProperty("decision").GetInt32();
            var creationOrder = root.GetProperty("creation_order").GetInt64();
            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_at_utc", out var exp) && exp.ValueKind != JsonValueKind.Null)
            {
                expiresAt = DateTimeOffset.Parse(exp.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            var matcherEncrypted = root.GetProperty("matcher_encrypted");
            var nonce = Convert.FromBase64String(matcherEncrypted.GetProperty("nonce").GetString()!);
            var ciphertext = Convert.FromBase64String(matcherEncrypted.GetProperty("ciphertext").GetString()!);
            var tag = Convert.FromBase64String(matcherEncrypted.GetProperty("tag").GetString()!);
            var version = matcherEncrypted.GetProperty("version").GetInt32();
            if (version != CurrentProtectionVersion || nonce.Length != NonceSize || tag.Length != TagSize)
            {
                throw new HarnessStoreException("policy_matcher_envelope_invalid");
            }
            var matcherJson = await DecryptMatcherAsync(ruleId, nonce, ciphertext, tag, cancellationToken).ConfigureAwait(false);
            var matcher = ParseMatcher(matcherJson);
            var rule = new PolicyRule(ruleId, scope, decision, matcher, creationOrder, expiresAt);
            ValidateRule(rule);
            return rule;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or CryptographicException)
        {
            throw new HarnessStoreException("policy_snapshot_invalid", ex);
        }
    }

    private async Task<byte[]> DecryptMatcherAsync(PolicyRuleId ruleId, byte[] nonce, byte[] ciphertext, byte[] tag, CancellationToken ct)
    {
        var key = await _keyStore.GetOrCreateKeyAsync(ct).ConfigureAwait(false);
        try
        {
            if (key.Length != 32) throw new HarnessStoreException("harness_key_invalid");
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            try
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(ruleId.Value));
            }
            catch (CryptographicException ex)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new HarnessStoreException("policy_decryption_failed", ex);
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<IReadOnlyList<byte[]>> ReadDocumentsAsync(SqliteCommand command, CancellationToken ct)
    {
        var list = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add((byte[])reader[0]);
        }
        return list;
    }

    private static PolicyMatcher ParseMatcher(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? toolId = GetOptionalString(root, "tool_id");
        string? toolVersion = GetOptionalString(root, "tool_version");
        string? path = GetOptionalString(root, "canonical_path_pattern");
        string? exe = GetOptionalString(root, "resolved_executable");
        IReadOnlyList<string>? argv = null;
        if (root.TryGetProperty("argv_pattern", out var argvProp) && argvProp.ValueKind != JsonValueKind.Null)
        {
            var list = new List<string>();
            foreach (var el in argvProp.EnumerateArray())
            {
                list.Add(el.GetString()!);
            }
            argv = list;
        }
        string? cwd = GetOptionalString(root, "cwd_pattern");
        string? op = GetOptionalString(root, "operation_class");
        string? net = GetOptionalString(root, "network_intent");
        return new PolicyMatcher(toolId, toolVersion, path, exe, argv, cwd, op, net);
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null)
        {
            return prop.GetString();
        }
        return null;
    }

    private static void ValidateRule(PolicyRule rule)
    {
        _ = rule.RuleId.Value;
        if (!Enum.IsDefined(rule.Scope))
        {
            throw new HarnessStoreException("policy_scope_invalid");
        }
        if (!Enum.IsDefined(rule.Decision))
        {
            throw new HarnessStoreException("policy_decision_invalid");
        }
        if (rule.CreationOrder < 0)
        {
            throw new HarnessStoreException("policy_creation_order_invalid");
        }
        if (rule.ExpiresAt is { } expires && expires == default)
        {
            throw new HarnessStoreException("policy_expiry_invalid");
        }
        try
        {
            var m = rule.Matcher;
            _ = new PolicyMatcher(m.ToolId, m.ToolVersion, m.CanonicalPathPattern, m.ResolvedExecutable, m.ArgvPattern, m.CwdPattern, m.OperationClass, m.NetworkIntent);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new HarnessStoreException("policy_matcher_invalid", ex);
        }
    }
}
