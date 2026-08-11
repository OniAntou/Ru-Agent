using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class HistoryStoreTests
{
    [Fact]
    public async Task ReadLegacy_filters_invalid_roles_and_non_string_content()
    {
        var (history, _, paths) = CreateStores();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.HistoryFile)!);
        await File.WriteAllTextAsync(paths.HistoryFile, """
            [
              {"role":"user","content":"prompt"},
              {"role":"ai","content":"response"},
              {"role":"system","content":"private"},
              {"role":"user","content":42}
            ]
            """);

        var messages = await history.ReadLegacyAsync(CancellationToken.None);

        Assert.Equal(2, messages.Count);
        Assert.Equal("prompt", messages[0].Content);
        Assert.Equal("response", messages[1].Content);
    }

    [Fact]
    public async Task Migrate_creates_verified_session_hash_manifest_and_timestamped_backup()
    {
        var (history, sessions, paths) = CreateStores();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.HistoryFile)!);
        var source = """
            [
              {"role":"user","content":"Old prompt"},
              {"role":"ai","content":"Old response"}
            ]
            """;
        await File.WriteAllTextAsync(paths.HistoryFile, source);
        var sourceBytes = await File.ReadAllBytesAsync(paths.HistoryFile);
        var expectedHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

        var sessionId = await history.MigrateToSessionAsync(CancellationToken.None);

        Assert.NotNull(sessionId);
        Assert.False(File.Exists(paths.HistoryFile));
        var migrated = await sessions.GetAsync(sessionId!, CancellationToken.None);
        Assert.Equal(["Old prompt", "Old response"], migrated!.Messages.Select(message => message.Content));

        var manifestPath = paths.HistoryFile + ".migrations.json";
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsArray();
        Assert.Single(manifest);
        Assert.Equal(expectedHash, manifest[0]!["source_sha256"]!.GetValue<string>());
        Assert.Equal(sessionId, manifest[0]!["session_id"]!.GetValue<string>());
        var backupFile = manifest[0]!["backup_file"]!.GetValue<string>();
        Assert.True(File.Exists(backupFile));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(backupFile));
    }

    [Fact]
    public async Task Migrate_keeps_legacy_file_when_a_session_already_exists()
    {
        var (history, sessions, paths) = CreateStores();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.HistoryFile)!);
        await File.WriteAllTextAsync(paths.HistoryFile, "[{\"role\":\"user\",\"content\":\"legacy\"}]");
        await sessions.CreateAsync(CancellationToken.None);

        var migrated = await history.MigrateToSessionAsync(CancellationToken.None);

        Assert.Null(migrated);
        Assert.True(File.Exists(paths.HistoryFile));
        Assert.Single(await sessions.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Migrate_keeps_legacy_file_when_session_append_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-history-tests", Guid.NewGuid().ToString("N"));
        var paths = CreatePaths(root);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(paths.HistoryFile, "[{\"role\":\"user\",\"content\":\"legacy\"}]");
        var sessions = new FailingSessionStore();
        var history = new HistoryStore(paths, sessions);

        var migrated = await history.MigrateToSessionAsync(CancellationToken.None);

        Assert.Null(migrated);
        Assert.True(File.Exists(paths.HistoryFile));
        Assert.Equal("created", sessions.DeletedSessionId);
    }

    private static (HistoryStore History, SessionStore Sessions, DataPaths Paths) CreateStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-history-tests", Guid.NewGuid().ToString("N"));
        var paths = CreatePaths(root);
        var sessions = new SessionStore(paths);
        return (new HistoryStore(paths, sessions), sessions, paths);
    }

    private static DataPaths CreatePaths(string root)
    {
        return new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "memory_texts.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
    }

    private sealed class FailingSessionStore : ISessionStore
    {
        public string? DeletedSessionId { get; private set; }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<SessionDocument?>(null);

        public Task<string?> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("created");

        public Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken)
        {
            DeletedSessionId = sessionId;
            return Task.FromResult(true);
        }

        public Task<bool> AppendAsync(
            string sessionId,
            IReadOnlyList<SessionMessage> messages,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
            string sessionId,
            int maxTurns,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
    }
}
