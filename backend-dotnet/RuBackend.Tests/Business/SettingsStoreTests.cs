using System.Text.Json.Nodes;
using RuBackend.Business;
using RuBackend.Business.Settings;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task Read_missing_file_uses_the_current_runtime_defaults()
    {
        var (store, _) = CreateStore();

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("OpenCode Zen", snapshot.Backend);
        Assert.Equal(string.Empty, snapshot.Model);
        Assert.Equal("Default", snapshot.ApiVariant);
        Assert.True(snapshot.AllowWebSearch);
        Assert.True(snapshot.EnableSelfLearning);
        Assert.True(snapshot.ShareMemoryWithCloud);
        Assert.Equal("Ru (OmniVoice)", snapshot.VoiceLabel);
        Assert.Equal("original", snapshot.VoiceStyleId);
        Assert.Equal(1.0, snapshot.VoiceSpeed);
        Assert.False(snapshot.AutoPaceEnabled);
        Assert.True(snapshot.AutoReadReplies);
        Assert.False(snapshot.ShowSubtitles);
    }

    [Fact]
    public async Task Read_ignores_retired_generation_fields_and_normalizes_other_settings()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "kilo",
              "model": "fixture-cloud-model",
              "kilo_model": "",
              "max_tokens": 9999,
              "temperature": 99,
              "top_k": 0,
              "voice_speed": 99,
              "show_subtitles": "yes",
              "voice_style_id": "low_pitch",
              "unknown_future_key": "preserve-me"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("Kilo Gateway", snapshot.Backend);
        Assert.Equal("fixture-cloud-model", snapshot.KiloModel);
        Assert.Null(snapshot.Raw["max_tokens"]);
        Assert.Null(snapshot.Raw["max_new_tokens"]);
        Assert.Null(snapshot.Raw["temperature"]);
        Assert.Null(snapshot.Raw["top_k"]);
        Assert.Equal(2.0, snapshot.VoiceSpeed);
        Assert.False(snapshot.ShowSubtitles);
        Assert.Equal("original", snapshot.VoiceStyleId);
        Assert.Equal("preserve-me", snapshot.Raw["unknown_future_key"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_malformed_json_falls_back_to_defaults()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, "{not-json");

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("OpenCode Zen", snapshot.Backend);
        Assert.Empty(snapshot.Raw);
    }

    [Fact]
    public async Task Save_preserves_unknown_properties_and_removes_credential_fields()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "llama.cpp",
              "unknown_future_key": "preserve-me",
              "max_tokens": 256,
              "max_new_tokens": 512,
              "temperature": 0.7,
              "top_k": 50,
              "api_key": "must-not-survive",
              "opencode_api_key": "must-not-survive",
              "kilo_api_key": "must-not-survive",
              "gemini_vision_api_key": "must-not-survive"
            }
            """);
        var current = await store.ReadAsync(CancellationToken.None);

        Assert.Null(current.Raw["api_key"]);
        Assert.Null(current.Raw["opencode_api_key"]);
        Assert.Null(current.Raw["kilo_api_key"]);
        Assert.Null(current.Raw["gemini_vision_api_key"]);

        await store.SaveAsync(current, CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal("preserve-me", persisted["unknown_future_key"]!.GetValue<string>());
        Assert.Null(persisted["max_tokens"]);
        Assert.Null(persisted["max_new_tokens"]);
        Assert.Null(persisted["temperature"]);
        Assert.Null(persisted["top_k"]);
        Assert.Null(persisted["api_key"]);
        Assert.Null(persisted["opencode_api_key"]);
        Assert.Null(persisted["kilo_api_key"]);
        Assert.Null(persisted["gemini_vision_api_key"]);
    }

    [Fact]
    public async Task Concurrent_saves_leave_one_complete_document_and_no_temp_file()
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);
        var saves = Enumerable.Range(1, 32)
            .Select(_ => store.SaveAsync(current, CancellationToken.None));

        await Task.WhenAll(saves);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Null(persisted["max_tokens"]);
        Assert.Null(persisted["max_new_tokens"]);
        Assert.Null(persisted["temperature"]);
        Assert.Null(persisted["top_k"]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths.SettingsFile)!, "*.tmp"));
    }

    [Fact]
    public async Task Read_ollama_settings_populates_the_new_snapshot_fields()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "ollama",
              "ollama_model": "qwen3:8b",
              "ollama_url": "http://127.0.0.1:11434/v1",
              "lmstudio_model": "local-model",
              "custom_url": "http://10.0.0.5:8000/v1"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("Ollama", snapshot.Backend);
        Assert.Equal("qwen3:8b", snapshot.OllamaModel);
        Assert.Equal("http://127.0.0.1:11434/v1", snapshot.OllamaUrl);
        Assert.Equal("local-model", snapshot.LmStudioModel);
        Assert.Equal("http://10.0.0.5:8000/v1", snapshot.CustomUrl);
    }

    [Fact]
    public async Task Save_persists_the_new_backend_fields_and_strips_custom_api_key()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "custom",
              "custom_api_key": "must-not-survive"
            }
            """);
        var current = await store.ReadAsync(CancellationToken.None);

        Assert.Null(current.Raw["custom_api_key"]);

        await store.SaveAsync(
            current with
            {
                Backend = "Custom OpenAI",
                CustomModel = "my-model",
                CustomUrl = "http://localhost:9000/v1",
                OllamaUrl = "http://localhost:11434/v1",
            },
            CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal("Custom OpenAI", persisted["backend"]!.GetValue<string>());
        Assert.Equal("my-model", persisted["custom_model"]!.GetValue<string>());
        Assert.Equal("http://localhost:9000/v1", persisted["custom_url"]!.GetValue<string>());
        Assert.Null(persisted["custom_api_key"]);
    }

    [Fact]
    public async Task Read_retired_gguf_backend_migrates_to_opencode_without_legacy_model()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "llama.cpp",
              "model": "LFM2.5-2.6B-Q4_K_M.gguf"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("OpenCode Zen", snapshot.Backend);
        Assert.Equal(string.Empty, snapshot.OpenCodeModel);
        Assert.Equal("LFM2.5-2.6B-Q4_K_M.gguf", snapshot.Model);
    }

    [Fact]
    public async Task Read_uppercase_retired_gguf_backend_still_migrates_without_legacy_model()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "LLAMA.CPP",
              "model": "LFM2.5-2.6B-Q4_K_M.gguf"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("OpenCode Zen", snapshot.Backend);
        Assert.Equal(string.Empty, snapshot.OpenCodeModel);
    }

    [Fact]
    public async Task Save_never_persists_a_retired_gguf_legacy_model()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "llama.cpp",
              "model": "LFM2.5-2.6B-Q4_K_M.gguf"
            }
            """);
        var current = await store.ReadAsync(CancellationToken.None);

        await store.SaveAsync(
            current with { Backend = "OpenCode Zen" },
            CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal("OpenCode Zen", persisted["backend"]!.GetValue<string>());
        Assert.Null(persisted["model"]);
    }

    [Fact]
    public async Task Save_rejects_overlong_model_without_replacing_the_existing_file()
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);
        await store.SaveAsync(current, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(paths.SettingsFile);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
            current with { Model = new string('m', 257) },
            CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(paths.SettingsFile));
    }

    private static (SettingsStore Store, DataPaths Paths) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-settings-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(
            Path.Combine(root, "memory.db"),
            Path.Combine(root, "memory_texts.json"),
            Path.Combine(root, "settings.json"),
            Path.Combine(root, "history.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new SettingsStore(paths), paths);
    }
}
