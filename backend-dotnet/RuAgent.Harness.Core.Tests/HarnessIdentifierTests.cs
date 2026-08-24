using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class HarnessIdentifierTests
{
    [Fact]
    public void Generator_creates_strongly_typed_lowercase_identifiers()
    {
        IIdGenerator generator = new CryptographicIdGenerator();

        Assert.Matches("^run_[0-9a-f]{32}$", generator.NewRunId().Value);
        Assert.Matches("^step_[0-9a-f]{32}$", generator.NewModelStepId().Value);
        Assert.Matches("^call_[0-9a-f]{32}$", generator.NewToolCallId().Value);
        Assert.Matches("^exec_[0-9a-f]{32}$", generator.NewExecutionId().Value);
        Assert.Matches("^apr_[0-9a-f]{32}$", generator.NewApprovalId().Value);
        Assert.Matches("^trust_[0-9a-f]{32}$", generator.NewTrustRequestId().Value);
        Assert.Matches("^rule_[0-9a-f]{32}$", generator.NewPolicyRuleId().Value);
        Assert.Matches("^evt_[0-9a-f]{32}$", generator.NewEventId().Value);
        Assert.Matches("^payload_[0-9a-f]{32}$", generator.NewPayloadId().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("run_")]
    [InlineData("run_ABCDEF0123456789abcdef0123456789")]
    [InlineData("step_abcdef0123456789abcdef0123456789")]
    [InlineData("run_abcdef0123456789abcdef01234567890")]
    public void RunId_rejects_empty_wrong_type_or_noncanonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => new RunId(value));
    }

    [Fact]
    public void Existing_session_id_and_workspace_id_have_explicit_contracts()
    {
        var session = new SessionId("0123456789abcdef0123456789abcdef");
        var workspace = new WorkspaceId("ws_0123456789abcdef0123456789abcdef");

        Assert.Equal("0123456789abcdef0123456789abcdef", session.Value);
        Assert.Equal("ws_0123456789abcdef0123456789abcdef", workspace.Value);
        Assert.NotEqual<object>(session, workspace);
    }

    [Fact]
    public void Default_record_struct_cannot_be_observed_as_a_valid_identifier()
    {
        Assert.Throws<InvalidOperationException>(() => default(RunId).Value);
        Assert.Throws<InvalidOperationException>(() => default(SessionId).ToString());
        Assert.Throws<InvalidOperationException>(() => default(WorkspaceId).Value);
    }

    [Fact]
    public void Strong_identifier_types_expose_no_implicit_conversion_operators()
    {
        var identifierTypes = new[]
        {
            typeof(RunId),
            typeof(ModelStepId),
            typeof(ToolCallId),
            typeof(ExecutionId),
        };

        Assert.All(
            identifierTypes,
            identifierType => Assert.DoesNotContain(
                identifierType.GetMethods(),
                method => method.Name == "op_Implicit"));
    }
}
