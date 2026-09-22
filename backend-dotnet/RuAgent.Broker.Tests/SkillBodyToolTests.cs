using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The skill body tool, driven through the service the way a tool request is:
/// manifest binding, schema validation and policy all apply, and the body is
/// prompt guidance only — it never grants a tool, scope, path, or approval.
/// </summary>
public sealed class SkillBodyToolTests
{
    private const string Session = "skill-body-session";

    [Fact]
    public async Task Known_skill_returns_name_description_body_and_digest()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"pdf.analyzer"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Equal("pdf.analyzer", result.Output["name"]!.GetValue<string>());
        Assert.Contains("PDF", result.Output["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read the PDF", result.Output["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(64, result.Output["body_sha256"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Unknown_skill_fails_closed_with_skill_not_found()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"no.such.skill"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("skill_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Skill_body_never_grants_tools_scopes_or_paths()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"pdf.analyzer"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.DoesNotContain("skill.execute", result.Output.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("workspace.write", result.Output.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_name_fails_schema_validation()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();

        var result = await service.ExecuteAsync(
            Envelope("""{}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
    }

    private static AgentEnvelope Envelope(string arguments, string sessionId = Session) =>
        new(
            1,
            $"req_{Guid.NewGuid():N}",
            AgentMessageTypes.ToolRequest,
            sessionId,
            null,
            30_000,
            new JsonObject
            {
                ["tool_id"] = "skill",
                ["tool_version"] = "1.0.0",
                ["arguments"] = JsonNode.Parse(arguments),
            });

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, BrokerSession session)
        {
            Root = root;
            Session = session;
        }

        public string Root { get; }

        public BrokerSession Session { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-skill-body-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var skills = Directory.CreateDirectory(Path.Combine(root, "skills", "pdf.analyzer")).FullName;
            File.WriteAllText(
                Path.Combine(skills, "SKILL.md"),
                "---\nname: pdf.analyzer\ndescription: Analyze PDF files\nallowed-tools: read, bash\n---\n\n# PDF Analyzer\n\nRead the PDF with the read tool.",
                new System.Text.UTF8Encoding(false));
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:18088",
                "token",
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            return new Fixture(root, BrokerSession.Load(descriptorPath));
        }

        public ToolExecutionService CreateService()
        {
            Session.Open(
                Session_,
                new HashSet<string>(
                    [RuAgent.Broker.Policy.ToolScope.SystemRead],
                    StringComparer.Ordinal));
            return new ToolExecutionService(Session, Root);
        }

        private const string Session_ = "skill-body-session";

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
