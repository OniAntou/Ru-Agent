using System.Reflection;
using System.Text;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuBackend.Providers;
using Xunit;

namespace RuBackend.Tests.Providers;

public sealed class SqliteProviderContinuationReaderTests
{
    [Fact]
    public void Parse_maps_malformed_persisted_output_member_to_named_continuation_error()
    {
        var bootstrap = new ProviderBootstrapData(
            "OpenAI", "openai_responses", new Uri("https://provider.invalid/v1/responses"),
            "gpt-5.6-luna", "synthetic", toolCallingMode: ProviderToolCallingMode.Native, allowNativeResponses: true);
        var parse = typeof(SqliteProviderContinuationReader).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;

        var thrown = Assert.Throws<TargetInvocationException>(() => parse.Invoke(null,
            [new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"output_items\":[null]}")), bootstrap, "openai"]));

        var failure = Assert.IsType<ProviderCatalogException>(thrown.InnerException);
        Assert.Equal("provider_continuation_invalid", failure.Code);
    }
}
