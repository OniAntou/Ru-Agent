using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillPackageLoaderTests
{
    [Fact]
    public void Loader_accepts_only_the_two_declarative_package_files()
    {
        using var package = Package.Create();
        var skill = new SkillPackageLoader().Load(package.Root);

        Assert.Equal("demo.skill", skill.Id);
        Assert.Equal("1.0.0", skill.Version);
        Assert.Equal(SkillStepKind.CallTool, skill.Steps[0].Kind);
    }

    [Fact]
    public void Loader_rejects_unknown_manifest_fields_and_executable_content()
    {
        using var unknown = Package.Create(json => json["unknown"] = true);
        var unknownError = Assert.Throws<InvalidDataException>(() => new SkillPackageLoader().Load(unknown.Root));
        Assert.Equal("unknown_skill_field", unknownError.Message);

        using var executable = Package.Create();
        File.WriteAllText(Path.Combine(executable.Root, "run.ps1"), "Write-Host nope");
        var executableError = Assert.Throws<InvalidDataException>(() => new SkillPackageLoader().Load(executable.Root));
        Assert.Equal("executable_skill_content", executableError.Message);
    }

    [Fact]
    public void Loader_rejects_content_that_grows_after_initial_size_check()
    {
        using var package = Package.Create();
        File.AppendAllText(Path.Combine(package.Root, "instructions.md"), new string('x', SkillPackageLoader.DefaultMaxInstructionsBytes));

        var error = Assert.Throws<InvalidDataException>(() => new SkillPackageLoader().Load(package.Root));

        Assert.Equal("skill_package_too_large", error.Message);
    }

    [Fact]
    public void Registry_requires_skill_execute_and_keeps_failed_registration_atomic()
    {
        using var package = Package.Create(json =>
        {
            json["required_scopes"] = new JsonArray(ToolScope.WorkspaceRead);
        });
        var skill = new SkillPackageLoader().Load(package.Root);
        var tools = new ToolRegistry();
        tools.Register(new ToolManifest(
            "system.time", "1.0.0", "builtin", RiskClass.Read,
            new HashSet<string>([ToolScope.WorkspaceRead]),
            new JsonObject { ["type"] = "object" },
            new JsonObject { ["type"] = "object" },
            10_000, 64 * 1024, "builtin"));
        var registry = new SkillRegistry();

        var error = Assert.Throws<ArgumentException>(() => registry.Register(skill, tools));

        Assert.Equal("skill_execute_scope_missing", error.Message);
        Assert.Empty(registry.List());
    }

    private sealed class Package : IDisposable
    {
        private Package(string root) => Root = root;
        public string Root { get; }

        public static Package Create(Action<JsonObject>? mutate = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-skill-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var json = new JsonObject
            {
                ["id"] = "demo.skill",
                ["version"] = "1.0.0",
                ["input_schema"] = new JsonObject { ["type"] = "object" },
                ["output_schema"] = new JsonObject { ["type"] = "object" },
                ["required_tools"] = new JsonArray("system.time"),
                ["required_scopes"] = new JsonArray(ToolScope.WorkspaceRead, ToolScope.SkillExecute),
                ["steps"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "call_tool",
                    ["name"] = "time",
                    ["tool_id"] = "system.time",
                }),
                ["requires_approval"] = false,
                ["max_steps"] = 2,
                ["timeout_ms"] = 10_000,
                ["trust_class"] = "builtin",
            };
            mutate?.Invoke(json);
            File.WriteAllText(Path.Combine(root, "skill.json"), json.ToJsonString());
            File.WriteAllText(Path.Combine(root, "instructions.md"), "Data only.");
            return new Package(root);
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
