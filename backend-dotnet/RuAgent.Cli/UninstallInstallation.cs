using Microsoft.Win32;
using RuAgent.Updates;

namespace RuAgent.Cli;

internal static class UninstallInstallation
{
    public static string Resolve(string directory)
    {
        using var registration = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OniAntou.Ru.CLI_is1");
        return Resolve(directory, registration?.GetValue("InstallLocation") as string,
            registration?.GetValue("UninstallString") as string);
    }

    internal static string Resolve(string directory, string? registeredDirectory, string? uninstallCommand)
    {
        var installed = InstalledRelease.Load(directory);
        if (installed?.Product != "cli")
            throw new UpdateException("uninstall_installation_unknown: this command only removes an installed CLI package; source builds and Desktop are kept");
        if (string.IsNullOrWhiteSpace(registeredDirectory) || string.IsNullOrWhiteSpace(uninstallCommand))
            throw new UpdateException("uninstall_not_registered: portable packages are removed by deleting their extracted folder; Ru profile data is kept separately");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Equals(Path.GetPathRoot(Path.GetFullPath(directory))?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("uninstall_installation_invalid");
        if (!Path.GetFullPath(registeredDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("uninstall_installation_mismatch");
        var command = uninstallCommand.Trim();
        if (command.StartsWith('"') && command.EndsWith('"')) command = command[1..^1];
        var executable = Path.GetFullPath(command);
        var name = Path.GetFileName(executable);
        if (!string.Equals(Path.GetDirectoryName(executable), root, StringComparison.OrdinalIgnoreCase)
            || name.Length != 12 || !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !name.AsSpan(5, 3).ToString().All(char.IsAsciiDigit)
            || !File.Exists(executable) || !File.Exists(Path.ChangeExtension(executable, ".dat")))
            throw new UpdateException("uninstall_registration_invalid");
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UpdateException("uninstall_installation_invalid");
        if (File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint))
            throw new UpdateException("uninstall_installation_invalid");
        return executable;
    }
}
