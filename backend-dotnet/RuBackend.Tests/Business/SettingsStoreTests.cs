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
              "allow_web_search": false,
              "allow_online_search": false,
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
        Assert.Null(snapshot.Raw["allow_web_search"]);
        Assert.Null(snapshot.Raw["allow_online_search"]);
        Assert.Null(snapshot.Raw["voice_speed"]);
        Assert.Null(snapshot.Raw["show_subtitles"]);
        Assert.Null(snapshot.Raw["voice_style_id"]);
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
    public async Task Read_unknown_backend_stays_unselected_instead_of_falling_back_to_opencode()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "future-provider",
              "opencode_model": "must-not-be-selected"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(string.Empty, snapshot.Backend);
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
              "allow_web_search": false,
              "allow_online_search": false,
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
        Assert.Null(persisted["allow_web_search"]);
        Assert.Null(persisted["allow_online_search"]);
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
    public async Task Read_retired_local_provider_fields_are_scrubbed_from_the_snapshot()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "custom",
              "ollama_model": "qwen3:8b",
              "ollama_url": "http://127.0.0.1:11434/v1",
              "lmstudio_model": "local-model",
              "lmstudio_url": "http://127.0.0.1:1234/v1",
              "custom_url": "http://10.0.0.5:8000/v1"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("Custom OpenAI", snapshot.Backend);
        Assert.Equal("http://10.0.0.5:8000/v1", snapshot.CustomUrl);
        Assert.Null(snapshot.Raw["ollama_model"]);
        Assert.Null(snapshot.Raw["ollama_url"]);
        Assert.Null(snapshot.Raw["lmstudio_model"]);
        Assert.Null(snapshot.Raw["lmstudio_url"]);

        await store.SaveAsync(snapshot, CancellationToken.None);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Null(persisted["ollama_model"]);
        Assert.Null(persisted["ollama_url"]);
        Assert.Null(persisted["lmstudio_model"]);
        Assert.Null(persisted["lmstudio_url"]);
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

    [Fact]
    public async Task Save_rejects_an_oversized_unknown_settings_document()
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);
        var raw = current.Raw.DeepClone().AsObject();
        raw["unknown_future_payload"] = new string('x', 1 * 1024 * 1024);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            current with { Raw = raw },
            CancellationToken.None));

        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public async Task Read_missing_file_uses_the_compaction_defaults()
    {
        var (store, _) = CreateStore();

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(0.80, snapshot.CompactionTriggerRatio);
        Assert.Equal(0.62, snapshot.CompactionTargetRatio);
    }

    [Fact]
    public async Task Read_keeps_models_dev_backend()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """{"backend":"Models.Dev:OpenRouter"}""");

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal("models.dev:openrouter", snapshot.Backend);
    }

    [Fact]
    public async Task Save_round_trips_provider_id()
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(string.Empty, current.ProviderId);

        await store.SaveAsync(current with { ProviderId = "openrouter" }, CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal("openrouter", persisted["provider_id"]!.GetValue<string>());

        var reloaded = await store.ReadAsync(CancellationToken.None);
        Assert.Equal("openrouter", reloaded.ProviderId);
    }

    [Fact]
    public async Task Save_round_trips_compaction_ratios()
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);

        await store.SaveAsync(
            current with
            {
                CompactionTriggerRatio = 0.75,
                CompactionTargetRatio = 0.50,
            },
            CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal(0.75, persisted["compaction_trigger_ratio"]!.GetValue<double>());
        Assert.Equal(0.50, persisted["compaction_target_ratio"]!.GetValue<double>());

        var reloaded = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(0.75, reloaded.CompactionTriggerRatio);
        Assert.Equal(0.50, reloaded.CompactionTargetRatio);
    }

    [Theory]
    [InlineData(0.0, 0.62)]
    [InlineData(1.5, 0.62)]
    [InlineData(1.0, 0.62)]
    [InlineData(0.62, 0.62)]
    [InlineData(0.50, 0.62)]
    [InlineData(double.NaN, 0.62)]
    [InlineData(double.PositiveInfinity, 0.62)]
    [InlineData(0.80, 0.0)]
    [InlineData(0.80, 1.5)]
    [InlineData(0.80, 1.0)]
    [InlineData(0.80, 0.80)]
    [InlineData(0.80, double.NaN)]
    [InlineData(0.80, double.PositiveInfinity)]
    public async Task Read_invalid_compaction_ratios_fall_back_to_defaults(
        double triggerRatio,
        double targetRatio)
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        var triggerNode = double.IsNaN(triggerRatio) || double.IsInfinity(triggerRatio)
            ? "null"
            : triggerRatio.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var targetNode = double.IsNaN(targetRatio) || double.IsInfinity(targetRatio)
            ? "null"
            : targetRatio.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(paths.SettingsFile, $$"""
            {
              "compaction_trigger_ratio": {{triggerNode}},
              "compaction_target_ratio": {{targetNode}}
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(0.80, snapshot.CompactionTriggerRatio);
        Assert.Equal(0.62, snapshot.CompactionTargetRatio);
    }

    [Fact]
    public async Task Read_missing_compaction_ratios_fall_back_to_defaults()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "backend": "kilo"
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(0.80, snapshot.CompactionTriggerRatio);
        Assert.Equal(0.62, snapshot.CompactionTargetRatio);
    }

    [Fact]
    public async Task Read_trigger_at_exactly_one_falls_back_to_defaults()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "compaction_trigger_ratio": 1.0,
              "compaction_target_ratio": 0.50
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(0.80, snapshot.CompactionTriggerRatio);
        Assert.Equal(0.62, snapshot.CompactionTargetRatio);
    }

    [Fact]
    public async Task Read_trigger_just_below_one_is_accepted()
    {
        var (store, paths) = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFile)!);
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "compaction_trigger_ratio": 0.99,
              "compaction_target_ratio": 0.50
            }
            """);

        var snapshot = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(0.99, snapshot.CompactionTriggerRatio);
        Assert.Equal(0.50, snapshot.CompactionTargetRatio);
    }

    [Theory]
    [InlineData(0.0, 0.50)]
    [InlineData(1.0, 0.50)]
    [InlineData(1.5, 0.50)]
    [InlineData(0.50, 0.62)]
    [InlineData(0.80, 0.0)]
    [InlineData(0.80, 1.5)]
    public async Task Save_invalid_compaction_ratios_persist_defaults(
        double triggerRatio,
        double targetRatio)
    {
        var (store, paths) = CreateStore();
        var current = await store.ReadAsync(CancellationToken.None);

        await store.SaveAsync(
            current with
            {
                CompactionTriggerRatio = triggerRatio,
                CompactionTargetRatio = targetRatio,
            },
            CancellationToken.None);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(paths.SettingsFile))!.AsObject();
        Assert.Equal(0.80, persisted["compaction_trigger_ratio"]!.GetValue<double>());
        Assert.Equal(0.62, persisted["compaction_target_ratio"]!.GetValue<double>());

        var reloaded = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(0.80, reloaded.CompactionTriggerRatio);
        Assert.Equal(0.62, reloaded.CompactionTargetRatio);
    }

    private static (SettingsStore Store, DataPaths Paths) CreateStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-settings-tests", Guid.NewGuid().ToString("N"));
        var paths = new DataPaths(Path.Combine(root, "settings.json"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "activity.jsonl"));
        return (new SettingsStore(paths), paths);
    }
}
