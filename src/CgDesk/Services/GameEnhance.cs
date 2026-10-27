using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CgDesk.Services;

internal static class GameEnhance
{
    const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CgDesk", "enhance");

    public static string Apply(string? gameDirectory, bool enabled, int width, int height, string? gameExe)
    {
        if (!string.IsNullOrWhiteSpace(gameExe))
        {
            ClearLaunch(gameExe);
        }

        var leftover = gameDirectory == null ? null : CleanupGame(gameDirectory);
        if (!enabled)
        {
            Disarm();
            return leftover ?? "主窗口画质增强已关闭。";
        }

        if (!Prepare(out var problem))
        {
            return problem;
        }

        Arm(width, height);
        return leftover ?? $"画质增强开着。游戏按 {width}×{height} 绘制，画面会铺满主区域。";
    }

    public static void UpdateResolution(int width, int height)
    {
        if (!File.Exists(Path.Combine(Folder, "enhance.on")))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(Folder, "dgv"));
            WriteResolution(width, height);
        }
        catch (IOException)
        {
        }
    }

    public static bool TryArmLaunch(string gameExe, int width, int height, out string message)
    {
        message = "";
        var exeName = Path.GetFileName(gameExe);
        if (string.IsNullOrWhiteSpace(exeName))
        {
            message = "还没找到游戏程序，增强先不加载。";
            return false;
        }

        if (!Prepare(out message))
        {
            return false;
        }

        Arm(width, height);
        var once = Path.Combine(Folder, "enhance.once");
        File.WriteAllText(once, "1");
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(Ifeo + "\\" + exeName);
            key?.SetValue("Debugger", "\"" + Path.Combine(Folder, "enhance-inject.exe") + "\"");
        }
        catch (SystemException)
        {
            File.Delete(once);
            message = "没能挂上启动挂钩。请用管理员身份打开窗口台。";
            return false;
        }

        message = "";
        return true;
    }

    public static void ClearLaunch(string? gameExe)
    {
        var once = Path.Combine(Folder, "enhance.once");
        if (File.Exists(once))
        {
            File.Delete(once);
        }

        var exeName = Path.GetFileName(gameExe ?? "");
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Ifeo + "\\" + exeName, true);
            var value = key?.GetValue("Debugger") as string;
            if (value != null && value.Contains("enhance-inject.exe", StringComparison.OrdinalIgnoreCase))
            {
                key!.DeleteValue("Debugger", false);
            }
        }
        catch (SystemException)
        {
        }
    }

    private static void Arm(int width, int height)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "enhance.on"), "1");
        WriteResolution(width, height);
    }

    private static void Disarm()
    {
        var flag = Path.Combine(Folder, "enhance.on");
        if (File.Exists(flag))
        {
            File.Delete(flag);
        }
    }

    private static bool Prepare(out string problem)
    {
        problem = "";
        Directory.CreateDirectory(Path.Combine(Folder, "dgv"));
        var proxy = FindAppFile("ddraw-proxy.dll");
        var injector = FindAppFile("enhance-inject.exe");
        if (proxy == null || injector == null)
        {
            problem = "窗口台里缺少画质入口。";
            return false;
        }

        File.Copy(proxy, Path.Combine(Folder, "ddraw.dll"), true);
        File.Copy(injector, Path.Combine(Folder, "enhance-inject.exe"), true);
        CopyBundledWrapper();
        CopySystemDdraw();
        if (!File.Exists(Path.Combine(Folder, "dgv", "ddraw_dgv.dll")))
        {
            problem = "窗口台的数据目录里还没有画质组件，暂时不能打开。";
            return false;
        }

        return true;
    }

    private static string? CleanupGame(string gameDirectory)
    {
        var root = Path.Combine(gameDirectory, "ddraw.dll");
        if (File.Exists(root))
        {
            if (!IsOurs(root))
            {
                return "游戏目录里已有别的 ddraw.dll，窗口台没有改它。";
            }

            try
            {
                File.Delete(root);
            }
            catch (IOException)
            {
                return "游戏目录里还有上次留下的 ddraw。先关掉客户端，窗口台才能把它拿掉。";
            }
        }

        var extra = Path.Combine(gameDirectory, "cgdesk");
        if (Directory.Exists(extra) && File.Exists(Path.Combine(extra, "dgv", "ddraw_dgv.dll")))
        {
            try
            {
                Directory.Delete(extra, true);
            }
            catch (IOException)
            {
                return "游戏目录里还有上次留下的增强文件。先关掉客户端再清理。";
            }
        }

        return null;
    }

    private static void WriteResolution(int width, int height)
    {
        var resolution = $"h:{Math.Max(2, width) & ~1}, v:{Math.Max(2, height) & ~1}";
        var conf = Path.Combine(Folder, "dgv", "dgVoodoo.conf");
        if (!File.Exists(conf))
        {
            File.WriteAllText(conf, DefaultConf(resolution));
            return;
        }

        var text = File.ReadAllText(conf);
        text = ReplaceKey(text, "ScalingMode", "centered_ar");
        text = ReplaceKey(text, "Bilinear2DOperations", "false");
        var section = text.IndexOf("[DirectX]", StringComparison.Ordinal);
        if (section < 0)
        {
            File.WriteAllText(conf, DefaultConf(resolution));
            return;
        }

        var next = text.IndexOf("\n[", section + 1, StringComparison.Ordinal);
        if (next < 0)
        {
            next = text.Length;
        }

        var body = text[section..next];
        var line = "Resolution                          = " + resolution;
        var matches = Regex.Matches(body, @"^Resolution\s*=.*(?:\r?\n)?", RegexOptions.Multiline);
        string updated;
        if (matches.Count == 0)
        {
            updated = body.TrimEnd() + Environment.NewLine + line + Environment.NewLine;
        }
        else
        {
            for (var i = matches.Count - 1; i >= 1; i--)
            {
                body = body.Remove(matches[i].Index, matches[i].Length);
            }

            updated = Regex.Replace(body, @"^Resolution\s*=.*$", line, RegexOptions.Multiline);
        }

        File.WriteAllText(conf, text[..section] + updated + text[next..]);
    }

    private static string ReplaceKey(string text, string key, string value)
    {
        return Regex.Replace(text, "^" + key + @"\s*=.*$", key + "                          = " + value, RegexOptions.Multiline);
    }

    private static string DefaultConf(string resolution) =>
        "Version = 0x287\r\n" +
        "[General]\r\n" +
        "OutputAPI = d3d11_fl11_0\r\n" +
        "FullScreenMode = false\r\n" +
        "ScalingMode = centered_ar\r\n" +
        "CaptureMouse = false\r\n" +
        "[DirectX]\r\n" +
        "Resolution = " + resolution + "\r\n" +
        "AppControlledScreenMode = true\r\n" +
        "DisableAltEnterToToggleScreenMode = true\r\n" +
        "dgVoodooWatermark = false\r\n";

    private static void CopyBundledWrapper()
    {
        var source = FindAppFile(Path.Combine("dgv", "ddraw_dgv.dll"));
        var target = Path.Combine(Folder, "dgv", "ddraw_dgv.dll");
        if (source == null || File.Exists(target))
        {
            return;
        }

        File.Copy(source, target);
    }

    private static void CopySystemDdraw()
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "ddraw.dll");
        var target = Path.Combine(Folder, "ddraw_sys.dll");
        if (!File.Exists(source))
        {
            return;
        }

        if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
        {
            return;
        }

        File.Copy(source, target, true);
    }

    private static string? FindAppFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private static bool IsOurs(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Encoding.ASCII.GetString(bytes).Contains("CgDeskDDrawProxy", StringComparison.Ordinal);
    }
}
