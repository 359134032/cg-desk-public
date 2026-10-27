using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CgDesk.Services;

internal sealed class GameHost : HwndHost
{
    private const int ParentNotify = 0x0210;
    private const int LeftButtonDown = 0x0201;
    private const int WndProcIndex = -4;

    private IntPtr _handle;
    private IntPtr _oldProc;
    private HostWndProc? _proc;

    public IntPtr HostHandle => _handle;

    public IntPtr KeyTarget { get; set; }

    public event Action? Clicked;

    protected override bool TranslateAcceleratorCore(ref MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (msg.wParam == new IntPtr(0x73) && (modifiers & System.Windows.Input.ModifierKeys.Alt) != 0)
            return base.TranslateAcceleratorCore(ref msg, modifiers);
        if (ForwardKeyboard(msg)) return true;
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override bool TranslateCharCore(ref MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (ForwardKeyboard(msg)) return true;
        return base.TranslateCharCore(ref msg, modifiers);
    }

    private bool ForwardKeyboard(MSG msg)
    {
        if (msg.message is not (0x0100 or 0x0101 or 0x0102 or 0x0104 or 0x0105 or 0x0106)
            || KeyTarget == IntPtr.Zero || !NativeMethods.IsWindow(KeyTarget)) return false;
        _ = PostMessage(KeyTarget, (uint)msg.message, msg.wParam, msg.lParam);
        return true;
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _ = SetThreadDpiHostingBehavior(1);
        EnsureHostClass();
        _handle = CreateWindowExW(
            0,
            HostClass,
            "",
            0x52000000,
            0,
            0,
            0,
            0,
            parent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        _proc = OnHostMessage;
        _oldProc = SetWindowLongPtr(_handle, WndProcIndex, Marshal.GetFunctionPointerForDelegate(_proc));
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            if (_oldProc != IntPtr.Zero)
            {
                _ = SetWindowLongPtr(hwnd.Handle, WndProcIndex, _oldProc);
                _oldProc = IntPtr.Zero;
            }

            _ = DestroyWindow(hwnd.Handle);
        }

        _handle = IntPtr.Zero;
    }

    private IntPtr OnHostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0014 && _darkBrush != IntPtr.Zero)
        {
            GetClientRect(hwnd, out var rect);
            _ = FillRect(wParam, ref rect, _darkBrush);
            return new IntPtr(1);
        }

        if (msg == ParentNotify && (wParam.ToInt32() & 0xFFFF) == LeftButtonDown)
        {
            Clicked?.Invoke();
        }

        if (msg is 0x0100 or 0x0101 or 0x0102 or 0x0104 or 0x0105 or 0x0106)
        {
            var child = KeyTarget;
            if (child == IntPtr.Zero || !NativeMethods.IsWindow(child))
            {
                child = GetWindow(hwnd, 5);
            }

            if (child != IntPtr.Zero)
            {
                _ = PostMessage(child, msg, wParam, lParam);
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_oldProc, hwnd, msg, wParam, lParam);
    }

    private const string HostClass = "CgDeskHost";
    private static IntPtr _darkBrush;
    private static bool _classReady;

    private static void EnsureHostClass()
    {
        if (_classReady)
        {
            return;
        }

        _darkBrush = CreateSolidBrush(0x000E1011);
        var wndClass = new WndClass
        {
            WndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            Style = 0x0008,
            Background = _darkBrush,
            ClassName = HostClass
        };
        _classReady = RegisterClass(ref wndClass) != 0 || Marshal.GetLastWin32Error() == 1410;
    }

    private delegate IntPtr HostWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? Menu;
        public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass wndClass);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref RectNative rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetThreadDpiHostingBehavior(int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

internal static class GameEmbedder
{
    public static string LastError { get; private set; } = "";
    private const int StyleIndex = -16;
    private const int ExStyleIndex = -20;
    private const uint Popup = 0x80000000;
    private const uint Caption = 0x00C00000;
    private const uint ThickFrame = 0x00040000;
    private const uint Border = 0x00800000;
    private const uint DialogFrame = 0x00400000;
    private const uint Child = 0x40000000;
    private const uint Visible = 0x10000000;
    private const uint AppWindow = 0x00040000;
    private const uint ToolWindow = 0x00000080;
    private const uint WindowEdge = 0x00000100;
    private const uint ClientEdge = 0x00000200;
    private const uint FrameChanged = 0x0020;

    private static readonly Dictionary<IntPtr, (uint Style, uint ExStyle, NativeMethods.RectNative Bounds)> Saved = [];
    private static readonly Dictionary<IntPtr, (IntPtr Host, int X, int Y, int Width, int Height)> Placed = [];
    private static readonly Dictionary<IntPtr, StagePlace> Staged = [];
    private static readonly HashSet<IntPtr> Held = [];
    private static IntPtr _park;

    public static bool Place(IntPtr game, IntPtr host, int width, int height)
    {
        LastError = "";
        if (game == IntPtr.Zero || host == IntPtr.Zero || !NativeMethods.IsWindow(game) || !NativeMethods.IsWindow(host) || width < 8 || height < 8)
        {
            LastError = "游戏窗口或接入区域尚未就绪。";
            return false;
        }

        _ = SetThreadDpiHostingBehavior(1);
        if (!Saved.ContainsKey(game))
        {
            _ = NativeMethods.GetWindowRect(game, out var bounds);
            Saved[game] = (StyleOf(game), ExStyleOf(game), bounds);
        }

        Held.Add(game);
        Staged.Remove(game);
        var origin = Anchor(host, width, height);
        if (GetAncestor(game, 1) == host
            && Placed.TryGetValue(game, out var current)
            && current.Host == host
            && current.X == origin.X
            && current.Y == origin.Y
            && current.Width == width
            && current.Height == height)
        {
            return true;
        }

        if (!NativeMethods.Responds(game))
        {
            LastError = "游戏窗口暂时没有响应，稍后会自动重试。";
            return false;
        }

        if (NativeMethods.IsIconic(game))
        {
            _ = NativeMethods.ShowWindow(game, NativeMethods.SwRestore);
        }

        if (GetAncestor(game, 1) != host)
        {
            Marshal.SetLastPInvokeError(0);
            _ = SetParent(game, host);
            var parentError = Marshal.GetLastWin32Error();
            if (GetAncestor(game, 1) != host)
            {
                LastError = parentError == 5
                    ? "接入被 Windows 拒绝（错误 5）。请确认窗口台和游戏使用相同的管理员权限。"
                    : $"游戏仍在桌面，未能嵌入（Windows 错误 {parentError}）。可重新接入或复制诊断信息。";
                return false;
            }
        }

        var style = (Saved[game].Style & ~(Popup | Caption | ThickFrame | Border | DialogFrame)) | Child | Visible;
        var exStyle = Saved[game].ExStyle & ~(AppWindow | WindowEdge | ClientEdge);
        if (StyleOf(game) != style)
        {
            _ = SetWindowLong(game, StyleIndex, (int)style);
        }

        if (ExStyleOf(game) != exStyle)
        {
            _ = SetWindowLong(game, ExStyleIndex, (int)exStyle);
        }

        if (!Fits(game, host, origin.X, origin.Y, width, height))
        {
            PlaceInside(game, origin.X, origin.Y, width, height);
        }

        if (!NativeMethods.IsWindow(game) || GetAncestor(game, 1) != host)
        {
            Placed.Remove(game);
            return false;
        }

        Placed[game] = (host, origin.X, origin.Y, width, height);
        return true;
    }

    public static void Park(IntPtr game)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game))
        {
            Placed.Remove(game);
            Staged.Remove(game);
            Held.Remove(game);
            Saved.Remove(game);
            return;
        }

        if (!Held.Contains(game))
        {
            return;
        }

        if (!NativeMethods.Responds(game))
        {
            return;
        }

        _ = SetParent(game, ParkHost());
        Placed.Remove(game);
        Staged.Remove(game);
    }

    public static bool ShowFull(IntPtr game, int x, int y, int clientWidth, int clientHeight, bool forCapture = false)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game) || clientWidth < 8 || clientHeight < 8)
        {
            return false;
        }

        _ = SetThreadDpiHostingBehavior(1);
        var fitWidth = clientWidth;
        var fitHeight = clientHeight;
        if (forCapture)
        {
            // A DPI-unaware game keeps 800×600 in its own coordinates. On a 150%
            // desktop that window is 1200×900 physical pixels. Size it in physical
            // pixels or the capture check never matches.
            var scale = PhysicalScale(game);
            fitWidth = Math.Max(8, (int)Math.Round(clientWidth * scale));
            fitHeight = Math.Max(8, (int)Math.Round(clientHeight * scale));
        }

        if (!Saved.ContainsKey(game))
        {
            _ = NativeMethods.GetWindowRect(game, out var bounds);
            Saved[game] = (StyleOf(game), ExStyleOf(game), bounds);
        }

        Held.Add(game);
        Placed.Remove(game);
        if (Staged.TryGetValue(game, out var current)
            && IsReady(game)
            && Math.Abs(current.X - x) < 4
            && Math.Abs(current.Y - y) < 4
            && current.ForCapture == forCapture
            && (forCapture || (current.ClientWidth == clientWidth && current.ClientHeight == clientHeight)))
        {
            return true;
        }

        if (!NativeMethods.Responds(game))
        {
            return false;
        }

        var style = (Saved[game].Style & ~Child) | Visible;
        if (forCapture) style = (style & ~(Caption | ThickFrame | Border | DialogFrame)) | Popup;
        if ((style & (Popup | Caption)) == 0)
        {
            style |= Popup;
        }

        var exStyle = (Saved[game].ExStyle & ~(AppWindow | WindowEdge | ClientEdge)) | ToolWindow;
        if (forCapture) exStyle &= ~ToolWindow;
        if (GetAncestor(game, 1) != GetDesktopWindow())
        {
            _ = SetParent(game, IntPtr.Zero);
        }

        _ = SetWindowLong(game, StyleIndex, (int)style);
        _ = SetWindowLong(game, ExStyleIndex, (int)exStyle);

        if (NativeMethods.IsIconic(game))
        {
            _ = NativeMethods.ShowWindow(game, 8);
        }

        var outerWidth = fitWidth;
        var outerHeight = fitHeight;
        var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | FrameChanged;
        _ = NativeMethods.SetWindowPos(game, new IntPtr(1), x, y, outerWidth, outerHeight, flags);
        if (NativeMethods.GetWindowRect(game, out var window) && NativeMethods.GetClientRect(game, out var client))
        {
            var extraWidth = (window.Right - window.Left) - (client.Right - client.Left);
            var extraHeight = (window.Bottom - window.Top) - (client.Bottom - client.Top);
            var nextWidth = fitWidth + Math.Max(0, extraWidth);
            var nextHeight = fitHeight + Math.Max(0, extraHeight);
            if (Math.Abs(nextWidth - outerWidth) > 1 || Math.Abs(nextHeight - outerHeight) > 1
                || Math.Abs((client.Right - client.Left) - fitWidth) > 2
                || Math.Abs((client.Bottom - client.Top) - fitHeight) > 2)
            {
                outerWidth = nextWidth;
                outerHeight = nextHeight;
                _ = NativeMethods.SetWindowPos(game, new IntPtr(1), x, y, outerWidth, outerHeight, flags);
            }
        }

        if (!NativeMethods.IsWindow(game) || GetAncestor(game, 1) != GetDesktopWindow())
        {
            Staged.Remove(game);
            return false;
        }

        Staged[game] = new StagePlace(x, y, fitWidth, fitHeight, outerWidth, outerHeight, forCapture);
        return true;
    }

    private static double PhysicalScale(IntPtr game)
    {
        var context = GetWindowDpiAwarenessContext(game);
        var unaware = AreDpiAwarenessContextsEqual(context, new IntPtr(-1))
            || AreDpiAwarenessContextsEqual(context, new IntPtr(-5));
        if (!unaware)
        {
            return 1;
        }

        var monitor = MonitorFromWindow(game, 2);
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0 || dpiX < 96)
        {
            return 1;
        }

        return dpiX / 96.0;
    }

    public static void MoveStaged(IntPtr game, int x, int y)
    {
        if (!Staged.TryGetValue(game, out var place))
        {
            return;
        }

        if (!NativeMethods.IsWindow(game))
        {
            Staged.Remove(game);
            return;
        }

        if (Math.Abs(place.X - x) < 4 && Math.Abs(place.Y - y) < 4)
        {
            return;
        }

        if (!NativeMethods.Responds(game))
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            game,
            new IntPtr(1),
            x,
            y,
            place.OuterWidth,
            place.OuterHeight,
            NativeMethods.SwpNoActivate);
        Staged[game] = place with { X = x, Y = y };
    }

    public static bool IsReady(IntPtr game)
    {
        if (!Staged.TryGetValue(game, out var place) || !NativeMethods.IsWindow(game) || GetAncestor(game, 1) != GetDesktopWindow())
        {
            return false;
        }

        if (!NativeMethods.GetClientRect(game, out var client))
        {
            return false;
        }

        return Math.Abs(client.Right - client.Left - place.ClientWidth) < 12
            && Math.Abs(client.Bottom - client.Top - place.ClientHeight) < 12;
    }

    public static void ForgetClosed()
    {
        foreach (var game in Held.ToArray())
        {
            if (NativeMethods.IsWindow(game))
            {
                continue;
            }

            Held.Remove(game);
            Placed.Remove(game);
            Staged.Remove(game);
            Saved.Remove(game);
        }
    }

    public static IReadOnlyList<IntPtr> HeldHandles => Held.ToArray();

    public static void ParkAll()
    {
        var park = ParkHost();
        foreach (var game in Placed.Keys.ToArray())
        {
            if (!NativeMethods.IsWindow(game))
            {
                Placed.Remove(game);
                Staged.Remove(game);
                Held.Remove(game);
                continue;
            }

            _ = SetParent(game, park);
        }

        foreach (var game in Staged.Keys.ToArray())
        {
            if (NativeMethods.IsWindow(game))
            {
                _ = SetParent(game, park);
            }
        }

        Placed.Clear();
        Staged.Clear();
    }

    public static void EnsureTopLevel(IntPtr game)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game))
        {
            return;
        }

        if (Held.Contains(game))
        {
            Release(game);
            return;
        }

        var style = (uint)GetWindowLong(game, StyleIndex);
        if ((style & Child) == 0)
        {
            return;
        }

        _ = SetParent(game, IntPtr.Zero);
        var restored = (style & ~(Child | Popup)) | 0x00CF0000 | Visible;
        _ = SetWindowLong(game, StyleIndex, (int)restored);
        _ = NativeMethods.ShowWindow(game, 5);
    }

    public static void Detach(IntPtr game)
    {
        Release(game);
    }

    public static void ReleaseAll()
    {
        foreach (var game in Held.ToArray())
        {
            Release(game);
        }
    }

    private static void Release(IntPtr game)
    {
        Placed.Remove(game);
        Staged.Remove(game);
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game))
        {
            Held.Remove(game);
            return;
        }

        Restore(game);
        Held.Remove(game);
        Saved.Remove(game);
    }

    public static bool IsAttached(IntPtr game, IntPtr host)
    {
        return game != IntPtr.Zero && host != IntPtr.Zero && NativeMethods.IsWindow(game) && NativeMethods.IsWindow(host) && GetAncestor(game, 1) == host;
    }

    public static void KeepAll()
    {
        foreach (var (game, place) in Placed.ToArray())
        {
            KeepInside(game, place.Host, place.X, place.Y, place.Width, place.Height);
        }
    }

    public static void NudgeActive(IntPtr game)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game))
        {
            return;
        }

        _ = PostMessage(game, 0x0006, new IntPtr(1), IntPtr.Zero);
        _ = PostMessage(game, 0x0007, IntPtr.Zero, IntPtr.Zero);
    }

    private static void KeepInside(IntPtr game, IntPtr host, int x, int y, int width, int height)
    {
        if (!IsAttached(game, host) || !NativeMethods.GetWindowRect(game, out var rect))
        {
            return;
        }

        var origin = new PointNative();
        if (!ClientToScreen(host, ref origin))
        {
            return;
        }

        var currentWidth = rect.Right - rect.Left;
        var currentHeight = rect.Bottom - rect.Top;
        if (Math.Abs(rect.Left - (origin.X + x)) < 8
            && Math.Abs(rect.Top - (origin.Y + y)) < 8
            && Math.Abs(currentWidth - width) < 8
            && Math.Abs(currentHeight - height) < 8)
        {
            return;
        }

        PlaceInside(game, x, y, width, height);
    }

    private static bool Fits(IntPtr game, IntPtr host, int x, int y, int width, int height)
    {
        if (!NativeMethods.GetWindowRect(game, out var rect))
        {
            return false;
        }

        var origin = new PointNative();
        if (!ClientToScreen(host, ref origin))
        {
            return false;
        }

        return Math.Abs(rect.Left - (origin.X + x)) < 8
            && Math.Abs(rect.Top - (origin.Y + y)) < 8
            && Math.Abs(rect.Right - rect.Left - width) < 8
            && Math.Abs(rect.Bottom - rect.Top - height) < 8;
    }

    private static (int X, int Y) Anchor(IntPtr host, int width, int height)
    {
        if (!GetClientRect(host, out var client))
        {
            return (0, 0);
        }

        var hostWidth = Math.Max(0, client.Right - client.Left);
        var hostHeight = Math.Max(0, client.Bottom - client.Top);
        return (Math.Max(0, (hostWidth - width) / 2), Math.Max(0, (hostHeight - height) / 2));
    }

    private static void PlaceInside(IntPtr game, int x, int y, int width, int height)
    {
        var flags = NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | FrameChanged;
        if (!NativeMethods.IsWindowVisible(game))
        {
            flags |= NativeMethods.SwpShowWindow;
        }

        _ = NativeMethods.SetWindowPos(game, IntPtr.Zero, x, y, width, height, flags);
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeMethods.RectNative client);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr ParkHost()
    {
        if (_park != IntPtr.Zero && NativeMethods.IsWindow(_park))
        {
            return _park;
        }

        _park = CreateWindowExW(0, "Static", "", unchecked((int)0x80000000), 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return _park;
    }

    private static void Restore(IntPtr game)
    {
        if (!NativeMethods.Responds(game))
        {
            return;
        }

        _ = SetParent(game, IntPtr.Zero);
        if (Saved.TryGetValue(game, out var saved))
        {
            _ = SetWindowLong(game, StyleIndex, (int)saved.Style);
            _ = SetWindowLong(game, ExStyleIndex, (int)saved.ExStyle);
        }

        _ = NativeMethods.ShowWindow(game, 9);
        if (Saved.TryGetValue(game, out var original) && original.Bounds.Right > original.Bounds.Left
            && original.Bounds.Bottom > original.Bounds.Top && original.Bounds.Left > -30000 && original.Bounds.Top > -30000)
        {
            _ = NativeMethods.SetWindowPos(game, IntPtr.Zero, original.Bounds.Left, original.Bounds.Top,
                original.Bounds.Right - original.Bounds.Left, original.Bounds.Bottom - original.Bounds.Top,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | FrameChanged);
        }
        else
        {
            _ = NativeMethods.SetWindowPos(game, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpShowWindow | FrameChanged);
        }
        WindowArranger.EnsureMinimumSize(game);
    }

    private static uint StyleOf(IntPtr hwnd)
    {
        return (uint)GetWindowLong(hwnd, StyleIndex);
    }

    private static uint ExStyleOf(IntPtr hwnd)
    {
        return (uint)GetWindowLong(hwnd, ExStyleIndex);
    }

    public static void Focus(IntPtr game)
    {
        if (game == IntPtr.Zero || !NativeMethods.IsWindow(game))
        {
            return;
        }

        var root = GetAncestor(game, 2);
        var top = root == IntPtr.Zero ? game : root;
        if (NativeMethods.GetWindowThreadProcessId(top, out _) == NativeMethods.GetCurrentThreadId()
            && NativeMethods.GetForegroundWindow() != top)
        {
            _ = NativeMethods.SetForegroundWindow(top);
        }

        if (!NativeMethods.Responds(game) || Interlocked.Exchange(ref _focusBusy, 1) == 1)
        {
            return;
        }

        var target = game;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                FocusGame(target);
            }
            finally
            {
                Interlocked.Exchange(ref _focusBusy, 0);
            }
        }, null);
    }

    private static int _focusBusy;

    private static void FocusGame(IntPtr game)
    {
        if (!NativeMethods.IsWindow(game) || !NativeMethods.Responds(game))
        {
            return;
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(game, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = targetThread != 0 && targetThread != currentThread && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            _ = SetFocus(game);
        }
        finally
        {
            if (attached)
            {
                _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int SetThreadDpiHostingBehavior(int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref PointNative point);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    private readonly record struct StagePlace(int X, int Y, int ClientWidth, int ClientHeight, int OuterWidth, int OuterHeight, bool ForCapture);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}

internal static class GameCursorCloak
{
    private const int DoubleClicks = 0x0008;
    private const int ChildVisible = 0x50000000;
    private const int Layered = 0x00080000;
    private const int NoActivate = 0x08000000;
    private const int NoMove = 0x0002;
    private const int NoSize = 0x0001;
    private const int NoActivatePos = 0x0010;
    private const int Show = 0x0040;
    private const int Alpha = 0x00000002;
    private const int SetCursorMessage = 0x0020;
    private const int MouseActivate = 0x0021;
    private const int MouseFirst = 0x0200;
    private const int MouseLast = 0x020E;
    private const int LeftDown = 0x0201;
    private const int LeftUp = 0x0202;
    private const int RightDown = 0x0204;
    private const int RightUp = 0x0205;
    private const int MiddleDown = 0x0207;
    private const int MiddleUp = 0x0208;
    private const int LeftButton = 0x0001;
    private const int RightButton = 0x0002;
    private const int MiddleButton = 0x0010;

    private static IntPtr _hwnd;
    private static IntPtr _host;
    private static IntPtr _game;
    private static CloakProc? _proc;
    private static bool _classReady;
    private static int _x;
    private static int _y;
    private static int _width;
    private static int _height;

    public static void Sync(IntPtr host, IntPtr game)
    {
        if (host == IntPtr.Zero || game == IntPtr.Zero || !IsWindow(host) || !IsWindow(game))
        {
            Hide();
            return;
        }

        if (!GetWindowRect(game, out var screen))
        {
            Hide();
            return;
        }

        MapWindowPoints(IntPtr.Zero, host, ref screen, 2);
        var left = screen.Left;
        var top = screen.Top;
        var width = screen.Right - screen.Left;
        var height = screen.Bottom - screen.Top;
        if (width < 8 || height < 8)
        {
            Hide();
            return;
        }

        Ensure(host);
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_game != game) GamePointer.ReleaseOwnedCapture(_hwnd);
        _game = game;
        var moved = _x != left || _y != top || _width != width || _height != height;
        _x = left;
        _y = top;
        _width = width;
        _height = height;
        var flags = NoActivatePos | Show;
        if (!moved)
        {
            flags |= NoMove | NoSize;
        }

        _ = SetWindowPos(_hwnd, IntPtr.Zero, _x, _y, _width, _height, flags);
    }

    public static void Hide()
    {
        var hadTarget = _game != IntPtr.Zero;
        _game = IntPtr.Zero;
        var released = GamePointer.ReleaseOwnedCapture(_hwnd);
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            _ = ShowWindow(_hwnd, 0);
        }
        if (hadTarget || released) GamePointer.RestoreArrow();
    }

    public static void Close()
    {
        Hide();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            _ = DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        _host = IntPtr.Zero;
    }

    private static void Ensure(IntPtr host)
    {
        if (_hwnd != IntPtr.Zero && _host == host && IsWindow(_hwnd))
        {
            return;
        }

        Close();
        _ = SetThreadDpiHostingBehavior(1);
        Register();
        _proc = OnMessage;
        _hwnd = CreateWindowExW(
            Layered | NoActivate,
            "CgDeskCloak",
            "",
            ChildVisible,
            0,
            0,
            0,
            0,
            host,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        _host = host;
        if (_hwnd != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_proc));
            _ = SetLayeredWindowAttributes(_hwnd, 0, 0, Alpha);
        }
    }

    private static void Register()
    {
        if (_classReady)
        {
            return;
        }

        var wndClass = new WndClass
        {
            Style = DoubleClicks,
            WndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            ClassName = "CgDeskCloak"
        };
        _classReady = RegisterClass(ref wndClass) != 0 || Marshal.GetLastWin32Error() == 1410;
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == SetCursorMessage)
        {
            if (_game != IntPtr.Zero && IsWindow(_game)) _ = SetCursor(IntPtr.Zero);
            else
            {
                GamePointer.ReleaseOwnedCapture(hwnd);
                GamePointer.RestoreArrow();
            }
            return new IntPtr(1);
        }

        if (msg == MouseActivate)
        {
            return new IntPtr(3);
        }

        if (msg == 0x001F)
        {
            GamePointer.ReleaseOwnedCapture(hwnd);
            GamePointer.RestoreArrow();
        }
        if (msg is LeftUp or RightUp or MiddleUp && (wParam.ToInt32() & (LeftButton | RightButton | MiddleButton)) == 0)
            GamePointer.ReleaseOwnedCapture(hwnd);
        if (msg is >= MouseFirst and <= MouseLast && (_game == IntPtr.Zero || !IsWindow(_game)))
        {
            GamePointer.ReleaseOwnedCapture(hwnd);
            GamePointer.RestoreArrow();
        }
        if (msg is >= MouseFirst and <= MouseLast && _game != IntPtr.Zero && IsWindow(_game))
        {
            _ = PostMessage(_game, msg, wParam, lParam);
            if (msg is LeftDown or RightDown or MiddleDown)
            {
                _ = SetCapture(hwnd);
            }

            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private delegate IntPtr CloakProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? Menu;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int SetThreadDpiHostingBehavior(int value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out ScreenRect rect);

    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref ScreenRect rect, int points);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, int flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
