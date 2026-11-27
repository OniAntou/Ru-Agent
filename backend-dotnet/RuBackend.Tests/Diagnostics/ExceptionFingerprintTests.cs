using System.Runtime.CompilerServices;
using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

/// <summary>
/// The fingerprint's site is the only pointer a failed run carries to where it failed,
/// so it must name Ru's own code, not the framework helper that raised the exception.
/// </summary>
public sealed class ExceptionFingerprintTests
{
    [Fact]
    public void A_fault_raised_by_a_framework_helper_names_the_ru_caller()
    {
        var error = Capture(() => CallsAThrowHelper(null));

        Assert.Equal(
            $"{typeof(ExceptionFingerprintTests).FullName}.{nameof(CallsAThrowHelper)}",
            ExceptionFingerprint.SiteOf(error));
    }

    [Fact]
    public async Task An_async_method_is_named_as_written()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(FailsAsync);

        Assert.Equal(
            $"{typeof(ExceptionFingerprintTests).FullName}.{nameof(FailsAsync)}",
            ExceptionFingerprint.SiteOf(error));
    }

    [Fact]
    public void A_wrapper_holding_one_fault_is_classified_by_that_fault()
    {
        var inner = Capture(() => CallsAThrowHelper(null));
        var wrapped = new AggregateException(inner);

        Assert.Equal("invalid_argument", ExceptionFingerprint.KindOf(wrapped));
        Assert.Equal(ExceptionFingerprint.SiteOf(inner), ExceptionFingerprint.SiteOf(wrapped));
        Assert.Equal("unknown", ExceptionFingerprint.KindOf(new AggregateException(inner, new InvalidOperationException())));
    }

    [Fact]
    public void A_named_run_state_failure_has_its_own_kind()
    {
        var failure = new RuAgent.Harness.Core.AgentRunStateException("run_version_conflict");

        Assert.Equal("state", ExceptionFingerprint.KindOf(failure));
        Assert.Equal("run_version_conflict", failure.Code);
    }

    [Fact]
    public void The_fingerprint_built_from_a_site_passes_its_own_validation()
    {
        var fingerprint = ExceptionFingerprint.For(Capture(() => CallsAThrowHelper(null)));

        Assert.NotNull(fingerprint);
        Assert.Equal("invalid_argument", fingerprint!.Kind);
        Assert.EndsWith("." + nameof(CallsAThrowHelper), fingerprint.Site, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CallsAThrowHelper(string? value) => ArgumentNullException.ThrowIfNull(value);

    private static async Task FailsAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("failed");
    }

    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            return error;
        }

        throw new InvalidOperationException("expected a failure");
    }
}
