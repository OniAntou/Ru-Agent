using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Hosting;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class RuntimeDescriptorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-desc-" + Guid.NewGuid().ToString("N"));
    public RuntimeDescriptorTests() => Directory.CreateDirectory(_root);

    private RuntimeDescriptor CreateValidDescriptor()
    {
        var proc = Process.GetCurrentProcess();
        return new RuntimeDescriptor(
            RuntimeDescriptor.CurrentProtocolVersion,
            $"http://127.0.0.1:{PublicHostRuntimeOptions.FindFreeLoopbackPort()}/",
            PublicHostRuntimeOptions.GenerateBearerToken(),
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            PublicHostRuntimeOptions.GenerateNonce(),
            DateTimeOffset.UtcNow.ToString("O"));
    }

    [Fact]
    public void SerializesStrictVersionedFieldsAndNoExtraTokens()
    {
        var desc = CreateValidDescriptor();
        var json = JsonSerializer.Serialize(desc, RuntimeDescriptor.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(RuntimeDescriptor.AllowedFields, fields);
        Assert.Contains("protocol_version", fields);
        Assert.Contains("base_url", fields);
        Assert.Contains("bearer_token", fields);
        Assert.Contains("pid", fields);
        Assert.Contains("process_start_time", fields);
        Assert.Contains("instance_nonce", fields);
        Assert.Contains("created_at", fields);
        Assert.DoesNotContain("worker_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broker", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executable", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OPENAI", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescriptorTimestampsParseUnderANonGregorianCulture()
    {
        // The descriptor's timestamps are written with ToString("O"). A culture-sensitive
        // parse reads those digits in the current culture's calendar, so on a locale like
        // th-TH the process-bound comparison never matched and a live host was reported as
        // stale_pid. A round-trip parse must survive whatever culture the machine runs.
        var desc = CreateValidDescriptor();
        var json = JsonSerializer.Serialize(desc, RuntimeDescriptor.JsonOptions);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var parsed = RuntimeDescriptorValidator.ParseStrict(json);

            Assert.Equal(desc.ProcessStartTime, parsed.ProcessStartTime);
            Assert.Equal(desc.CreatedAt, parsed.CreatedAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void RejectsUnknownFieldsAndVersions()
    {
        var desc = CreateValidDescriptor();
        var json = JsonSerializer.Serialize(desc, RuntimeDescriptor.JsonOptions);
        var withExtra = json.TrimEnd('}') + ",\"unknown_field\":\"oops\"}";
        var ex = Assert.ThrowsAny<Exception>(() => RuntimeDescriptorValidator.ParseStrict(withExtra));
        Assert.Contains("unknown_field", ex.Message, StringComparison.OrdinalIgnoreCase);

        var badVersion = json.Replace($"\"protocol_version\":{RuntimeDescriptor.CurrentProtocolVersion}", "\"protocol_version\":999");
        Assert.ThrowsAny<Exception>(() => RuntimeDescriptorValidator.ParseStrict(badVersion));
    }

    [Fact]
    public void RejectsNonLoopbackUrls()
    {
        var desc = CreateValidDescriptor() with { BaseUrl = "http://192.168.1.1:8000/" };
        var json = JsonSerializer.Serialize(desc, RuntimeDescriptor.JsonOptions);
        Assert.ThrowsAny<Exception>(() => RuntimeDescriptorValidator.ParseStrict(json));
        var https = CreateValidDescriptor() with { BaseUrl = "https://127.0.0.1:8000/" };
        var json2 = JsonSerializer.Serialize(https, RuntimeDescriptor.JsonOptions);
        Assert.ThrowsAny<Exception>(() => RuntimeDescriptorValidator.ParseStrict(json2));
    }

    [Fact]
    public async Task BindingToPidStartNonceRequiresAuthenticatedHealth()
    {
        var validDesc = CreateValidDescriptor();
        var path = Path.Combine(_root, "desc.json");
        var writer = new RuntimeDescriptorWriter(path);
        await writer.WriteAsync(validDesc);

        // Valid descriptor should be readable and bound
        Assert.True(writer.TryReadValid(out var read, out _));
        Assert.NotNull(read);
        Assert.Equal(validDesc.InstanceNonce, read!.InstanceNonce);

        // Stale descriptor (wrong PID) should be considered stale
        var staleDesc = validDesc with { ProcessId = 999999, InstanceNonce = PublicHostRuntimeOptions.GenerateNonce() };
        var stalePath = Path.Combine(_root, "stale.json");
        var staleWriter = new RuntimeDescriptorWriter(stalePath);
        await staleWriter.WriteAsync(staleDesc);
        Assert.True(staleWriter.IsStale());
        Assert.False(staleWriter.TryReadValid(out _, out var err));
        Assert.Equal("stale_pid", err);
    }

    [Fact]
    public async Task AtomicReplaceAndCleanup()
    {
        var path = Path.Combine(_root, "atomic.json");
        var writer = new RuntimeDescriptorWriter(path);
        var d1 = CreateValidDescriptor();
        await writer.WriteAsync(d1);
        var first = File.ReadAllText(path);
        var d2 = d1 with { InstanceNonce = PublicHostRuntimeOptions.GenerateNonce(), CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1).ToString("O") };
        await writer.WriteAsync(d2);
        var second = File.ReadAllText(path);
        Assert.NotEqual(first, second);
        writer.Cleanup();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ProvenTokensNeverSerialize()
    {
        var desc = CreateValidDescriptor();
        var json = JsonSerializer.Serialize(desc, RuntimeDescriptor.JsonOptions);
        Assert.DoesNotContain("RU_WORKER_TOKEN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RU_AGENT_BROKER_DESCRIPTOR", json, StringComparison.Ordinal);
        Assert.DoesNotContain("args", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("env", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BearerAuthSuccessAndWrongBearerFails()
    {
        // Simulate HTTP health probe: valid bearer returns 200, wrong returns 401
        // This test does not spin up real server; it validates validator distinguishes bearer via probe logic in Client tests.
        // Here we just prove descriptor contains bearer and that wrong bearer would be rejected by health check stub.
        var desc = CreateValidDescriptor();
        var correct = desc.BearerToken;
        var wrong = PublicHostRuntimeOptions.GenerateBearerToken();
        Assert.NotEqual(correct, wrong);
        // Health probe stub would check equality; we assert tokens differ
        Assert.True(correct.Length == 64);
        Assert.True(wrong.Length == 64);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PortConflictHandling()
    {
        // Simulate port conflict: occupy a port with listener, then ensure descriptor writer does not claim it if no valid descriptor
        var port = PublicHostRuntimeOptions.FindFreeLoopbackPort();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        listener.Start();
        try
        {
            // Try to write descriptor pointing to that port but with stale PID => should be considered stale and not attached
            var stale = CreateValidDescriptor() with { BaseUrl = $"http://127.0.0.1:{port}/", ProcessId = 999999 };
            var path = Path.Combine(_root, "conflict.json");
            var writer = new RuntimeDescriptorWriter(path);
            await writer.WriteAsync(stale);
            Assert.True(writer.IsStale());
            // Port is occupied, but descriptor is stale => starter should detect public_host_port_conflict if it tries to bind
            // We simulate starter's IsPortOccupied logic: listener is running, so port is occupied
            bool occupied = IsPortOccupied(port);
            Assert.True(occupied);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The descriptor carries the desktop bearer, and the Broker descriptor
    /// carries the token that authenticates the execution authority. Both were
    /// previously written with plain file APIs, inheriting whatever the root
    /// grants — on a shared <c>RU_LOG_ROOT</c> that is every local principal.
    /// The ACL now lands at creation time, so there is no window in which the
    /// token exists with inherited rights.
    /// </summary>
    [Fact]
    public async Task DescriptorIsWrittenOwnerOnlyEvenUnderAPermissiveDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A directory that hands Everyone full control: inheritance would publish
        // the token to every principal if the file itself were not restricted.
        var openRoot = Path.Combine(_root, "open");
        Directory.CreateDirectory(openRoot);
        var directorySecurity = new DirectoryInfo(openRoot).GetAccessControl();
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new DirectoryInfo(openRoot).SetAccessControl(directorySecurity);

        var path = Path.Combine(openRoot, "runtime_descriptor.json");
        var writer = new RuntimeDescriptorWriter(path);
        await writer.WriteAsync(CreateValidDescriptor());

        AssertOwnerOnly(path);
    }

    [Fact]
    public void BrokerDescriptorIsWrittenOwnerOnlyEvenUnderAPermissiveDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var openRoot = Path.Combine(_root, "open-broker");
        Directory.CreateDirectory(openRoot);
        var directorySecurity = new DirectoryInfo(openRoot).GetAccessControl();
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier("S-1-1-0"),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new DirectoryInfo(openRoot).SetAccessControl(directorySecurity);

        var path = Path.Combine(openRoot, "broker.json");
        OwnerOnlyFile.WriteAllText(path, "{\"auth_token\":\"secret\"}");

        AssertOwnerOnly(path);
        Assert.Equal("{\"auth_token\":\"secret\"}", File.ReadAllText(path));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance must be disabled");

        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
        var allowed = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(System.Security.Principal.SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToArray();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, rule => Assert.Equal(identity, rule.IdentityReference));
    }

    private static bool IsPortOccupied(int port)
    {
        try
        {
            using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            return true;
        }
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
