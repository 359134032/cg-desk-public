using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CgDesk.Services;

internal static class LauncherInstall
{
    private readonly record struct GameEntry(string Code, string Exe);

    public static bool TryLocate(string? launcherPath, out string directory, out string exePath)
    {
        directory = "";
        exePath = "";
        try
        {
            return TryLocateCore(launcherPath, out directory, out exePath);
        }
        catch (Exception)
        {
            directory = "";
            exePath = "";
            return false;
        }
    }

    private static bool TryLocateCore(string? launcherPath, out string directory, out string exePath)
    {
        directory = "";
        exePath = "";
        var launcherDir = ResolveLauncherDirectory(launcherPath);
        if (launcherDir == null)
        {
            return false;
        }

        var ini = Path.Combine(launcherDir, "PlayOnline.ini");
        var paths = ReadPaths(ini);
        if (paths.Count == 0)
        {
            return false;
        }

        var games = ReadGames(Path.Combine(launcherDir, "XML", "GameList.xml"));
        var code = SelectedCode(ini, games);
        string? chosen = null;
        if (code.Length > 0 && paths.TryGetValue(code, out var selected) && IsClientDirectory(selected))
        {
            chosen = selected;
        }

        chosen ??= paths.Values.FirstOrDefault(IsClientDirectory);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return false;
        }

        directory = chosen;
        exePath = ResolveExe(chosen, code, games);
        return true;
    }

    private static string? ResolveLauncherDirectory(string? launcherPath)
    {
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            if (File.Exists(launcherPath))
            {
                return Path.GetDirectoryName(launcherPath);
            }

            if (File.Exists(Path.Combine(launcherPath, "PlayOnline.ini")))
            {
                return launcherPath;
            }

            var directory = Path.GetDirectoryName(launcherPath);
            if (!string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, "PlayOnline.ini")))
            {
                return directory;
            }
        }

        foreach (var process in Process.GetProcessesByName("POLCN_Launcher"))
        {
            try
            {
                if (NativeMethods.TryImagePath(process.Id, out var image))
                {
                    return Path.GetDirectoryName(image);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string SelectedCode(string ini, IReadOnlyList<GameEntry> games)
    {
        var settings = ReadSection(ini, "Settings");
        if (!settings.TryGetValue("Game", out var text) || !int.TryParse(text, out var index))
        {
            return "";
        }

        return index >= 0 && index < games.Count ? games[index].Code : "";
    }

    private static string ResolveExe(string directory, string code, IReadOnlyList<GameEntry> games)
    {
        var named = games.FirstOrDefault(game => game.Code == code).Exe;
        if (!string.IsNullOrWhiteSpace(named))
        {
            var full = Path.Combine(directory, named);
            if (File.Exists(full))
            {
                return full;
            }
        }

        foreach (var name in new[] { "cg_se_3000.exe", "cg_se_6000.exe", "cg_item_6000.exe", "cg.exe" })
        {
            var full = Path.Combine(directory, name);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return "";
    }

    private static bool IsClientDirectory(string? directory)
    {
        return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "Config.dat"));
    }

    private static Dictionary<string, string> ReadPaths(string ini)
    {
        var section = ReadSection(ini, "Path");
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in section)
        {
            if (!key.StartsWith("Game", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            {
                continue;
            }

            paths[key["Game".Length..]] = value;
        }

        return paths;
    }

    private static Dictionary<string, string> ReadSection(string ini, string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ini))
        {
            return values;
        }

        var current = "";
        foreach (var raw in ReadLauncherText(ini).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            values[line[..split].Trim()] = line[(split + 1)..].Trim().Trim('"');
        }

        return values;
    }

    private static List<GameEntry> ReadGames(string xml)
    {
        var games = new List<GameEntry>();
        if (!File.Exists(xml))
        {
            return games;
        }

        var text = ReadLauncherText(xml);
        foreach (Match game in Regex.Matches(text, @"<Game\b[^>]*\bCode\s*=\s*""([^""]*)""[^>]*>(.*?)</Game>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var code = game.Groups[1].Value.Trim();
            var exe = Regex.Match(game.Groups[2].Value, @"<EXE>\s*([^<]+?)\s*</EXE>", RegexOptions.IgnoreCase);
            if (code.Length > 0)
            {
                games.Add(new GameEntry(code, exe.Success ? exe.Groups[1].Value.Trim() : ""));
            }
        }

        return games;
    }

    private static string ReadLauncherText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) || bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return File.ReadAllText(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            // The Chinese official launcher also writes legacy GBK configuration files.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }
}
