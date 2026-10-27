using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WndClass wndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass { public uint Style; public IntPtr Proc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; }
    struct Rect { public int Left, Top, Right, Bottom; }
    struct Msg { public IntPtr Hwnd; public uint Message; public nint WParam; public nint LParam; public uint Time; public int X, Y; }
    static WndProc? _proc;

    [STAThread]
    static int Main()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-1));
        _proc = (hwnd, msg, wParam, lParam) =>
        {
            if (msg == 0x201)
            {
                var packed = unchecked((int)lParam);
                Console.WriteLine($"GOT={(short)packed},{(short)(packed >> 16)}");
                Console.Out.Flush();
                PostQuitMessage(0);
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        };
        var cls = new WndClass { Proc = Marshal.GetFunctionPointerForDelegate(_proc), ClassName = "CgDeskDpiMouseChild" };
        RegisterClass(ref cls);
        var hwnd = CreateWindowExW(0, cls.ClassName, "dpi-mouse-child", unchecked((int)0x80000000) | 0x10000000, 80, 80, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        GetClientRect(hwnd, out var client);
        Console.WriteLine($"HWND={hwnd.ToInt64()} LOGICAL={client.Right - client.Left}x{client.Bottom - client.Top}");
        Console.Out.Flush();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return 0;
    }
}
