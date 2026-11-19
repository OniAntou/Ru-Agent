using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// A file this session read in full, in one page, may be replaced by write_file without
/// overwrite=true while it still holds what the session read. Benchmarks showed models
/// calling write_file on existing files and paying a file_exists failure plus a retry.
/// </summary>
public sealed partial class ToolExecutionServiceTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("read_file")]
    public async Task Write_file_replaces_a_file_this_session_read_in_full(string readTool)
    {
        var fixture = CreateFullAccessService("ru-fullread-rewrite-");
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "original\n");
            Assert.Equal(ToolResultStatuses.Succeeded, (await Read(fixture, "r1", readTool, new JsonObject { ["path"] = "pricing.py" })).Status);

            var rewrite = await Write(fixture, "w1", "pricing.py", "replaced\n");

            Assert.Equal(ToolResultStatuses.Succeeded, rewrite.Status);
            Assert.Equal("replaced\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_after_a_full_read_follows_the_sessions_own_edits()
    {
        var fixture = CreateFullAccessService("ru-fullread-edit-");
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "alpha\n");
            Assert.Equal(ToolResultStatuses.Succeeded, (await Read(fixture, "r1", "read", new JsonObject { ["path"] = "pricing.py" })).Status);
            var edit = await fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, "e1", "edit_file",
                new JsonObject
                {
                    ["path"] = "pricing.py",
                    ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "alpha", ["new_string"] = "beta" }),
                }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, edit.Status);

            var rewrite = await Write(fixture, "w1", "pricing.py", "gamma\n");

            Assert.Equal(ToolResultStatuses.Succeeded, rewrite.Status);
            Assert.Equal("gamma\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_when_the_file_changed_after_the_full_read()
    {
        var fixture = CreateFullAccessService("ru-fullread-changed-");
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "original\n");
            Assert.Equal(ToolResultStatuses.Succeeded, (await Read(fixture, "r1", "read", new JsonObject { ["path"] = "pricing.py" })).Status);
            File.WriteAllText(target, "user edit\n");

            var rewrite = await Write(fixture, "w1", "pricing.py", "replaced\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal("user edit\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    public static TheoryData<string, JsonObject> PartialReads() => new()
    {
        { "read", new JsonObject { ["path"] = "pricing.py", ["limit"] = 4 } },
        { "read", new JsonObject { ["path"] = "pricing.py", ["offset"] = 1 } },
        { "read_file", new JsonObject { ["path"] = "pricing.py", ["line_count"] = 1 } },
        { "read_file", new JsonObject { ["path"] = "pricing.py", ["start_line"] = 2 } },
    };

    [Theory]
    [MemberData(nameof(PartialReads))]
    public async Task Write_file_stays_create_only_after_a_partial_read(string readTool, JsonObject arguments)
    {
        var fixture = CreateFullAccessService("ru-fullread-partial-");
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "line one\nline two\n");
            Assert.Equal(ToolResultStatuses.Succeeded, (await Read(fixture, "r1", readTool, arguments)).Status);

            var rewrite = await Write(fixture, "w1", "pricing.py", "replaced\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal("line one\nline two\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_after_a_full_read_the_host_could_clip()
    {
        var fixture = CreateFullAccessService("ru-fullread-large-");
        var target = Path.Combine(fixture.Root, "workspace", "large.txt");
        var original = new string('x', 100 * 1024) + "\n";
        try
        {
            File.WriteAllText(target, original);
            var read = await Read(fixture, "r1", "read", new JsonObject { ["path"] = "large.txt" });
            Assert.Equal(ToolResultStatuses.Succeeded, read.Status);

            var rewrite = await Write(fixture, "w1", "large.txt", "replaced\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal(original, File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_replaces_a_file_read_in_full_under_the_standard_preset()
    {
        var fixture = CreateLeaseService("ru-fullread-standard-", EnvelopeTestSnapshots.StandardEmpty());
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "original\n");
            Assert.Equal(ToolResultStatuses.Succeeded, (await Read(fixture, "r1", "read", new JsonObject { ["path"] = "pricing.py" })).Status);

            var rewrite = await Write(fixture, "w1", "pricing.py", "replaced\n");

            Assert.Equal(ToolResultStatuses.Succeeded, rewrite.Status);
            Assert.Equal("replaced\n", File.ReadAllText(target));
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_after_reading_an_extracted_document()
    {
        var fixture = CreateFullAccessService("ru-fullread-pdf-");
        var target = Path.Combine(fixture.Root, "workspace", "spec.pdf");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4)
            .AddText("short spec", 12, new UglyToad.PdfPig.Core.PdfPoint(72, 720), font);
        var original = builder.Build();
        try
        {
            File.WriteAllBytes(target, original);
            var read = await Read(fixture, "r1", "read", new JsonObject { ["path"] = "spec.pdf" });
            Assert.Equal(ToolResultStatuses.Succeeded, read.Status);

            var rewrite = await Write(fixture, "w1", "spec.pdf", "short spec\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal(original, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public void A_full_read_is_known_only_to_the_reading_session_and_only_for_that_content()
    {
        var known = new SessionKnownFiles();
        var hash = new string('a', 64);

        known.RecordFullRead("session-a", @"C:\w\pricing.py", hash);

        Assert.True(known.IsUnchangedSinceSessionLastSaw("session-a", @"C:\w\pricing.py", hash));
        Assert.False(known.IsUnchangedSinceSessionLastSaw("session-b", @"C:\w\pricing.py", hash));
        Assert.False(known.IsUnchangedSinceSessionLastSaw("session-a", @"C:\w\pricing.py", new string('b', 64)));
        known.Forget("session-a", @"C:\w\pricing.py");
        Assert.False(known.IsUnchangedSinceSessionLastSaw("session-a", @"C:\w\pricing.py", hash));
    }

    private static Task<ToolResult> Read(
        (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) fixture,
        string requestId, string toolId, JsonObject arguments) =>
        fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, requestId, toolId, arguments),
            CancellationToken.None);
}
