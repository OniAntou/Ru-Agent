using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Skills;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService : IToolInvoker
{
    public IReadOnlyList<ToolManifest> ListTools() =>
        _catalog.Registry.List().Select(binding => binding.Manifest).ToArray();

    public IReadOnlyList<AgentToolCapability> ListCapabilities() =>
        _catalog.Registry.ListCapabilities();

    public IReadOnlyList<AgentToolCapability> ListCapabilities(CapabilityProfile profile) =>
        _catalog.Registry.ListCapabilities(profile, EnabledWebScopes());

    public IReadOnlyList<SkillManifest> ListSkills() => _skills.List();

    public IReadOnlyList<SkillManifest> ListSkills(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_session.Owns(sessionId))
        {
            return Array.Empty<SkillManifest>();
        }

        return _skills.ListGranted(CreateSkillGrant(sessionId));
    }

    public SkillManifest? ResolveSkill(string id, string version)
    {
        try
        {
            return _skills.Resolve(id, version);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public IReadOnlyList<SkillManifest> ListAllSkills() => _skills.List();
}
