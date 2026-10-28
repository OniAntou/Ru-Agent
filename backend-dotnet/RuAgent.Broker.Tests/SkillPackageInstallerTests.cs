using System.IO.Compression;
using System.Net;
using System.Text;
using RuAgent.Broker.Skills;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class SkillPackageInstallerTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-skill-install-" + Guid.NewGuid().ToString("N"));

    public SkillPackageInstallerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Install_downloads_only_the_pinned_codeload_archive_and_records_receipt()
    {
        var handler = new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\ndescription: Example package\n---\nDo the work."), ("references/guide.md", "Read this.")));
        using var http = new HttpClient(handler);
        using var installer = new SkillPackageInstaller(_root, http);

        var receipt = await installer.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install);

        Assert.Equal(new Uri($"https://codeload.github.com/acme/repo/zip/{Commit}"), handler.RequestUri);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("demo", receipt.Id);
        Assert.Equal("Demo", receipt.Name);
        Assert.Equal(Commit, receipt.Commit);
        Assert.Equal(2, receipt.FileCount);
        Assert.Equal("Example package", receipt.Description);
        Assert.Equal(InstallPath("demo"), receipt.InstallPath);
        Assert.Matches("^[a-f0-9]{64}$", receipt.PackageSha256);
        Assert.Equal("Read this.", File.ReadAllText(Path.Combine(InstallPath("demo"), "references", "guide.md")));
        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(InstallPath("demo"), SkillPackageInstaller.ManifestFileName)));
        Assert.Equal("acme", manifest.RootElement.GetProperty("Owner").GetString());
        Assert.Equal("repo", manifest.RootElement.GetProperty("Repository").GetString());
        Assert.Equal(Commit, manifest.RootElement.GetProperty("Commit").GetString());
    }

    [Fact]
    public async Task Existing_target_requires_explicit_update_and_unknown_folder_is_preserved()
    {
        var target = Directory.CreateDirectory(InstallPath("demo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "user.txt"), "keep me");
        using var http = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\n---\nBody."))));
        using var installer = new SkillPackageInstaller(_root, http);

        var error = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            installer.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));

        Assert.Equal("skill_target_exists", error.Code);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(target, "user.txt")));
    }

    [Fact]
    public async Task Update_replaces_only_an_unchanged_managed_package_and_pins_new_commit()
    {
        using var firstHttp = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\n---\nOld."))));
        using (var first = new SkillPackageInstaller(_root, firstHttp))
            await first.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install);
        using var nextHttp = new HttpClient(new ArchiveHandler(ZipForCommit(NextCommit, ("SKILL.md", "---\nname: Demo\n---\nNew."))));
        using var next = new SkillPackageInstaller(_root, nextHttp);

        var receipt = await next.InstallAsync(SourceUrl(NextCommit), SkillPackageInstallAction.Update);

        Assert.Equal(NextCommit, receipt.Commit);
        Assert.Equal("New.", (await File.ReadAllTextAsync(Path.Combine(InstallPath("demo"), "SKILL.md"))).Split("---").Last().Trim());
        Assert.Empty(Directory.EnumerateDirectories(_root, ".ru-skill-install-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Update_refuses_modified_managed_content_without_changing_it()
    {
        using var firstHttp = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\n---\nOld."))));
        using (var first = new SkillPackageInstaller(_root, firstHttp))
            await first.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install);
        await File.WriteAllTextAsync(Path.Combine(InstallPath("demo"), "SKILL.md"), "User edit.");
        using var nextHttp = new HttpClient(new ArchiveHandler(ZipForCommit(NextCommit, ("SKILL.md", "---\nname: Demo\n---\nNew."))));
        using var next = new SkillPackageInstaller(_root, nextHttp);

        var error = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            next.InstallAsync(SourceUrl(NextCommit), SkillPackageInstallAction.Update));

        Assert.Equal("skill_target_modified", error.Code);
        Assert.Equal("User edit.", await File.ReadAllTextAsync(Path.Combine(InstallPath("demo"), "SKILL.md")));
    }

    [Theory]
    [InlineData("https://github.com/acme/repo/tree/main/skills/demo")]
    [InlineData("https://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/../demo")]
    [InlineData("https://github.com.evil/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo")]
    [InlineData("http://github.com/acme/repo/tree/0123456789abcdef0123456789abcdef01234567/skills/demo")]
    public async Task Unpinned_or_ambiguous_github_sources_are_rejected_before_network(string source)
    {
        var handler = new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\n---\nBody.")));
        using var http = new HttpClient(handler);
        using var installer = new SkillPackageInstaller(_root, http);

        var error = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            installer.InstallAsync(source, SkillPackageInstallAction.Install));

        Assert.Equal("invalid_skill_source", error.Code);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task Redirect_and_invalid_package_archive_are_named_failures()
    {
        using var redirectHttp = new HttpClient(new ArchiveHandler([], HttpStatusCode.Redirect));
        using var redirectInstaller = new SkillPackageInstaller(_root, redirectHttp);
        var redirect = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            redirectInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));
        Assert.Equal("skill_source_redirect", redirect.Code);

        using var invalidHttp = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "no frontmatter"), ("../escape", "bad"))));
        using var invalidInstaller = new SkillPackageInstaller(_root, invalidHttp);
        var invalid = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            invalidInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));
        Assert.Equal("skill_archive_invalid", invalid.Code);
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
    }

    [Fact]
    public async Task Invalid_frontmatter_and_symlink_entries_are_refused()
    {
        using var malformedHttp = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "---\nname: [\n---\nBody."))));
        using var malformedInstaller = new SkillPackageInstaller(_root, malformedHttp);
        var malformed = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            malformedInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));
        Assert.Equal("skill_manifest_invalid", malformed.Code);
        Assert.False(Directory.Exists(InstallPath("demo")));

        using var symlinkHttp = new HttpClient(new ArchiveHandler(ZipWithSymlink()));
        using var symlinkInstaller = new SkillPackageInstaller(_root, symlinkHttp);
        var symlink = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            symlinkInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));
        Assert.Equal("skill_archive_invalid", symlink.Code);
        Assert.False(Directory.Exists(InstallPath("demo")));
    }

    [Fact]
    public async Task Cancellation_and_download_size_limit_leave_no_install_target()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var cancelHttp = new HttpClient(new ArchiveHandler(Zip(("SKILL.md", "---\nname: Demo\n---\nBody."))));
        using var cancelInstaller = new SkillPackageInstaller(_root, cancelHttp);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install, cancelled.Token));

        using var largeHttp = new HttpClient(new ArchiveHandler(new byte[SkillPackageInstaller.MaxArchiveBytes + 1]));
        using var largeInstaller = new SkillPackageInstaller(_root, largeHttp);
        var error = await Assert.ThrowsAsync<SkillPackageInstallException>(() =>
            largeInstaller.InstallAsync(SourceUrl(), SkillPackageInstallAction.Install));
        Assert.Equal("skill_archive_too_large", error.Code);
        Assert.False(Directory.Exists(InstallPath("demo")));
    }

    private const string NextCommit = "fedcba9876543210fedcba9876543210fedcba98";
    private string SourceUrl(string commit = Commit) => $"https://github.com/acme/repo/tree/{commit}/skills/demo";
    private string InstallPath(string id) => Path.Combine(_root, id);

    private static byte[] Zip(params (string Path, string Content)[] files) => ZipForCommit(Commit, files);

    private static byte[] ZipForCommit(string commit, params (string Path, string Content)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var root = $"repo-{commit}/skills/demo/";
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(root + path, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private static byte[] ZipWithSymlink()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var skill = archive.CreateEntry($"repo-{Commit}/skills/demo/SKILL.md");
            using (var writer = new StreamWriter(skill.Open(), new UTF8Encoding(false)))
                writer.Write("---\nname: Demo\n---\nBody.");
            var link = archive.CreateEntry($"repo-{Commit}/skills/demo/link");
            link.ExternalAttributes = unchecked((int)0xA0000000) | 0x1FF;
            using (var writer = new StreamWriter(link.Open(), new UTF8Encoding(false))) writer.Write("SKILL.md");
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ArchiveHandler(byte[] bytes, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK) response.Content = new ByteArrayContent(bytes);
            return Task.FromResult(response);
        }
    }
}
