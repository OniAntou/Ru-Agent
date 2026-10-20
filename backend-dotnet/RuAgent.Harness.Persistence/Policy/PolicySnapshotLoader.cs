using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Persistence.Policy;

public sealed class PolicySnapshotLoader
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaximumDocumentBytes = 1024 * 1024;
    private const int MaximumMatcherBytes = 1024 * 1024;

    private readonly HarnessSqliteOptions _options;
    private readonly IHarnessKeyStore _keyStore;

    public PolicySnapshotLoader(HarnessSqliteOptions options, IHarnessKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyStore);
        _options = options;
        _keyStore = keyStore;
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

        // One read transaction spans the global, project, and run tables so a
        // concurrent rule write cannot produce a hybrid snapshot that never
        // existed in the database.
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var global = await LoadGlobalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var project = await LoadProjectAsync(connection, transaction, workspace.WorkspaceId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PolicyRule> run = Array.Empty<PolicyRule>();
        if (runId is not null)
        {
            var isActive = await IsRunActiveAsync(connection, transaction, runId.Value, cancellationToken).ConfigureAwait(false);
            if (isActive)
            {
                run = await LoadRunAsync(connection, transaction, runId.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task<IReadOnlyList<PolicyRule>> LoadGlobalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT rule_document FROM agent_global_rules";
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorkspaceId workspaceId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT rule_document FROM agent_project_rules WHERE workspace_id = $workspace_id";
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PolicyRule>> LoadRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT rule_document FROM agent_run_rules WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId.Value);
        var docs = await ReadDocumentsAsync(command, ct).ConfigureAwait(false);
        return await DecryptDocumentsAsync(docs, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsRunActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            string? expiresRaw = null;
            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expires_at_utc", out var exp) && exp.ValueKind != JsonValueKind.Null)
            {
                expiresRaw = exp.GetString()!;
                expiresAt = DateTimeOffset.Parse(expiresRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            var matcherEncrypted = root.GetProperty("matcher_encrypted");
            var nonce = Convert.FromBase64String(matcherEncrypted.GetProperty("nonce").GetString()!);
            var ciphertext = Convert.FromBase64String(matcherEncrypted.GetProperty("ciphertext").GetString()!);
            var tag = Convert.FromBase64String(matcherEncrypted.GetProperty("tag").GetString()!);
            var version = matcherEncrypted.GetProperty("version").GetInt32();
            if (version is not (PolicyMatcherProtection.LegacyVersion or PolicyMatcherProtection.CurrentVersion)
                || nonce.Length != NonceSize
                || tag.Length != TagSize
                || ciphertext.Length > MaximumMatcherBytes)
            {
                throw new HarnessStoreException("policy_matcher_envelope_invalid");
            }
            var aad = PolicyMatcherProtection.BuildAad(ruleId.Value, (int)scope, (int)decision, creationOrder, expiresRaw, version);
            var matcherJson = await DecryptMatcherAsync(ruleId, nonce, ciphertext, tag, aad, cancellationToken).ConfigureAwait(false);
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

    private async Task<byte[]> DecryptMatcherAsync(PolicyRuleId ruleId, byte[] nonce, byte[] ciphertext, byte[] tag, byte[] aad, CancellationToken ct)
    {
        var key = await _keyStore.GetOrCreateKeyAsync(ct).ConfigureAwait(false);
        try
        {
            if (key is not { Length: 32 }) throw new HarnessStoreException("harness_key_invalid");
            if (ciphertext.Length > MaximumMatcherBytes)
            {
                throw new HarnessStoreException("policy_matcher_envelope_invalid");
            }
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            try
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
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
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static async Task<IReadOnlyList<byte[]>> ReadDocumentsAsync(SqliteCommand command, CancellationToken ct)
    {
        var list = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(HarnessSqliteMapper.ReadBoundedBlob(
                reader,
                0,
                MaximumDocumentBytes,
                "policy_snapshot_invalid"));
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
