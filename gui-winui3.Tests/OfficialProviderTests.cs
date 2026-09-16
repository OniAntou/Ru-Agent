using System.Net;
using System.Text;
using System.Text.Json;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class OfficialProviderTests
{
    [Fact]
    public void Official_provider_update_serializes_separate_keys()
    {
        var dto = new ProviderSettingsUpdateDto
        {
            BackendChoice = "anthropic",
            OpenAiApiKey = "openai-only",
            AnthropicApiKey = "anthropic-only",
        };

        string json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"openai_api_key\":\"openai-only\"", json, StringComparison.Ordinal);
        Assert.Contains("\"anthropic_api_key\":\"anthropic-only\"", json, StringComparison.Ordinal);
        // Ensure keys are not swapped / leaked to the other field
        Assert.DoesNotContain("\"openai_api_key\":\"anthropic-only\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"anthropic_api_key\":\"openai-only\"", json, StringComparison.Ordinal);
        // Backend choice should be present as well
        Assert.Contains("\"backend_choice\":\"anthropic\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAi_settings_wire_names()
    {
        var dto = new AppSettingsDto
        {
            OpenAiApiKey = "sk-openai",
            HasOpenAiKey = true,
            OpenAiModel = "gpt-4o",
            AnthropicApiKey = "sk-ant",
            HasAnthropicKey = true,
            AnthropicModel = "claude-3-5-sonnet",
        };

        string payload = JsonSerializer.Serialize(dto);
        using JsonDocument document = JsonDocument.Parse(payload);

        // OpenAI wire names
        Assert.Equal("sk-openai", document.RootElement.GetProperty("openai_api_key").GetString());
        Assert.True(document.RootElement.GetProperty("has_openai_api_key").GetBoolean());
        Assert.Equal("gpt-4o", document.RootElement.GetProperty("openai_model").GetString());

        // Anthropic wire names
        Assert.Equal("sk-ant", document.RootElement.GetProperty("anthropic_api_key").GetString());
        Assert.True(document.RootElement.GetProperty("has_anthropic_api_key").GetBoolean());
        Assert.Equal("claude-3-5-sonnet", document.RootElement.GetProperty("anthropic_model").GetString());

        // Also verify raw payload contains expected wire names (no PascalCase leak)
        Assert.Contains("\"openai_api_key\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"has_openai_api_key\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"openai_model\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"anthropic_api_key\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"has_anthropic_api_key\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"anthropic_model\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_provider_model_selection_serializes_only_the_two_field_body()
    {
        using var handler = new SaveHandler(new[] { "{\"status\":\"ok\"}", "{\"status\":\"ok\"}" });
        using var api = new ApiService(
            new BackendSession(new Uri("http://127.0.0.1:59999/"), "secret"),
            handler);

        // OpenAI path
        Assert.True(await api.SaveProviderModelSelectionAsync("openai", "gpt-5.6-sol"));
        using (var document = JsonDocument.Parse(handler.LastBody))
        {
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        }

        Assert.Contains("\"backend_choice\":\"openai\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"selected_model\":\"gpt-5.6-sol\"", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("anthropic_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("kilo_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("opencode_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("lmstudio_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("custom_model", handler.LastBody, StringComparison.Ordinal);

        // Anthropic path (similarly)
        Assert.True(await api.SaveProviderModelSelectionAsync("anthropic", "claude-4-sonnet"));
        using (var anthropicDocument = JsonDocument.Parse(handler.LastBody))
        {
            Assert.Equal(2, anthropicDocument.RootElement.EnumerateObject().Count());
        }

        Assert.Contains("\"backend_choice\":\"anthropic\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"selected_model\":\"claude-4-sonnet\"", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("openai_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("kilo_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("opencode_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("lmstudio_model", handler.LastBody, StringComparison.Ordinal);
        Assert.DoesNotContain("custom_model", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ComboBox_contains_official_provider_items()
    {
        string xaml = File.ReadAllText(FindViewSource("SettingsView.xaml"));

        Assert.Contains("Text=\"OpenAI (Official API)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"openai\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Anthropic Claude (Official API)\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Tag=\"anthropic\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenAiApiRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AnthropicApiRow\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Retired_controls_remain_absent()
    {
        string xaml = File.ReadAllText(FindViewSource("SettingsView.xaml"));

        // Old generation controls used a Slider named Temperature - must remain absent
        Assert.DoesNotContain("x:Name=\"Temperature", xaml, StringComparison.Ordinal);
        // General safeguard: no "Temperature" control name should linger
        // We do a case-sensitive check; if a comment or unrelated text contains Temperature it's still a failure per spec
        // So we check for the name specifically but also ensure no Slider with Temperature appears
        foreach (string line in xaml.Split('\n'))
        {
            if (line.Contains("Slider", StringComparison.Ordinal) && line.Contains("Temperature", StringComparison.Ordinal))
            {
                Assert.Fail($"Retired Temperature Slider found: {line.Trim()}");
            }
        }
    }

    private static string FindViewSource(string sourceName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "gui-winui3", "Views", sourceName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            // Fallback: repo root may expose Views directly under gui-winui3
            var alt = Path.Combine(directory.FullName, "gui-winui3", sourceName);
            if (File.Exists(alt))
            {
                return alt;
            }
        }

        throw new FileNotFoundException($"Could not locate Views/{sourceName}.");
    }

    private sealed class SaveHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public SaveHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
