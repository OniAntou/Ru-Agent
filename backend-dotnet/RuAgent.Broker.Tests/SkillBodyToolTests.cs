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
    public async Task Skill_tool_query_returns_bounded_metadata_and_resource_base()
    {
        using var fixture = Fixture.Create();
        var service = fixture.CreateService();
        var result = await service.ExecuteAsync(Envelope("""{"query":"pdf.analyzer"}"""), CancellationToken.None);
        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        var item = Assert.Single(result.Output["items"]!.AsArray())!.AsObject();
        Assert.Equal("pdf.analyzer", item["id"]!.GetValue<string>());
        Assert.Equal(64, item["revision"]!.GetValue<string>().Length);
        Assert.Contains("pdf.analyzer", item["resource_base"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Skill_tool_uses_the_authenticated_run_lease_workspace()
    {
        using var fixture = Fixture.Create();
        var runWorkspace = Path.Combine(fixture.Root, "run-workspace");
        var package = Directory.CreateDirectory(Path.Combine(runWorkspace, "skills", "run-only"));
        File.WriteAllText(Path.Combine(package.FullName, "SKILL.md"), "---\nname: Run only\ndescription: Run workspace skill\n---\nRun workspace body.");
        var snapshot = EnvelopeTestSnapshots.StandardEmpty();
        const string runId = "run_skill_workspace_1234567890";
        var lease = BrokerRunLease.CreateValidated(
            runId, Session, snapshot.Workspace.WorkspaceId, runWorkspace,
            snapshot.Workspace.VolumeSerialNumber, snapshot.Workspace.RootFileId,
            snapshot.Workspace.DisplayIdentityHash, snapshot.Preset, snapshot.Version,
            EnvelopeTestSnapshots.HashOf(snapshot), DateTimeOffset.UtcNow.AddMinutes(5), snapshot);
        Assert.True(fixture.Session.TryOpenRunLease(lease, out var leaseError), leaseError);
        var service = fixture.CreateService();
        var result = await service.ExecuteAuthenticatedV2Async(
            new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2,
                $"skill-workspace-{Guid.NewGuid():N}",
                AgentMessageTypes.ToolExecute,
                Session,
                null,
                30_000,
                new JsonObject
                {
                    ["tool_id"] = "skill",
                    ["tool_version"] = "1.0.0",
                    ["capability_class"] = "internal",
                    ["run_id"] = runId,
                    ["arguments"] = new JsonObject { ["name"] = "run-only" },
                }),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
        Assert.Contains(runWorkspace, result.Output["source_path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Run workspace body", result.Output["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"pdf.analyzer\",\"query\":\"pdf\"}")]
    public async Task Skill_tool_rejects_neither_or_both_name_and_query(string arguments)
    {
        using var fixture = Fixture.Create();
        var result = await fixture.CreateService().ExecuteAsync(Envelope(arguments), CancellationToken.None);
        Assert.Equal(ToolResultStatuses.Failed, result.Status);
    }

    [Fact]
    public async Task Skill_tool_query_does_not_advertise_manual_only_entries()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("manual", "---\nname: Manual\ndescription: Explicit workflow\ndisable-model-invocation: true\n---\nBody");
        var result = await fixture.CreateService().ExecuteAsync(Envelope("""{"query":"manual"}"""), CancellationToken.None);
        Assert.DoesNotContain(result.Output["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == "manual");
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
    public void Manual_only_skill_is_not_loadable_without_explicit_invocation()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("manual", "---\nname: Manual flow\ndescription: Must be explicit\ndisable-model-invocation: true\n---\n\nDo this.");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json"));
        Assert.False(Assert.Single(catalog.Discover().Items, item => item.Id == "manual").ModelInvocable);
        Assert.Null(catalog.LoadBody("manual"));
        Assert.NotNull(catalog.LoadBody("manual", allowExplicit: true));
    }

    [Fact]
    public void Catalog_preferences_and_revision_guard_are_workspace_scoped()
    {
        using var fixture = Fixture.Create();
        var prefs = Path.Combine(fixture.Root, "prefs.json");
        var catalog = new SkillCatalog(fixture.Root, prefs);
        var item = Assert.Single(catalog.Discover().Items, item => item.Id == "pdf.analyzer");
        Assert.True(catalog.SetEnabled(item.Id, false).Success);
        Assert.False(Assert.Single(catalog.Discover().Items, candidate => candidate.Id == item.Id).Enabled);
        Assert.Null(catalog.LoadBody(item.Id));
        Assert.True(catalog.SetEnabled(item.Id, true).Success);
        Assert.Null(catalog.LoadBody(item.Id, expectedRevision: "stale"));
        Assert.NotNull(catalog.LoadBody(item.Id, expectedRevision: item.Revision));
        Assert.False(catalog.TryLoadBody(item.Id, out _, out var errorCode, expectedRevision: "stale"));
        Assert.Equal("skill_revision_changed", errorCode);
    }

    [Fact]
    public void Catalog_scans_metadata_without_inlining_skill_bodies()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("large", "---\nname: Large\ndescription: Metadata only\n---\n\n" + new string('x', 32 * 1024));
        var item = Assert.Single(new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json")).Discover().Items, item => item.Id == "large");
        Assert.Equal("Large", item.Name);
        Assert.DoesNotContain("xxxx", item.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_markdown_metadata_budget_does_not_reject_a_split_utf8_character()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("unicode-heading", new string('界', 1000) + "\n\nWorkflow.");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json"));
        Assert.Contains(catalog.Discover().Items, item => item.Id == "unicode-heading");
        Assert.NotNull(catalog.LoadBody("unicode-heading"));
    }

    [Fact]
    public void Metadata_and_body_parse_frontmatter_delimiter_whitespace_consistently()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("spaced", "--- \t\r\nname: spaced\r\ndescription: Explicit workflow\r\ndisable-model-invocation: true\r\n---\t \r\nWorkflow.");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json"));
        Assert.False(catalog.FindById("spaced")!.ModelInvocable);
        Assert.Null(catalog.LoadBody("spaced"));
        Assert.NotNull(catalog.LoadBody("spaced", allowExplicit: true));
    }

    [Fact]
    public void Expected_body_digest_rejects_content_changes_even_if_discovery_stamp_is_preserved()
    {
        using var fixture = Fixture.Create();
        const string header = "---\nname: versioned\ndescription: Version check\n---\n";
        fixture.AddSkill("versioned", header + "Old workflow.");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json"));
        var loaded = Assert.IsType<SkillBody>(catalog.LoadBody("versioned"));
        var timestamp = File.GetLastWriteTimeUtc(loaded.SourcePath);
        File.WriteAllText(loaded.SourcePath, header + "New workflow.");
        File.SetLastWriteTimeUtc(loaded.SourcePath, timestamp);

        Assert.Equal(loaded.Revision, catalog.FindById("versioned")!.Revision);
        Assert.False(catalog.TryLoadBody("versioned", out _, out var errorCode,
            expectedBodySha256: loaded.BodySha256));
        Assert.Equal("skill_digest_mismatch", errorCode);
        Assert.Equal("Old workflow.", loaded.Body);
    }

    [Fact]
    public void Direct_id_lookup_loads_skills_beyond_the_catalog_listing_cap()
    {
        using var fixture = Fixture.Create();
        for (var index = 0; index < 130; index++)
            fixture.AddSkill($"skill-{index:000}", $"---\nname: Skill {index}\ndescription: A bounded entry\n---\nBody {index}");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "prefs.json"));
        var listing = catalog.Discover();
        Assert.True(listing.Truncated);
        Assert.Null(listing.Items.FirstOrDefault(item => item.Id == "skill-129"));
        var body = catalog.LoadBody("skill-129");
        Assert.NotNull(body);
        Assert.Contains("Body 129", body.Body, StringComparison.Ordinal);
        Assert.Equal("skill-129", Assert.Single(catalog.Discover("skill-129").Items).Id);
    }

    [Fact]
    public void Shared_catalog_cache_invalidates_for_edits_creation_deletion_and_other_workspaces_are_isolated()
    {
        using var fixture = Fixture.Create();
        var preferences = Path.Combine(fixture.Root, "shared-prefs.json");
        var first = new SkillCatalog(fixture.Root, preferences);
        var before = Assert.Single(first.Discover().Items, item => item.Id == "pdf.analyzer");
        Assert.True(first.SetEnabled(before.Id, false).Success);
        Assert.False(Assert.Single(new SkillCatalog(fixture.Root, preferences).Discover().Items, item => item.Id == before.Id).Enabled);

        var otherWorkspace = Path.Combine(fixture.Root, "other-workspace");
        Directory.CreateDirectory(Path.Combine(otherWorkspace, "skills", before.Id));
        File.WriteAllText(Path.Combine(otherWorkspace, "skills", before.Id, "SKILL.md"), "---\nname: Other\ndescription: Workspace two\n---\nBody");
        Assert.True(Assert.Single(new SkillCatalog(otherWorkspace, preferences).Discover().Items,
            item => item.Id == before.Id).Enabled);

        var skillFile = Path.Combine(fixture.Root, "skills", before.Id, "SKILL.md");
        File.AppendAllText(skillFile, " edited");
        var edited = Assert.Single(new SkillCatalog(fixture.Root, preferences).Discover().Items, item => item.Id == before.Id);
        Assert.NotEqual(before.Revision, edited.Revision);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "skills", "new-skill"));
        File.WriteAllText(Path.Combine(fixture.Root, "skills", "new-skill", "SKILL.md"), "---\nname: New\ndescription: Newly found\n---\n" + new string('x', 100 * 1024));
        Assert.Contains(new SkillCatalog(fixture.Root, preferences).Discover().Items, item => item.Id == "new-skill");
        Directory.Delete(Path.Combine(fixture.Root, "skills", "new-skill"), recursive: true);
        Assert.DoesNotContain(new SkillCatalog(fixture.Root, preferences).Discover().Items, item => item.Id == "new-skill");
    }

    [Fact]
    public void Existing_catalog_refreshes_registered_roots_from_shared_preferences()
    {
        using var fixture = Fixture.Create();
        var preferences = Path.Combine(fixture.Root, "shared-prefs.json");
        var existing = new SkillCatalog(fixture.Root, preferences);
        var registered = new SkillCatalog(fixture.Root, preferences);
        var longLivedStore = new SkillBodyStore(fixture.Root, preferences);
        var root = Path.Combine(fixture.Root, "extra-skills");
        var skill = Directory.CreateDirectory(Path.Combine(root, "extra"));
        File.WriteAllText(Path.Combine(skill.FullName, "SKILL.md"), "---\nname: Extra\ndescription: Added root\n---\nBody");

        Assert.True(registered.AddRoot(root).Success);
        Assert.Contains(existing.Discover().Items, item => item.Id == "extra");
        Assert.Contains(longLivedStore.Discover().Items, item => item.Id == "extra");
        Assert.True(registered.RemoveRoot(root).Success);
        Assert.DoesNotContain(existing.Discover().Items, item => item.Id == "extra");
        Assert.DoesNotContain(longLivedStore.Discover().Items, item => item.Id == "extra");
    }

    [Fact]
    public void Registered_skill_root_limit_includes_default_roots()
    {
        using var fixture = Fixture.Create();
        var preferences = Path.Combine(fixture.Root, "prefs.json");
        var catalog = new SkillCatalog(fixture.Root, preferences);
        var defaultRootCount = SkillRoots.For(fixture.Root).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var allowedRegisteredRoots = 16 - Math.Min(16, defaultRootCount);
        for (var index = 0; index < 16; index++)
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture.Root, $"registered-{index}")).FullName;
            var result = catalog.AddRoot(root);
            if (index < allowedRegisteredRoots) Assert.True(result.Success);
            else { Assert.False(result.Success); Assert.Equal("root_limit", result.Code); }
        }
    }

    [Fact]
    public void Shared_user_library_loads_across_workspaces_without_registration_and_keeps_project_priority()
    {
        using var fixture = Fixture.Create();
        var profile = Directory.CreateDirectory(Path.Combine(fixture.Root, "profile")).FullName;
        var shared = Directory.CreateDirectory(Path.Combine(profile, ".agents", "skills", "shared-flow")).FullName;
        File.WriteAllText(Path.Combine(shared, "SKILL.md"), "---\nname: shared-flow\ndescription: Shared workflow\n---\nShared body.");
        var secondWorkspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "second-workspace")).FullName;
        SkillCatalog CatalogFor(string workspace) => new(workspace, Path.Combine(fixture.Root, "prefs.json"),
            SkillRoots.For(workspace, Path.Combine(fixture.Root, "local"), profile, configuredRoot: ""));

        var first = Assert.IsType<SkillBody>(CatalogFor(fixture.Root).LoadBody("shared-flow"));
        var second = Assert.IsType<SkillBody>(CatalogFor(secondWorkspace).LoadBody("shared-flow"));
        Assert.Equal(shared, first.ResourceBase);
        Assert.Equal(first.BodySha256, second.BodySha256);
        fixture.AddSkill("shared-flow", "---\nname: shared-flow\ndescription: Project workflow\n---\nProject body.");
        Assert.Equal("Project body.", CatalogFor(fixture.Root).LoadBody("shared-flow")!.Body);
        Assert.Equal("Shared body.", CatalogFor(secondWorkspace).LoadBody("shared-flow")!.Body);
    }

    [Fact]
    public void Cached_discovery_detects_a_repaired_definition_that_was_previously_omitted()
    {
        using var fixture = Fixture.Create();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills", "repaired")).FullName;
        var file = Path.Combine(directory, "SKILL.md");
        File.WriteAllText(file, "---\nname: repaired\n");
        var catalog = new SkillCatalog(fixture.Root, Path.Combine(fixture.Root, "isolated.json"));
        Assert.DoesNotContain(catalog.Discover().Items, item => item.Id == "repaired");
        var directoryStamp = Directory.GetLastWriteTimeUtc(directory);
        File.WriteAllText(file, "---\nname: repaired\ndescription: Repaired metadata\n---\nWorkflow.");
        Directory.SetLastWriteTimeUtc(directory, directoryStamp);
        Assert.Contains(catalog.Discover().Items, item => item.Id == "repaired");
    }

    [Fact]
    public void Windows_workspace_preference_key_is_case_insensitive()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = Fixture.Create();
        var preferences = Path.Combine(fixture.Root, "prefs.json");
        Assert.True(new SkillCatalog(fixture.Root, preferences).SetEnabled("pdf.analyzer", false).Success);
        var caseVariant = fixture.Root.ToUpperInvariant();
        Assert.False(Assert.Single(new SkillCatalog(caseVariant, preferences).Discover().Items, item => item.Id == "pdf.analyzer").Enabled);
    }

    [Fact]
    public async Task Unknown_requirement_fails_closed_naming_the_token()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("unobtainium.required", "---\nname: unobtainium.required\ndescription: Needs unknown capability\nrequires: unobtainium\n---\n\nRun the script.");
        var service = fixture.CreateService(pythonAvailable: () => true);

        var result = await service.ExecuteAsync(
            Envelope("""{"name":"unobtainium.required"}"""),
            CancellationToken.None);

        Assert.Equal(ToolResultStatuses.Failed, result.Status);
        Assert.Equal("unsupported_skill_requirement", result.ErrorCode);
        Assert.Contains("unobtainium", result.SafeSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Run the script", result.Output.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_requirement_uses_the_injected_bounded_capability_probe()
    {
        using var fixture = Fixture.Create();
        fixture.AddSkill("node.required", "---\nname: node.required\ndescription: Needs Node\nrequires:\n  - node\n---\n\nRun the script.");
        var unavailable = await fixture.CreateService(skillCapabilityAvailable: _ => false).ExecuteAsync(Envelope("""{"name":"node.required"}"""), CancellationToken.None);
        var available = await fixture.CreateService(skillCapabilityAvailable: capability => capability == "node").ExecuteAsync(Envelope("""{"name":"node.required"}"""), CancellationToken.None);
        Assert.Equal("node_unavailable", unavailable.ErrorCode);
        Assert.Equal(ToolResultStatuses.Succeeded, available.Status);
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

    [Fact]
    public void Skill_body_store_denies_a_hard_link_to_an_outside_file()
    {
        using var fixture = Fixture.Create();
        var outside = Path.Combine(Path.GetTempPath(), "ru-skill-hardlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var secret = Path.Combine(outside, "SKILL.md");
            File.WriteAllText(secret, "---\nname: linked\ndescription: outside file\n---\n\nSECRET-BODY");
            var skillDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "skills", "linked")).FullName;
            var linkedSkill = Path.Combine(skillDirectory, "SKILL.md");
            if (!FileReadAdapterTests.TryCreateHardLink(linkedSkill, secret))
            {
                return;
            }

            var body = new SkillBodyStore(fixture.Root).LoadBody("linked");

            Assert.Null(body);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
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
                "---\nname: pdf.analyzer\ndescription: Analyze PDF files\nallowed-tools: read, powershell\n---\n\n# PDF Analyzer\n\nRead the PDF with the read tool.",
                new System.Text.UTF8Encoding(false));
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            return new Fixture(
                root,
                BrokerSession.CreateForHost(sessionRoot, DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public ToolExecutionService CreateService(Func<bool>? pythonAvailable = null, Func<string, bool>? skillCapabilityAvailable = null)
        {
            Session.Open(
                Session_,
                new HashSet<string>(
                    [RuAgent.Broker.Policy.ToolScope.SystemRead],
                    StringComparer.Ordinal));
            return new ToolExecutionService(Session, Root, pythonAvailable: pythonAvailable, skillCapabilityAvailable: skillCapabilityAvailable);
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
