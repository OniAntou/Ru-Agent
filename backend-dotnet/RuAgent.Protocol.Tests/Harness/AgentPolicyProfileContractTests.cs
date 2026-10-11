using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests.Harness;

public sealed class AgentPolicyProfileContractTests
{
    [Theory]
    [InlineData("plan", AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow)]
    [InlineData("plan", AgentPolicySubject.Web, AgentPolicyDecision.Allow)]
    [InlineData("plan", AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Deny)]
    [InlineData("plan", AgentPolicySubject.WorkspaceTerminal, AgentPolicyDecision.Deny)]
    [InlineData("plan", AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Ask)]
    [InlineData("plan", AgentPolicySubject.ExternalTerminal, AgentPolicyDecision.Deny)]
    [InlineData("plan", AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny)]
    [InlineData("plan", AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny)]
    [InlineData("standard", AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow)]
    [InlineData("standard", AgentPolicySubject.Web, AgentPolicyDecision.Allow)]
    [InlineData("standard", AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Allow)]
    [InlineData("standard", AgentPolicySubject.WorkspaceTerminal, AgentPolicyDecision.Allow)]
    [InlineData("standard", AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Ask)]
    [InlineData("standard", AgentPolicySubject.ExternalTerminal, AgentPolicyDecision.Ask)]
    [InlineData("standard", AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny)]
    [InlineData("standard", AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny)]
    [InlineData("full_access", AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.Web, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.WorkspaceTerminal, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.ExternalTerminal, AgentPolicyDecision.Allow)]
    [InlineData("full_access", AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny)]
    [InlineData("full_access", AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny)]
    public void Catalog_returns_canonical_decision(
        string preset,
        AgentPolicySubject subject,
        AgentPolicyDecision expected)
    {
        Assert.Equal(expected, AgentPolicyProfileCatalog.Get(preset).DecisionFor(subject));
    }

    [Fact]
    public void Catalog_exposes_exactly_three_profiles()
    {
        var profiles = AgentPolicyProfileCatalog.Profiles;

        Assert.Equal(3, profiles.Count);
        Assert.Equal(
            ["plan", "standard", "full_access"],
            profiles.Select(profile => profile.Preset));
    }

    [Fact]
    public void Every_profile_contains_each_subject_once()
    {
        foreach (var profile in AgentPolicyProfileCatalog.Profiles)
        {
            Assert.Equal(8, profile.Rules.Count);
            Assert.Equal(8, profile.Rules.Select(rule => rule.Subject).Distinct().Count());
            Assert.Equal(
                Enum.GetValues<AgentPolicySubject>().OrderBy(subject => (int)subject),
                profile.Rules.Select(rule => rule.Subject).OrderBy(subject => (int)subject));
        }
    }

    [Fact]
    public void Unknown_preset_is_rejected()
    {
        var error = Assert.Throws<AgentProtocolException>(() => AgentPolicyProfileCatalog.Get("trusted"));
        Assert.Equal("invalid_preset", error.Code);
        Assert.False(AgentPolicyProfileCatalog.TryGet("trusted", out var profile));
        Assert.Null(profile);
    }

    [Fact]
    public void Direct_construction_rejects_unknown_preset_with_invalid_preset()
    {
        var error = Assert.Throws<AgentProtocolException>(() => new AgentPolicyProfile(
            "2",
            "trusted",
            PresetTerminalAuthority.Denied,
            ValidRules()));

        Assert.Equal("invalid_preset", error.Code);
    }

    [Fact]
    public void Json_decoding_rejects_unknown_preset_with_invalid_preset()
    {
        var node = ProfileJson();
        node["preset"] = "trusted";

        var error = Assert.Throws<AgentProtocolException>(() => AgentPolicyProfileCatalog.ReadProfile(node));

        Assert.Equal("invalid_preset", error.Code);
    }

    [Fact]
    public void Tampered_plan_decision_is_rejected()
    {
        var rules = AgentPolicyProfileCatalog.Get("plan").Rules
            .Select(rule => rule.Subject == AgentPolicySubject.WorkspaceMutation
                ? new AgentPolicyRule(rule.Subject, AgentPolicyDecision.Allow)
                : rule)
            .ToList();

        var error = Assert.Throws<AgentProtocolException>(() => new AgentPolicyProfile(
            "2",
            "plan",
            PresetTerminalAuthority.Denied,
            rules));

        Assert.Equal("invalid_profile", error.Code);

        var node = ProfileJson();
        node["preset"] = "plan";
        node["terminal_authority"] = "denied";
        ((JsonObject)((JsonArray)node["rules"]!)[2]!)["decision"] = "allow";
        AssertProfileJsonRejected(node);
    }

    [Fact]
    public void Tampered_full_access_decision_is_rejected()
    {
        var rules = AgentPolicyProfileCatalog.Get("full_access").Rules
            .Select(rule => rule.Subject == AgentPolicySubject.ExternalTerminal
                ? new AgentPolicyRule(rule.Subject, AgentPolicyDecision.Deny)
                : rule)
            .ToList();

        var error = Assert.Throws<AgentProtocolException>(() => new AgentPolicyProfile(
            "2",
            "full_access",
            PresetTerminalAuthority.WindowsCurrentUser,
            rules));

        Assert.Equal("invalid_profile", error.Code);

        var node = ProfileJson();
        node["preset"] = "full_access";
        node["terminal_authority"] = "windows_current_user";
        ((JsonObject)((JsonArray)node["rules"]!)[5]!)["decision"] = "deny";
        AssertProfileJsonRejected(node);
    }

    [Fact]
    public void Profile_rules_are_copied_into_a_read_only_collection()
    {
        var source = ValidRules();
        var profile = new AgentPolicyProfile("2", "standard", PresetTerminalAuthority.WorkspaceCurrentUser, source);

        source.Clear();

        Assert.Equal(8, profile.Rules.Count);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentPolicyRule>)profile.Rules).Add(
                new AgentPolicyRule(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow)));
    }

    [Fact]
    public void Profile_round_trip_is_canonical()
    {
        var profile = AgentPolicyProfileCatalog.Get("standard");
        var encoded = AgentPolicyProfileCatalog.WriteProfile(profile);
        var decoded = AgentPolicyProfileCatalog.ReadProfile(encoded);

        Assert.True(profile.HasSameSemanticsAs(decoded));
        Assert.Equal(encoded.ToJsonString(), AgentPolicyProfileCatalog.WriteProfile(decoded).ToJsonString());
    }

    [Theory]
    [InlineData("plan", PresetTerminalAuthority.Denied, "denied")]
    [InlineData("standard", PresetTerminalAuthority.WorkspaceCurrentUser, "workspace_current_user")]
    [InlineData("full_access", PresetTerminalAuthority.WindowsCurrentUser, "windows_current_user")]
    public void Catalog_exposes_each_terminal_authority(
        string preset,
        PresetTerminalAuthority expectedAuthority,
        string expectedWireValue)
    {
        var profile = AgentPolicyProfileCatalog.Get(preset);

        Assert.Equal(expectedAuthority, profile.TerminalAuthority);
        Assert.Equal(expectedWireValue, AgentPolicyProfileCatalog.DescribeTerminalAuthority(profile.TerminalAuthority));
    }

    [Fact]
    public void Construction_rejects_missing_duplicate_and_unknown_subjects()
    {
        var missing = ValidRules();
        missing.RemoveAt(0);
        AssertProfileRejected(missing);

        var duplicate = ValidRules();
        duplicate[1] = new AgentPolicyRule(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow);
        AssertProfileRejected(duplicate);

        var unknown = ValidRules();
        unknown.Add(new AgentPolicyRule((AgentPolicySubject)999, AgentPolicyDecision.Allow));
        AssertProfileRejected(unknown);
    }

    [Fact]
    public void Json_decoding_rejects_missing_duplicate_unknown_and_extra_subjects()
    {
        var missing = ProfileJson();
        ((JsonArray)missing["rules"]!).RemoveAt(0);
        AssertProfileJsonRejected(missing);

        var duplicate = ProfileJson();
        var rules = (JsonArray)duplicate["rules"]!;
        rules.Add(rules[0]!.DeepClone());
        AssertProfileJsonRejected(duplicate);

        var unknown = ProfileJson();
        ((JsonArray)unknown["rules"]!).Add(new JsonObject
        {
            ["subject"] = "unknown_subject",
            ["decision"] = "allow",
        });
        AssertProfileJsonRejected(unknown);

        var extra = ProfileJson();
        ((JsonObject)((JsonArray)extra["rules"]!)[0]!)["extra"] = true;
        AssertProfileJsonRejected(extra);
    }

    [Fact]
    public void Json_decoding_rejects_invalid_profile_fields()
    {
        var missingVersion = ProfileJson();
        missingVersion.Remove("version");
        AssertProfileJsonRejected(missingVersion);

        var extraProfileField = ProfileJson();
        extraProfileField["extra"] = true;
        AssertProfileJsonRejected(extraProfileField);

        var wrongVersion = ProfileJson();
        wrongVersion["version"] = "1";
        AssertProfileJsonRejected(wrongVersion);
    }

    private static void AssertProfileRejected(IReadOnlyList<AgentPolicyRule> rules)
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            new AgentPolicyProfile("2", "standard", PresetTerminalAuthority.WorkspaceCurrentUser, rules));
        Assert.Equal("invalid_profile", error.Code);
    }

    private static void AssertProfileJsonRejected(JsonObject node)
    {
        var error = Assert.Throws<AgentProtocolException>(() => AgentPolicyProfileCatalog.ReadProfile(node));
        Assert.Equal("invalid_profile", error.Code);
    }

    private static List<AgentPolicyRule> ValidRules() =>
    [
        new(AgentPolicySubject.WorkspaceRead, AgentPolicyDecision.Allow),
        new(AgentPolicySubject.Web, AgentPolicyDecision.Allow),
        new(AgentPolicySubject.WorkspaceMutation, AgentPolicyDecision.Allow),
        new(AgentPolicySubject.WorkspaceTerminal, AgentPolicyDecision.Allow),
        new(AgentPolicySubject.ExternalStructuredFile, AgentPolicyDecision.Ask),
        new(AgentPolicySubject.ExternalTerminal, AgentPolicyDecision.Ask),
        new(AgentPolicySubject.StructuredProtectedPath, AgentPolicyDecision.Deny),
        new(AgentPolicySubject.HardInvalid, AgentPolicyDecision.Deny),
    ];

    private static JsonObject ProfileJson() => new()
    {
        ["version"] = "2",
        ["preset"] = "standard",
        ["terminal_authority"] = "workspace_current_user",
        ["rules"] = new JsonArray(
            new JsonObject { ["subject"] = "workspace_read", ["decision"] = "allow" },
            new JsonObject { ["subject"] = "web", ["decision"] = "allow" },
            new JsonObject { ["subject"] = "workspace_mutation", ["decision"] = "allow" },
            new JsonObject { ["subject"] = "workspace_terminal", ["decision"] = "allow" },
            new JsonObject { ["subject"] = "external_structured_file", ["decision"] = "ask" },
            new JsonObject { ["subject"] = "external_terminal", ["decision"] = "ask" },
            new JsonObject { ["subject"] = "structured_protected_path", ["decision"] = "deny" },
            new JsonObject { ["subject"] = "hard_invalid", ["decision"] = "deny" }),
    };
}
