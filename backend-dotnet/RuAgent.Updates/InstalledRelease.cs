using System.Reflection;
using System.Text.Json;

namespace RuAgent.Updates;

public enum UpdateChannel { Stable, Preview }
public enum UpdateProduct { Desktop, Cli }

public sealed record InstalledRelease(string Product, string Version, string Architecture, int UpdateProtocol = 1)
{
    public const string MarkerName = "ru-install.json";
    public static string SoftwareVersion => typeof(InstalledRelease).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public UpdateProduct ProductKind => Product == "desktop" ? UpdateProduct.Desktop : UpdateProduct.Cli;

    public static InstalledRelease? Load(string directory)
    {
        var path = Path.Combine(directory, MarkerName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) throw new UpdateException("update_installation_invalid");
        InstalledRelease? marker;
        try { marker = JsonSerializer.Deserialize<InstalledRelease>(File.ReadAllText(path)); }
        catch (JsonException) { throw new UpdateException("update_installation_invalid"); }
        if (marker is null || marker.Product is not ("desktop" or "cli") || marker.Architecture != "win-x64"
            || marker.UpdateProtocol != 1 || ReleaseVersion.Parse(marker.Version) is null)
            throw new UpdateException("update_installation_invalid");
        return marker;
    }
}

public sealed class UpdateException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Local maintenance preference, separate from provider settings and credentials.</summary>
public sealed class UpdatePreferences(string? root = null)
{
    public string DirectoryPath { get; } = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ru", "updates");
    public UpdateChannel LoadChannel()
    {
        var path = Path.Combine(DirectoryPath, "channel.txt");
        if (!File.Exists(path)) return UpdateChannel.Stable;
        if (new FileInfo(path).Length > 32) return UpdateChannel.Stable;
        return File.ReadAllText(path).Trim() == "preview" ? UpdateChannel.Preview : UpdateChannel.Stable;
    }
    public string? LoadLastResult()
    {
        var path = Path.Combine(DirectoryPath, "last-update.json");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 4096) return "Previous update status is unreadable.";
            using var document = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
            var result = document.RootElement;
            if (result.GetProperty("status").GetString() == "installed")
            {
                var version = ReleaseVersion.Parse(result.GetProperty("version").GetString());
                return version is null ? "Previous update status is unreadable." : $"Last update installed Ru {version}.";
            }
            // Persisted error text never carries executable output into the UI.
            return "The previous update failed. Close other Ru windows and try again; the update log has details.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return "Previous update status is unreadable."; }
    }
    public void SaveChannel(UpdateChannel channel)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "channel.txt");
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, channel == UpdateChannel.Preview ? "preview" : "stable");
        File.Move(temporary, path, overwrite: true);
    }
}
