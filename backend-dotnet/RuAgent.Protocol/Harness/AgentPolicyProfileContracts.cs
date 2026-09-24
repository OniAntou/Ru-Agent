using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Harness;

public enum AgentPolicySubject
{
    WorkspaceRead,
    Web,
    WorkspaceMutation,
    BoundedLocalTerminal,
    ExternalStructuredFile,
    RiskyTerminal,
    StructuredProtectedPath,
    HardInvalid,
}

public enum AgentPolicyDecision
{
    Allow,
    Ask,
    Deny,
}

public enum PresetTerminalAuthority
{
    Denied,
    BoundedLocal,
    WindowsCurrentUser,
}

public sealed record AgentPolicyRule(
    AgentPolicySubject Subject,
    AgentPolicyDecision Decision);

public sealed record AgentPolicyProfile
{
    private static readonly AgentPolicySubject[] OrderedSubjects =
    [
        AgentPolicySubject.WorkspaceRead,
        AgentPolicySubject.Web,
        AgentPolicySubject.WorkspaceMutation,
        AgentPolicySubject.BoundedLocalTerminal,
        AgentPolicySubject.ExternalStructuredFile,
        AgentPolicySubject.RiskyTerminal,
        AgentPolicySubject.StructuredProtectedPath,
        AgentPolicySubject.HardInvalid,
    ];

    public AgentPolicyProfile(
        string version,
        string preset,
        PresetTerminalAuthority terminalAuthority,
        IReadOnlyList<AgentPolicyRule> rules)
    {
        ValidateHeader(version, preset, terminalAuthority);
        Version = version;
        Preset = preset;
        TerminalAuthority = terminalAuthority;
        Rules = CopyRules(preset, rules);
    }

    public string Version { get; }
    public string Preset { get; }
    public PresetTerminalAuthority TerminalAuthority { get; }
    public IReadOnlyList<AgentPolicyRule> Rules { get; }

    public AgentPolicyDecision DecisionFor(AgentPolicySubject subject)
    {
        if (!IsDefined(subject))
        {
            throw new AgentProtocolException("invalid_subject");
        }

        foreach (var rule in Rules)
        {
            if (rule.Subject == subject)
            {
                return rule.Decision;
            }
        }

        throw new AgentProtocolException("invalid_profile");
    }

    public bool HasSameSemanticsAs(AgentPolicyProfile other)
    {
        if (other is null
            || !string.Equals(Version, other.Version, StringComparison.Ordinal)
            || !string.Equals(Preset, other.Preset, StringComparison.Ordinal)
            || TerminalAuthority != other.TerminalAuthority
            || Rules.Count != other.Rules.Count)
        {
            return false;
        }

        foreach (var rule in Rules)
        {
            if (other.DecisionFor(rule.Subject) != rule.Decision)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<AgentPolicyRule> CopyRules(
        string preset,
        IReadOnlyList<AgentPolicyRule> rules)
    {
        if (rules is null || rules.Count != OrderedSubjects.Length)
        {
            throw InvalidProfile();
        }

        var seen = new HashSet<AgentPolicySubject>();
        var copy = new List<AgentPolicyRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (rule is null
                || !IsDefined(rule.Subject)
                || !IsDefined(rule.Decision)
                || !AgentPolicyProfileCatalog.IsCanonicalDecision(preset, rule.Subject, rule.Decision)
                || !seen.Add(rule.Subject))
            {
                throw InvalidProfile();
            }

            copy.Add(rule);
        }

        foreach (var subject in OrderedSubjects)
        {
            if (!seen.Contains(subject))
            {
                throw InvalidProfile();
            }
        }

        return Array.AsReadOnly(copy.OrderBy(rule => (int)rule.Subject).ToArray());
    }

    private static void ValidateHeader(
        string version,
        string preset,
        PresetTerminalAuthority terminalAuthority)
    {
        AgentPolicyProfileCatalog.ValidatePreset(preset);
        if (version != AgentPolicyProfileCatalog.Version
            || terminalAuthority != AgentPolicyProfileCatalog.CanonicalTerminalAuthority(preset))
        {
            throw InvalidProfile();
        }
    }

    private static bool IsDefined(AgentPolicySubject subject) =>
        subject is AgentPolicySubject.WorkspaceRead
            or AgentPolicySubject.Web
            or AgentPolicySubject.WorkspaceMutation
            or AgentPolicySubject.BoundedLocalTerminal
            or AgentPolicySubject.ExternalStructuredFile
            or AgentPolicySubject.RiskyTerminal
            or AgentPolicySubject.StructuredProtectedPath
            or AgentPolicySubject.HardInvalid;

    private static bool IsDefined(AgentPolicyDecision decision) =>
        decision is AgentPolicyDecision.Allow
            or AgentPolicyDecision.Ask
            or AgentPolicyDecision.Deny;

    private static AgentProtocolException InvalidProfile() => new("invalid_profile");
}

public static class AgentPolicyProfileCatalog
{
    public const string Version = "1";
    public const string PlanPresetWireValue = BrokerPolicyContracts.PlanPresetWireValue;
    public const string StandardPresetWireValue = BrokerPolicyContracts.StandardPresetWireValue;
    public const string FullAccessPresetWireValue = BrokerPolicyContracts.FullAccessPresetWireValue;

    internal static void ValidatePreset(string preset)
    {
        if (!IsKnownPreset(preset))
        {
            throw new AgentProtocolException("invalid_preset");
        }
    }

    internal static PresetTerminalAuthority CanonicalTerminalAuthority(string preset) => preset switch
    {
        PlanPresetWireValue => PresetTerminalAuthority.Denied,
        StandardPresetWireValue => PresetTerminalAuthority.BoundedLocal,
        FullAccessPresetWireValue => PresetTerminalAuthority.WindowsCurrentUser,
        _ => throw new AgentProtocolException("invalid_preset"),
    };

    internal static bool IsCanonicalDecision(
        string preset,
        AgentPolicySubject subject,
        AgentPolicyDecision decision) =>
        CanonicalDecision(preset, subject) == decision;

    private static bool IsKnownPreset(string preset) =>
        preset is PlanPresetWireValue or StandardPresetWireValue or FullAccessPresetWireValue;

    private static AgentPolicyDecision? CanonicalDecision(
        string preset,
        AgentPolicySubject subject) => (preset, subject) switch
    {
        (PlanPresetWireValue, AgentPolicySubject.WorkspaceRead) => AgentPolicyDecision.Allow,
        (PlanPresetWireValue, AgentPolicySubject.Web) => AgentPolicyDecision.Allow,
        (PlanPresetWireValue, AgentPolicySubject.WorkspaceMutation) => AgentPolicyDecision.Deny,
        (PlanPresetWireValue, AgentPolicySubject.BoundedLocalTerminal) => AgentPolicyDecision.Deny,
        (PlanPresetWireValue, AgentPolicySubject.ExternalStructuredFile) => AgentPolicyDecision.Deny,
        (PlanPresetWireValue, AgentPolicySubject.RiskyTerminal) => AgentPolicyDecision.Deny,
        (PlanPresetWireValue, AgentPolicySubject.StructuredProtectedPath) => AgentPolicyDecision.Deny,
        (PlanPresetWireValue, AgentPolicySubject.HardInvalid) => AgentPolicyDecision.Deny,
        (StandardPresetWireValue, AgentPolicySubject.WorkspaceRead) => AgentPolicyDecision.Allow,
        (StandardPresetWireValue, AgentPolicySubject.Web) => AgentPolicyDecision.Allow,
        (StandardPresetWireValue, AgentPolicySubject.WorkspaceMutation) => AgentPolicyDecision.Allow,
        (StandardPresetWireValue, AgentPolicySubject.BoundedLocalTerminal) => AgentPolicyDecision.Allow,
        (StandardPresetWireValue, AgentPolicySubject.ExternalStructuredFile) => AgentPolicyDecision.Ask,
        (StandardPresetWireValue, AgentPolicySubject.RiskyTerminal) => AgentPolicyDecision.Ask,
        (StandardPresetWireValue, AgentPolicySubject.StructuredProtectedPath) => AgentPolicyDecision.Deny,
        (StandardPresetWireValue, AgentPolicySubject.HardInvalid) => AgentPolicyDecision.Deny,
        (FullAccessPresetWireValue, AgentPolicySubject.WorkspaceRead) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.Web) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.WorkspaceMutation) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.BoundedLocalTerminal) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.ExternalStructuredFile) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.RiskyTerminal) => AgentPolicyDecision.Allow,
        (FullAccessPresetWireValue, AgentPolicySubject.StructuredProtectedPath) => AgentPolicyDecision.Deny,
        (FullAccessPresetWireValue, AgentPolicySubject.HardInvalid) => AgentPolicyDecision.Deny,
        _ => null,
    };

    private static readonly IReadOnlyList<AgentPolicyProfile> CatalogProfiles = BuildProfiles();
    private static readonly IReadOnlyDictionary<string, AgentPolicyProfile> ProfilesByPreset =
        CatalogProfiles.ToDictionary(profile => profile.Preset, StringComparer.Ordinal);

    public static IReadOnlyList<AgentPolicyProfile> Profiles => CatalogProfiles;

    public static AgentPolicyProfile Get(string presetWire)
    {
        if (TryGet(presetWire, out var profile) && profile is not null)
        {
            return profile;
        }

        throw new AgentProtocolException("invalid_preset");
    }

    public static bool TryGet(string presetWire, out AgentPolicyProfile? profile)
    {
        if (presetWire is null)
        {
            profile = null;
            return false;
        }

        return ProfilesByPreset.TryGetValue(presetWire, out profile);
    }

    public static string Describe(AgentPolicySubject subject) => subject switch
    {
        AgentPolicySubject.WorkspaceRead => "workspace_read",
        AgentPolicySubject.Web => "web",
        AgentPolicySubject.WorkspaceMutation => "workspace_mutation",
        AgentPolicySubject.BoundedLocalTerminal => "bounded_local_terminal",
        AgentPolicySubject.ExternalStructuredFile => "external_structured_file",
        AgentPolicySubject.RiskyTerminal => "risky_terminal",
        AgentPolicySubject.StructuredProtectedPath => "structured_protected_path",
        AgentPolicySubject.HardInvalid => "hard_invalid",
        _ => throw new AgentProtocolException("invalid_subject"),
    };

    public static string DescribeTerminalAuthority(PresetTerminalAuthority authority) => authority switch
    {
        PresetTerminalAuthority.Denied => "denied",
        PresetTerminalAuthority.BoundedLocal => "bounded_local",
        PresetTerminalAuthority.WindowsCurrentUser => "windows_current_user",
        _ => throw new AgentProtocolException("invalid_terminal_authority"),
    };

    public static JsonObject WriteProfile(AgentPolicyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var rules = new JsonArray();
        foreach (var rule in profile.Rules.OrderBy(rule => (int)rule.Subject))
        {
            rules.Add(new JsonObject
            {
                ["subject"] = Describe(rule.Subject),
                ["decision"] = DescribeDecision(rule.Decision),
            });
        }

        return new JsonObject
        {
            ["version"] = profile.Version,
            ["preset"] = profile.Preset,
            ["terminal_authority"] = DescribeTerminalAuthority(profile.TerminalAuthority),
            ["rules"] = rules,
        };
    }

    public static AgentPolicyProfile ReadProfile(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        RequireExactKeys(node, ["version", "preset", "terminal_authority", "rules"]);

        var preset = RequiredString(node, "preset");
        ValidatePreset(preset);
        var version = RequiredString(node, "version");
        var authority = RequiredString(node, "terminal_authority");
        if (!TryParseTerminalAuthority(authority, out var terminalAuthority)
            || node["rules"] is not JsonArray rulesNode)
        {
            throw InvalidProfile();
        }

        var rules = new List<AgentPolicyRule>(rulesNode.Count);
        foreach (var item in rulesNode)
        {
            if (item is not JsonObject rule
                || !HasExactKeys(rule, ["subject", "decision"])
                || !TryParseSubject(RequiredString(rule, "subject"), out var subject)
                || !TryParseDecision(RequiredString(rule, "decision"), out var decision))
            {
                throw InvalidProfile();
            }

            rules.Add(new AgentPolicyRule(subject, decision));
        }

        return new AgentPolicyProfile(version, preset, terminalAuthority, rules);
    }

    private static IReadOnlyList<AgentPolicyProfile> BuildProfiles()
    {
        var profiles = new AgentPolicyProfile[]
        {
            new(
                Version,
                PlanPresetWireValue,
                PresetTerminalAuthority.Denied,
                [
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.Web, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.BoundedLocalTerminal, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.RiskyTerminal, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny),
                ]),
            new(
                Version,
                StandardPresetWireValue,
                PresetTerminalAuthority.BoundedLocal,
                [
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.Web, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.BoundedLocalTerminal, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Ask),
                    new AgentPolicyRule(AgentPolicySubject.RiskyTerminal, AgentPolicyDecision.Ask),
                    new AgentPolicyRule(AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny),
                ]),
            new(
                Version,
                FullAccessPresetWireValue,
                PresetTerminalAuthority.WindowsCurrentUser,
                [
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.Web, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.BoundedLocalTerminal, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.RiskyTerminal, AgentPolicyDecision.Allow),
                    new AgentPolicyRule(AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny),
                    new AgentPolicyRule(AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny),
                ]),
        };

        return Array.AsReadOnly(profiles);
    }

    private static string DescribeDecision(AgentPolicyDecision decision) => decision switch
    {
        AgentPolicyDecision.Allow => BrokerPolicyContracts.AllowWireValue,
        AgentPolicyDecision.Ask => BrokerPolicyContracts.AskWireValue,
        AgentPolicyDecision.Deny => BrokerPolicyContracts.DenyWireValue,
        _ => throw new AgentProtocolException("invalid_decision"),
    };

    private static bool TryParseSubject(string value, out AgentPolicySubject subject)
    {
        switch (value)
        {
            case "workspace_read": subject = AgentPolicySubject.WorkspaceRead; return true;
            case "web": subject = AgentPolicySubject.Web; return true;
            case "workspace_mutation": subject = AgentPolicySubject.WorkspaceMutation; return true;
            case "bounded_local_terminal": subject = AgentPolicySubject.BoundedLocalTerminal; return true;
            case "external_structured_file": subject = AgentPolicySubject.ExternalStructuredFile; return true;
            case "risky_terminal": subject = AgentPolicySubject.RiskyTerminal; return true;
            case "structured_protected_path": subject = AgentPolicySubject.StructuredProtectedPath; return true;
            case "hard_invalid": subject = AgentPolicySubject.HardInvalid; return true;
            default: subject = default; return false;
        }
    }

    private static bool TryParseDecision(string value, out AgentPolicyDecision decision)
    {
        switch (value)
        {
            case BrokerPolicyContracts.AllowWireValue: decision = AgentPolicyDecision.Allow; return true;
            case BrokerPolicyContracts.AskWireValue: decision = AgentPolicyDecision.Ask; return true;
            case BrokerPolicyContracts.DenyWireValue: decision = AgentPolicyDecision.Deny; return true;
            default: decision = default; return false;
        }
    }

    private static bool TryParseTerminalAuthority(string value, out PresetTerminalAuthority authority)
    {
        switch (value)
        {
            case "denied": authority = PresetTerminalAuthority.Denied; return true;
            case "bounded_local": authority = PresetTerminalAuthority.BoundedLocal; return true;
            case "windows_current_user": authority = PresetTerminalAuthority.WindowsCurrentUser; return true;
            default: authority = default; return false;
        }
    }

    private static string RequiredString(JsonObject node, string property) =>
        node[property] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : throw InvalidProfile();

    private static void RequireExactKeys(JsonObject node, IReadOnlyList<string> expected)
    {
        if (!HasExactKeys(node, expected))
        {
            throw InvalidProfile();
        }
    }

    private static bool HasExactKeys(JsonObject node, IReadOnlyList<string> expected) =>
        node.Count == expected.Count && expected.All(node.ContainsKey);

    private static AgentProtocolException InvalidProfile() => new("invalid_profile");
}
