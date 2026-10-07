using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// What the connection test tells the user.
/// </summary>
/// <remarks>
/// This is the entire feedback the button produces, so every branch is asserted.
/// A branch that fell through to a blank line would look like the test did nothing,
/// and a wrong "works" would be worse than no test at all.
/// </remarks>
public sealed class ProviderConnectionTestPresentationTests
{
    private static ProviderCatalogState Success(int models) => new()
    {
        Status = "ok",
        Backend = "openai",
        Models = Enumerable.Range(1, models).Select(index => $"model-{index}").ToArray(),
    };

    private static ProviderCatalogState Failure(string? errorCode) => new()
    {
        Status = "error",
        Backend = "openai",
        ErrorCode = errorCode,
    };

    [Fact]
    public void A_working_endpoint_reports_how_many_models_it_offers()
    {
        var test = ProviderConnectionTestPresentation.Project(Success(42));

        Assert.Equal(ProviderConnectionTest.SuccessTone, test.Tone);
        Assert.Contains("42", test.Message, StringComparison.Ordinal);
        Assert.Contains("works", test.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_single_model_is_not_reported_as_plural()
    {
        var test = ProviderConnectionTestPresentation.Project(Success(1));

        Assert.Contains("1 model ", test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1 models", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_that_answers_with_no_models_is_a_warning_not_a_failure()
    {
        // A local server with nothing loaded answers exactly like this. Calling it a
        // failure would be wrong, and calling it a success would hide the reason the
        // next message fails.
        var test = ProviderConnectionTestPresentation.Project(Success(0));

        Assert.NotEqual(ProviderConnectionTest.ErrorTone, test.Tone);
        Assert.Contains("no models", test.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("catalog_unauthorized", "API key")]
    [InlineData("catalog_rate_limited", "rate-limit")]
    [InlineData("catalog_http_error", "returned an error")]
    [InlineData("catalog_timeout", "did not answer")]
    [InlineData("catalog_too_large", "too large")]
    [InlineData("catalog_malformed", "could not read")]
    [InlineData("provider_not_active", "provider is selected")]
    [InlineData("catalog_configuration_error", "API key")]
    [InlineData("catalog_unavailable", "network")]
    public void Every_catalog_error_the_host_produces_has_its_own_instruction(string code, string expected)
    {
        var test = ProviderConnectionTestPresentation.Project(Failure(code));

        Assert.Equal(ProviderConnectionTest.ErrorTone, test.Tone);
        Assert.Contains(expected, test.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_host_error_code_falls_through_to_the_raw_code()
    {
        // Every code ProviderCatalogService emits. A live run produced
        // catalog_unauthorized before this table knew it, and the user saw the raw
        // code instead of an instruction.
        string[] hostCodes =
        [
            "catalog_unauthorized",
            "catalog_rate_limited",
            "catalog_http_error",
            "catalog_timeout",
            "catalog_too_large",
            "catalog_malformed",
            "catalog_unavailable",
            "provider_not_active",
            "catalog_configuration_error",
        ];

        foreach (string code in hostCodes)
        {
            string message = ProviderConnectionTestPresentation.Project(Failure(code)).Message;

            Assert.DoesNotContain(code, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_wrong_key_and_an_unreachable_provider_give_different_advice()
    {
        // These have different fixes. Sending someone to check their wifi when the key
        // is wrong wastes the one thing a test is supposed to save.
        var keyProblem = ProviderConnectionTestPresentation.Project(Failure("catalog_unauthorized"));
        var networkProblem = ProviderConnectionTestPresentation.Project(Failure("catalog_unavailable"));

        Assert.NotEqual(keyProblem.Message, networkProblem.Message);
        Assert.Contains("key", keyProblem.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network", networkProblem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unmapped_error_code_still_names_itself()
    {
        var test = ProviderConnectionTestPresentation.Project(Failure("some_new_code"));

        Assert.Equal(ProviderConnectionTest.ErrorTone, test.Tone);
        Assert.Contains("some_new_code", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_error_code_is_treated_as_unreachable()
    {
        var test = ProviderConnectionTestPresentation.Project(Failure(null));

        Assert.Equal(ProviderConnectionTest.ErrorTone, test.Tone);
        Assert.Contains("network", test.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_null_state_reports_that_the_backend_was_unreachable()
    {
        var test = ProviderConnectionTestPresentation.Project(null);

        Assert.Equal(ProviderConnectionTest.ErrorTone, test.Tone);
        Assert.Contains("backend", test.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_a_failure_counts_as_a_reportable_test_result()
    {
        // The catalog refreshes on a timer. A background failure must not overwrite a
        // success the user just asked for, and a success must not appear on its own.
        Assert.False(ProviderConnectionTestPresentation.ReportsFailure(
            ProviderConnectionTestPresentation.Project(Success(3))));
        Assert.False(ProviderConnectionTestPresentation.ReportsFailure(null));
        Assert.False(ProviderConnectionTestPresentation.ReportsFailure(
            ProviderConnectionTestPresentation.Testing()));
        Assert.True(ProviderConnectionTestPresentation.ReportsFailure(
            ProviderConnectionTestPresentation.Project(Failure("catalog_unavailable"))));
    }

    [Fact]
    public void Testing_state_is_visible_so_the_button_does_not_look_ignored()
    {
        var testing = ProviderConnectionTestPresentation.Testing();

        Assert.Equal(ProviderConnectionTest.NeutralTone, testing.Tone);
        Assert.False(string.IsNullOrWhiteSpace(testing.Message));
        Assert.Equal("Test connection", ProviderConnectionTestPresentation.ButtonLabel);
    }

    [Fact]
    public void Every_message_is_short_enough_for_a_settings_row()
    {
        // The status sits under a 260-wide control; a paragraph there is a layout bug.
        var messages = new[]
        {
            ProviderConnectionTestPresentation.Testing().Message,
            ProviderConnectionTestPresentation.Project(null).Message,
            ProviderConnectionTestPresentation.Project(Success(0)).Message,
            ProviderConnectionTestPresentation.Project(Success(500)).Message,
            ProviderConnectionTestPresentation.Project(Failure("provider_not_active")).Message,
            ProviderConnectionTestPresentation.Project(Failure("catalog_configuration_error")).Message,
            ProviderConnectionTestPresentation.Project(Failure("catalog_unavailable")).Message,
        };

        foreach (string message in messages)
        {
            Assert.InRange(message.Length, 1, 120);
        }
    }
}
