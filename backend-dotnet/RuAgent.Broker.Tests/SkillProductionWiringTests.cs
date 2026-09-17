using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillProductionWiringTests
{
    [Fact]
    public async Task Tool_service_lists_and_executes_a_granted_builtin_skill()
    {
        using var fixture = SessionFixture.Create();
        var skill = new SkillManifest(
            "workspace.list.skill", "1.0.0",
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            new HashSet<string>(["list"]),
            new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute]),
            [new SkillStep(SkillStepKind.CallTool, "list", "list", new JsonObject { ["path"] = ".", ["max_entries"] = 8 })],
            false, 4, 30_000, "builtin", "Data only.");
        fixture.Session.Open(
            "skill-session",
            new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute]),
            new HashSet<string>([skill.Id]));
        var service = new ToolExecutionService(fixture.Session, fixture.Root, builtinSkills: [skill]);

        Assert.Single(service.ListSkills("skill-session"));
        var run = await service.ExecuteSkillAsync(
            "skill-session", skill.Id, skill.Version, new JsonObject(), CancellationToken.None);

        Assert.Equal(SkillRunState.Completed, run.State);
        Assert.Single(run.StepReceipts);
    }

    [Fact]
    public async Task Tool_service_denies_an_ungranted_skill_before_the_adapter()
    {
        using var fixture = SessionFixture.Create();
        var skill = new SkillManifest(
            "workspace.list.skill", "1.0.0",
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            new HashSet<string>(["list"]),
            new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SystemRead, ToolScope.SkillExecute]),
            [new SkillStep(SkillStepKind.CallTool, "list", "list", new JsonObject { ["path"] = ".", ["max_entries"] = 8 })],
            false, 4, 30_000, "builtin", "Data only.");
        fixture.Session.Open(
            "skill-session",
            new HashSet<string>([ToolScope.WorkspaceRead, ToolScope.SkillExecute]),
            new HashSet<string>());
        var service = new ToolExecutionService(fixture.Session, fixture.Root, builtinSkills: [skill]);

        var run = await service.ExecuteSkillAsync(
            "skill-session", skill.Id, skill.Version, new JsonObject(), CancellationToken.None);

        Assert.Equal(SkillRunState.Failed, run.State);
        Assert.Equal("skill_not_granted", run.ErrorCode);
    }

    private sealed class SessionFixture : IDisposable
    {
        private SessionFixture(string root, BrokerSession session)
        {
            Root = root;
            Session = session;
        }

        public string Root { get; }
        public BrokerSession Session { get; }

        public static SessionFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-skill-service-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:18086",
                "token",
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(5));
            var descriptorPath = Path.Combine(root, "broker.json");
            File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            return new SessionFixture(root, BrokerSession.Load(descriptorPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
