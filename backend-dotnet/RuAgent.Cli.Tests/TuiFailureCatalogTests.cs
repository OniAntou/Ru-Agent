using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiFailureCatalogTests : IDisposable
{
    private readonly IDisposable _levelPin;

    public TuiFailureCatalogTests()
    {
        _levelPin = TuiPalette.PinLevel(TuiColorLevel.None);
    }

    public void Dispose() => _levelPin.Dispose();

    [Theory]
    [InlineData("provider_rate_limited")]
    [InlineData("catalog_unauthorized")]
    [InlineData("provider_not_active")]
    [InlineData("worker_unavailable")]
    [InlineData("worker_failed")]
    [InlineData("provider_failed")]
    [InlineData("generation_failed")]
    [InlineData("drive_failed")]
    [InlineData("runtime_drive_failed")]
    [InlineData("empty_model_reply")]
    [InlineData("repeated_tool_call")]
    [InlineData("invalid_selection")]
    [InlineData("prompt_contract_invalid")]
    [InlineData("invalid_request")]
    [InlineData("run_not_runnable")]
    [InlineData("run_failed")]
    [InlineData("workspace_busy")]
    [InlineData("session_busy")]
    [InlineData("reconciliation_required")]
    [InlineData("reconcile_stale")]
    [InlineData("approval_denied")]
    [InlineData("approval_expired")]
    [InlineData("approval_stale")]
    [InlineData("tool_failed")]
    [InlineData("provider_bootstrap_failed")]
    [InlineData("harness_store_unavailable")]
    [InlineData("agent_store_unavailable")]
    [InlineData("policy_snapshot_unavailable")]
    [InlineData("workspace_membership_expired")]
    [InlineData("session_create_failed")]
    [InlineData("session_not_found")]
    [InlineData("invalid_session_id")]
    [InlineData("invalid_workspace")]
    [InlineData("preset_mismatch")]
    [InlineData("preset_version_conflict")]
    [InlineData("compact_failed")]
    [InlineData("command_invalid")]
    [InlineData("command_unknown")]
    [InlineData("provider_auth")]
    [InlineData("provider_server_error")]
    [InlineData("provider_transport_error")]
    [InlineData("provider_bootstrap_invalid")]
    [InlineData("capability_unavailable")]
    [InlineData("model_tool_call_invalid")]
    [InlineData("model_tool_call_unavailable")]
    [InlineData("protocol_failure")]
    [InlineData("timeout")]
    [InlineData("transport_error")]
    [InlineData("server_error")]
    [InlineData("http_error")]
    [InlineData("response_too_large")]
    [InlineData("vision_unsupported")]
    [InlineData("image_limit_exceeded")]
    [InlineData("image_too_large")]
    [InlineData("invalid_image_mime")]
    [InlineData("invalid_image_base64")]
    [InlineData("invalid_prompt")]
    [InlineData("forbidden")]
    [InlineData("model_failed")]
    [InlineData("model_timeout")]
    [InlineData("prompt_too_large")]
    [InlineData("model_unavailable")]
    public void EveryCatalogedCode_HasTitleCauseAndFix(string code)
    {
        var info = TuiFailureCatalog.For(code);

        Assert.Equal(code, info.Code);
        Assert.False(string.IsNullOrWhiteSpace(info.Title));
        Assert.False(string.IsNullOrWhiteSpace(info.Cause));
        Assert.False(string.IsNullOrWhiteSpace(info.Fix));
        Assert.DoesNotContain('\n', info.Title);
        Assert.DoesNotContain('\n', info.Cause);
        Assert.DoesNotContain('\n', info.Fix);
    }

    [Fact]
    public void UnknownCode_NamesItselfAndPointsAtLogs()
    {
        var info = TuiFailureCatalog.For("some_future_code");

        Assert.Equal("some_future_code", info.Code);
        Assert.Contains("some_future_code", info.Cause, StringComparison.Ordinal);
        Assert.Contains("/status", info.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankCode_FallsBackToRunFailed()
    {
        Assert.Equal("run_failed", TuiFailureCatalog.For(null).Code);
        Assert.Equal("run_failed", TuiFailureCatalog.For("  ").Code);
    }

    [Fact]
    public void WorkerCodes_KeepWorkerAndProviderCausesApart()
    {
        Assert.Contains("backend", TuiFailureCatalog.For("worker_unavailable").Cause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backend", TuiFailureCatalog.For("worker_failed").Cause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("worker", TuiFailureCatalog.For("provider_failed").Cause, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("process_timed_out", "timed out")]
    [InlineData("policy_denied", "policy")]
    [InlineData("context_limit_reached", "context")]
    [InlineData("workspace_changed", "changed")]
    public void NewlyCatalogedCodes_GetTheirOwnCopyInsteadOfTheFallback(string code, string marker)
    {
        var info = TuiFailureCatalog.For(code);

        Assert.Equal(code, info.Code);
        Assert.NotEqual("Error", info.Title);
        Assert.Contains(marker, $"{info.Title} {info.Cause} {info.Fix}", StringComparison.OrdinalIgnoreCase);
    }
}
