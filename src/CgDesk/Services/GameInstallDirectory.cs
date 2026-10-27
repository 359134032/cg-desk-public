using System.IO;

namespace CgDesk.Services;

internal static class GameInstallDirectory
{
    public static string? FindDirectory(string? gamePath, string? launcherPath)
    {
        if (TryDirectory(Path.GetDirectoryName(gamePath), out var fromGame)) return fromGame;
        if (LauncherInstall.TryLocate(launcherPath, out var associated, out _)) return associated;

        var launcherDir = Path.GetDirectoryName(launcherPath);
        if (!string.IsNullOrEmpty(launcherDir))
        {
            var parent = Directory.GetParent(launcherDir)?.FullName;
            if (TryDirectory(string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "魔力宝贝"), out var fromLauncher))
                return fromLauncher;
        }
        return null;
    }

    private static bool TryDirectory(string? directory, out string found)
    {
        found = directory ?? "";
        return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "Config.dat"));
    }
}
