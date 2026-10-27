using System.Diagnostics;
using System.IO;
using System.Text;

namespace CgDesk.Services;

internal sealed record GameWindow(IntPtr Handle, int ProcessId, string ProcessName, string Title);

internal static class GameWindowScanner
{
    public static IReadOnlyList<GameWindow> Scan(IEnumerable<string> processNames, int ignoreProcessId, IEnumerable<IntPtr>? selectedWindows = null)
    {
        var wanted = new HashSet<string>(
            processNames.Select(name => name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(name.Trim()) : Path.GetFileName(name.Trim())),
            StringComparer.OrdinalIgnoreCase);
        wanted.Remove("");

        var found = new List<GameWindow>();
        NativeMethods.EnumWindows((IntPtr hwnd, IntPtr lParam) =>
        {
            TryAdd(found, hwnd, wanted, ignoreProcessId, true);
            return true;
        }, IntPtr.Zero);

        foreach (var handle in GameEmbedder.HeldHandles.Concat(selectedWindows ?? []).Distinct())
        {
            TryAdd(found, handle, wanted, ignoreProcessId, false, true);
        }

        return found;
    }

    public static IReadOnlyList<GameWindow> Candidates(int ignoreProcessId)
    {
        var found = new List<GameWindow>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            TryAdd(found, hwnd, [], ignoreProcessId, true, true);
            return true;
        }, IntPtr.Zero);
        return found.Where(window => !string.IsNullOrWhiteSpace(NativeMethods.ReadTitle(window.Handle)))
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void TryAdd(List<GameWindow> found, IntPtr hwnd, HashSet<string> wanted, int ignoreProcessId, bool topLevel, bool selected = false)
    {
        if (found.Any(item => item.Handle == hwnd) || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        if (topLevel && !NativeMethods.IsWindowVisible(hwnd))
        {
            return;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pidValue);
        var pid = (int)pidValue;
        if (pid == 0 || pid == ignoreProcessId)
        {
            return;
        }

        if (!TryIdentify(pid, out var processName, out var imagePath))
        {
            return;
        }

        var knownName = wanted.Contains(processName);
        if (!selected && !knownName && !IsGameClient(imagePath))
        {
            return;
        }

        var width = 0;
        var height = 0;
        if (topLevel)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            if (!NativeMethods.IsIconic(hwnd) && (width < 80 || height < 60))
            {
                return;
            }

            if (!knownName && !selected && (width < 480 || height < 360))
            {
                return;
            }
        }

        var title = NativeMethods.ReadTitle(hwnd).Trim();
        if (title.Length == 0)
        {
            title = "游戏窗口";
        }

        found.Add(new GameWindow(hwnd, pid, processName, title));
    }

    private static bool TryIdentify(int pid, out string processName, out string imagePath)
    {
        processName = "";
        imagePath = "";
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var capacity = 1024;
                var buffer = new StringBuilder(capacity);
                if (NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref capacity) && buffer.Length > 0)
                {
                    imagePath = buffer.ToString();
                    processName = Path.GetFileNameWithoutExtension(imagePath);
                }
            }
            finally
            {
                _ = NativeMethods.CloseHandle(process);
            }
        }

        if (processName.Length > 0)
        {
            return true;
        }

        try
        {
            using var current = Process.GetProcessById(pid);
            processName = current.ProcessName;
            return processName.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameClient(string imagePath)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath);
        if (name.Equals("Config", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CG5Reg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("POLCN_Launcher", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Patcher", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var directory = Path.GetDirectoryName(imagePath);
        return !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "Config.dat"));
    }
}
