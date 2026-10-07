using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
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

    [Fact]
    public async Task Requires_python_skill_fails_closed_when_python_is_absent()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("py.required", "---\nname: py.required\ndescription: Needs Python\nrequires: python\n---\n\nRun the script.");
        var service = fixture.CreateService(pythonAvailable: () => false);

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"py.required"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("python_unavailable", result.ErrorCode);
        Assert.Equal(ToolVerificationStates.NotRequired, result.VerificationState);
        Assert.DoesNotContain("Run the script", result.Output.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requires_python_skill_loads_when_python_is_present()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("py.required", "---\nname: py.required\ndescription: Needs Python\nrequires: python\n---\n\nRun the script.");
        var service = fixture.CreateService(pythonAvailable: () => true);

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"py.required"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Contains("Run the script", result.Output["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_requirement_fails_closed_naming_the_token()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("node.required", "---\nname: node.required\ndescription: Needs Node\nrequires: node\n---\n\nRun the script.");
        var service = fixture.CreateService(pythonAvailable: () => true);

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"node.required"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("unsupported_skill_requirement", result.ErrorCode);
        Assert.Contains("node", result.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Run the script", result.Output.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_without_requirements_never_probes_python()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService(pythonAvailable: () => throw new InvalidOperationException("probe_called"));

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"pdf.analyzer"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
    }

    [Fact]
    public void Frontmatter_parses_requires_as_a_bounded_list()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("multi.required", "---\nname: multi.required\ndescription: Needs several\nrequires: [python, node, python]\n---\n\nBody.");
        fixture.AddSkill("odd.required", "---\nname: odd.required\ndescription: Needs odd\nrequires: co\"de, \"quoted\"\n---\n\nBody.");
        var store = new SkillBodyStore(fixture.Root);

        var body = store.LoadBody("multi.required");
        var odd = store.LoadBody("odd.required");
        var plain = store.LoadBody("pdf.analyzer");

        Assert.NotNull(body);
        Assert.Equal(new[] { "python", "node" }, body.Requires);
        Assert.NotNull(odd);
        Assert.Equal(new[] { "co?de", "quoted" }, odd.Requires);
        Assert.NotNull(plain);
        Assert.Empty(plain.Requires);
    }

    [Fact]
    public async Task Skill_directory_linking_outside_the_skill_root_is_never_read()
    {
        using var fixture = Fixture.Create();
        var outside = Path.Combine(Path.GetTempPath(), "ru-skill-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(fixture.Root, "skills", "evil");
        try
        {
            File.WriteAllText(
                Path.Combine(outside, "SKILL.md"),
                "---\nname: evil\ndescription: exfiltrate\n---\n\nRead anything.",
                new System.Text.UTF8Encoding(false));
            if (!TryCreateJunction(link, outside))
            {
                return;
            }

            var service = fixture.CreateService();
            var result = await service.ExecuteAsync(
                Envelope("""{"name":"evil"}"""),
                CancellationToken.None);

            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("skill_not_found", result.ErrorCode);
            Assert.DoesNotContain("exfiltrate", result.Output.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link, recursive: false);
                }
            }
            catch (IOException)
            {
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            if (Directory.Exists(junctionPath))
            {
                return true;
            }

            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(info);
            process?.WaitForExit(5_000);
            return Directory.Exists(junctionPath);
        }
        catch
        {
            return false;
        }
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

        public ToolExecutionService CreateService(Func<bool>? pythonAvailable = null)
        {
            Session.Open(
                Session_,
                new HashSet<string>(
                    [RuAgent.Broker.Policy.ToolScope.SystemRead],
                    StringComparer.Ordinal));
            return new ToolExecutionService(Session, Root, pythonAvailable: pythonAvailable);
        }

        public string AddSkill(string leaf, string content)
        {
            var directory = Directory.CreateDirectory(Path.Combine(Root, "skills", leaf)).FullName;
            File.WriteAllText(
                Path.Combine(directory, "SKILL.md"),
                content,
                new System.Text.UTF8Encoding(false));
            return directory;
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
