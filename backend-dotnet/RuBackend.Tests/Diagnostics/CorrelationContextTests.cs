using RuBackend.Diagnostics;
using Xunit;

namespace RuBackend.Tests.Diagnostics;

public sealed class CorrelationContextTests
{
    [Fact]
    public void GenerateId_creates_opaque_prefixed_safe_id()
    {
        var id = CorrelationContext.GenerateId();
        Assert.NotNull(id);
        Assert.StartsWith("c_", id);
        Assert.True(id.Length is >= 8 and <= 64);
        Assert.True(CorrelationContext.IsValid(id));
    }

    [Theory]
    [InlineData("c_1234567890abcdef")]
    [InlineData("corr_abc-def_123")]
    [InlineData("req_12345678")]
    public void IsValid_accepts_valid_opaque_ids(string candidate)
    {
        Assert.True(CorrelationContext.IsValid(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")] // < 8 chars
    [InlineData("has spaces 12345")]
    [InlineData("has$special#chars!")]
    [InlineData("Bearer sk-1234567890abcdef1234567890")]
    public void IsValid_rejects_invalid_or_unsafe_ids(string? candidate)
    {
        Assert.False(CorrelationContext.IsValid(candidate));
    }

    [Fact]
    public void Sanitize_replaces_invalid_or_oversized_ids_with_new_opaque_id()
    {
        var valid = "c_abcdef1234567890";
        Assert.Equal(valid, CorrelationContext.Sanitize(valid));

        var oversized = "c_" + new string('a', 100);
        var sanitized = CorrelationContext.Sanitize(oversized);
        Assert.NotEqual(oversized, sanitized);
        Assert.True(CorrelationContext.IsValid(sanitized));

        var invalid = "invalid with spaces";
        sanitized = CorrelationContext.Sanitize(invalid);
        Assert.NotEqual(invalid, sanitized);
        Assert.True(CorrelationContext.IsValid(sanitized));
    }

    [Fact]
    public void Push_maintains_async_local_scope_and_restores_previous()
    {
        Assert.Null(CorrelationContext.Current);

        var id1 = CorrelationContext.GenerateId();
        using (CorrelationContext.Push(id1))
        {
            Assert.Equal(id1, CorrelationContext.Current);

            var id2 = CorrelationContext.GenerateId();
            using (CorrelationContext.Push(id2))
            {
                Assert.Equal(id2, CorrelationContext.Current);
            }

            Assert.Equal(id1, CorrelationContext.Current);
        }

        Assert.Null(CorrelationContext.Current);
    }
}
