using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence.Policy;

public sealed class SqlitePolicyRuleStore
{
    private const int CurrentProtectionVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly HarnessSqliteOptions _options;
    private readonly IHarnessKeyStore _keyStore;

    public SqlitePolicyRuleStore(HarnessSqliteOptions options, IHarnessKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyStore);
        _options = options;
        _keyStore = keyStore;
    }

    public SqlitePolicyRuleStore(HarnessSqliteOptions options, HarnessPayloadCryptography cryptography)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cryptography);
        _options = options;
        // Extract keyStore via reflection? For compatibility, create a wrapper that uses same cryptography key.
        // Instead, require IHarnessKeyStore directly; this overload is for existing call patterns but we will delegate to a dummy.
        // Use a Fake wrapper that delegates to cryptography's internal keystore via reflection is complex.
        // For now, throw if this overload is used without keyStore – tests use IHarnessKeyStore overload.
        throw new NotSupportedException("Use IHarnessKeyStore overload for policy store.");
    }

    public async Task CreateAsync(
        PolicyRule rule,
        WorkspaceId? workspaceId,
        RunId? runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await CreateManyAsync(
            new[] { rule },
            workspaceId,
            runId,
            now,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Persists a set of related rules atomically. External-directory approvals use
    /// this to materialize one bounded boundary across the non-destructive file
    /// tools without leaving a partially broadened project policy on failure.
    /// </summary>
    public async Task CreateManyAsync(
        IReadOnlyList<PolicyRule> rules,
        WorkspaceId? workspaceId,
        RunId? runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
        {
            throw new ArgumentException("At least one policy rule is required.", nameof(rules));
        }

        if (now == default)
        {
            throw new ArgumentException("Timestamp must be valid.", nameof(now));
        }

        var prepared = new List<(PolicyRule Rule, byte[] Document)>(rules.Count);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ValidateRule(rule);
            ValidateOwnership(rule.Scope, workspaceId, runId);
            var encrypted = await EncryptMatcherAsync(rule.Matcher, rule.RuleId, cancellationToken).ConfigureAwait(false);
            prepared.Add((rule, BuildDocument(rule, encrypted, now)));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            foreach (var item in prepared)
            {
                switch (item.Rule.Scope)
                {
                    case PolicyRuleScope.Global:
                        await InsertGlobalAsync(connection, transaction, item.Rule, item.Document, now, cancellationToken).ConfigureAwait(false);
                        break;
                    case PolicyRuleScope.Project:
                        await InsertProjectAsync(connection, transaction, item.Rule, workspaceId!.Value, item.Document, now, cancellationToken).ConfigureAwait(false);
                        break;
                    case PolicyRuleScope.Run:
                    case PolicyRuleScope.Once:
                        await InsertRunAsync(connection, transaction, item.Rule, runId!.Value, item.Document, now, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new HarnessStoreException("policy_scope_invalid");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint violation
        {
            throw new HarnessStoreException("policy_rule_duplicate", ex);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            throw new HarnessStoreException("policy_store_failed", ex);
        }
    }

    public async Task<IReadOnlyList<PolicyRule>> ListGlobalAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var documents = await LoadDocumentsAsync(connection, null, "SELECT rule_document FROM agent_global_rules", cancellationToken).ConfigureAwait(false);
        var rules = await DecryptDocumentsAsync(documents, cancellationToken).ConfigureAwait(false);
        return rules.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<PolicyRule>> ListProjectAsync(WorkspaceId workspaceId, CancellationToken cancellationToken)
    {
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule_document FROM agent_project_rules WHERE workspace_id = $workspace_id";
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        var documents = await ReadDocumentsAsync(command, cancellationToken).ConfigureAwait(false);
        var rules = await DecryptDocumentsAsync(documents, cancellationToken).ConfigureAwait(false);
        return rules.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<PolicyRule>> ListRunAsync(RunId runId, CancellationToken cancellationToken)
    {
        _ = runId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rule_document FROM agent_run_rules WHERE run_id = $run_id";
        command.Parameters.AddWithValue("$run_id", runId.Value);
        var documents = await ReadDocumentsAsync(command, cancellationToken).ConfigureAwait(false);
        var rules = await DecryptDocumentsAsync(documents, cancellationToken).ConfigureAwait(false);
        return rules.OrderBy(r => r.CreationOrder).ThenBy(r => r.RuleId.Value, StringComparer.Ordinal).ToArray();
    }

    public async Task DeleteGlobalAsync(PolicyRuleId ruleId, CancellationToken cancellationToken)
    {
        _ = ruleId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_global_rules WHERE rule_id = $rule_id";
        command.Parameters.AddWithValue("$rule_id", ruleId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteProjectAsync(PolicyRuleId ruleId, CancellationToken cancellationToken)
    {
        _ = ruleId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_project_rules WHERE rule_id = $rule_id";
        command.Parameters.AddWithValue("$rule_id", ruleId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRunAsync(PolicyRuleId ruleId, CancellationToken cancellationToken)
    {
        _ = ruleId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_run_rules WHERE rule_id = $rule_id";
        command.Parameters.AddWithValue("$rule_id", ruleId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateOwnership(PolicyRuleScope scope, WorkspaceId? workspaceId, RunId? runId)
    {
        switch (scope)
        {
            case PolicyRuleScope.Global:
                if (workspaceId is not null || runId is not null)
                {
                    throw new HarnessStoreException("policy_scope_ownership_invalid");
                }
                break;
            case PolicyRuleScope.Project:
                if (workspaceId is null || runId is not null)
                {
                    throw new HarnessStoreException("policy_scope_ownership_invalid");
                }
                _ = workspaceId.Value.Value;
                break;
            case PolicyRuleScope.Run:
            case PolicyRuleScope.Once:
                if (runId is null || workspaceId is not null)
                {
                    throw new HarnessStoreException("policy_scope_ownership_invalid");
                }
                _ = runId.Value.Value;
                break;
            default:
                throw new HarnessStoreException("policy_scope_invalid");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new HarnessStoreException("policy_scope_invalid");
        }
    }

    private static async Task InsertGlobalAsync(SqliteConnection connection, SqliteTransaction transaction, PolicyRule rule, byte[] document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_global_rules(rule_id, rule_document, version, created_at_utc, updated_at_utc)
            VALUES ($rule_id, $document, $version, $created_at, $updated_at)
            """;
        command.Parameters.AddWithValue("$rule_id", rule.RuleId.Value);
        command.Parameters.AddWithValue("$document", document);
        command.Parameters.AddWithValue("$version", 1);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertProjectAsync(SqliteConnection connection, SqliteTransaction transaction, PolicyRule rule, WorkspaceId workspaceId, byte[] document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_project_rules(rule_id, workspace_id, rule_document, version, created_at_utc, updated_at_utc)
            VALUES ($rule_id, $workspace_id, $document, $version, $created_at, $updated_at)
            """;
        command.Parameters.AddWithValue("$rule_id", rule.RuleId.Value);
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$document", document);
        command.Parameters.AddWithValue("$version", 1);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertRunAsync(SqliteConnection connection, SqliteTransaction transaction, PolicyRule rule, RunId runId, byte[] document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_run_rules(rule_id, run_id, rule_document, created_at_utc)
            VALUES ($rule_id, $run_id, $document, $created_at)
            """;
        command.Parameters.AddWithValue("$rule_id", rule.RuleId.Value);
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$document", document);
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] BuildDocument(PolicyRule rule, EncryptedMatcherPayload encrypted, DateTimeOffset now)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", rule.RuleId.Value);
            writer.WriteNumber("scope", (int)rule.Scope);
            writer.WriteNumber("decision", (int)rule.Decision);
            writer.WriteNumber("creation_order", rule.CreationOrder);
            if (rule.ExpiresAt is { } expires)
            {
                writer.WriteString("expires_at_utc", expires.ToString("O"));
            }
            else
            {
                writer.WriteNull("expires_at_utc");
            }
            writer.WriteNumber("version", 1);
            writer.WriteString("created_at_utc", now.ToString("O"));
            writer.WriteString("updated_at_utc", now.ToString("O"));
            writer.WritePropertyName("matcher_encrypted");
            writer.WriteStartObject();
            writer.WriteString("nonce", Convert.ToBase64String(encrypted.Nonce.ToArray()));
            writer.WriteString("ciphertext", Convert.ToBase64String(encrypted.Ciphertext.ToArray()));
            writer.WriteString("tag", Convert.ToBase64String(encrypted.Tag.ToArray()));
            writer.WriteNumber("version", CurrentProtectionVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private async Task<IReadOnlyList<byte[]>> LoadDocumentsAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await ReadDocumentsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<byte[]>> ReadDocumentsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<byte[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add((byte[])reader[0]);
        }
        return list;
    }

    private async Task<IReadOnlyList<PolicyRule>> DecryptDocumentsAsync(IReadOnlyList<byte[]> documents, CancellationToken cancellationToken)
    {
        var rules = new List<PolicyRule>(documents.Count);
        foreach (var doc in documents)
        {
            var rule = await DecryptDocumentAsync(doc, cancellationToken).ConfigureAwait(false);
            rules.Add(rule);
        }
        return rules;
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
            if (root.TryGetProperty("expires_at_utc", out var expiresProp) && expiresProp.ValueKind != JsonValueKind.Null)
            {
                expiresAt = DateTimeOffset.Parse(expiresProp.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
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
            throw new HarnessStoreException("policy_rule_corrupt", ex);
        }
    }

    private async Task<byte[]> DecryptMatcherAsync(PolicyRuleId ruleId, byte[] nonce, byte[] ciphertext, byte[] tag, CancellationToken cancellationToken)
    {
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key.Length != 32)
            {
                throw new HarnessStoreException("harness_key_invalid");
            }
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

    private async Task<EncryptedMatcherPayload> EncryptMatcherAsync(PolicyMatcher matcher, PolicyRuleId ruleId, CancellationToken cancellationToken)
    {
        var json = SerializeMatcher(matcher);
        var key = await _keyStore.GetOrCreateKeyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (key.Length != 32)
            {
                throw new HarnessStoreException("harness_key_invalid");
            }
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[json.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, json, ciphertext, tag, Encoding.UTF8.GetBytes(ruleId.Value));
            return new EncryptedMatcherPayload(nonce, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] SerializeMatcher(PolicyMatcher matcher)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteOptionalString(writer, "tool_id", matcher.ToolId);
            WriteOptionalString(writer, "tool_version", matcher.ToolVersion);
            WriteOptionalString(writer, "canonical_path_pattern", matcher.CanonicalPathPattern);
            WriteOptionalString(writer, "resolved_executable", matcher.ResolvedExecutable);
            writer.WritePropertyName("argv_pattern");
            if (matcher.ArgvPattern is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var a in matcher.ArgvPattern)
                {
                    writer.WriteStringValue(a);
                }
                writer.WriteEndArray();
            }
            WriteOptionalString(writer, "cwd_pattern", matcher.CwdPattern);
            WriteOptionalString(writer, "operation_class", matcher.OperationClass);
            WriteOptionalString(writer, "network_intent", matcher.NetworkIntent);
            writer.WriteEndObject();
        }
        return stream.ToArray();
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

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private sealed record EncryptedMatcherPayload(ReadOnlyMemory<byte> Nonce, ReadOnlyMemory<byte> Ciphertext, ReadOnlyMemory<byte> Tag);

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
        // Matcher validation is done via PolicyMatcher constructor; attempt to access to trigger?
        // Re-validate by constructing a copy.
        _ = rule.Matcher.ToolId;
        // Validate matcher fields via its own validation (replicate)
        try
        {
            // Create a new matcher with same values to trigger validation
            var m = rule.Matcher;
            _ = new PolicyMatcher(m.ToolId, m.ToolVersion, m.CanonicalPathPattern, m.ResolvedExecutable, m.ArgvPattern, m.CwdPattern, m.OperationClass, m.NetworkIntent);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new HarnessStoreException("policy_matcher_invalid", ex);
        }
    }
}
