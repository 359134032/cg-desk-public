using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using CgDesk;
using CgDesk.Services;

internal static partial class Program
{
    private static void TestImeInput(App app)
    {
        var main = NewDesk(app);
        var received = new ConcurrentQueue<byte>();
        var keys = new ConcurrentQueue<int>();
        var target = IntPtr.Zero;
        ImeWindowProc callback = (hwnd, message, key, data) =>
        {
            if (message == 0x102) received.Enqueue((byte)key.ToInt32());
            if (message == 0x100) keys.Enqueue(key.ToInt32());
            if (message == 0x0002) PostImeQuitMessage(0);
            return DefWindowProcA(hwnd, message, key, data);
        };
        var className = "CgDeskImeRegression";
        var ready = new TaskCompletionSource<IntPtr>();
        var fixtureThread = new Thread(() =>
        {
            var windowClass = new ImeWindowClass { Procedure = Marshal.GetFunctionPointerForDelegate(callback), ClassName = className };
            if (RegisterClassA(ref windowClass) == 0) { ready.SetException(new InvalidOperationException("Cannot register ANSI fixture.")); return; }
            var hwnd = CreateWindowExA(0, className, "IME fixture", 0, 0, 0, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            ready.SetResult(hwnd);
            if (hwnd != IntPtr.Zero)
                while (GetImeMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateImeMessage(ref message);
                    DispatchImeMessage(ref message);
                }
            UnregisterClassA(className, IntPtr.Zero);
        }) { IsBackground = true };
        try
        {
            main.Show();
            Pump();
            fixtureThread.Start();
            Require(ready.Task.Wait(TimeSpan.FromSeconds(5)), "ANSI game fixture registers on its own input thread.");
            target = ready.Task.Result;
            Require(target != IntPtr.Zero, "ANSI game fixture opens.");
            ((System.Windows.UIElement)main.FindName("MainPane")).Visibility = System.Windows.Visibility.Visible;
            ((System.Windows.UIElement)main.FindName("MainFrame")).Visibility = System.Windows.Visibility.Visible;
            Pump();
            var host = Get<GameHost>(main, "_mainHost");
            host.KeyTarget = target;
            host.FocusInput();
            Require(GetNativeFocus() == host.HostHandle, "IME focus belongs to the native game host.");
            var context = TestImmGetContext(host.HostHandle);
            Require(context != IntPtr.Zero, "Native game host has a live IME context.");
            // The English-key assertion must not depend on the desktop's current
            // Chinese/English mode. This context belongs only to the test process.
            TestImmSetOpenStatus(context, false);
            TestImmReleaseContext(host.HostHandle, context);

            // These are the IMM notifications for composing, committing, and ending
            // text. Candidate selection keys must never become game hotkeys/Enter.
            SendImeMessage(host.HostHandle, 0x010D, IntPtr.Zero, IntPtr.Zero);
            SendImeMessage(host.HostHandle, 0x0100, new IntPtr(0x0D), new IntPtr(1));
            SendImeMessage(host.HostHandle, 0x0100, new IntPtr(0xE5), new IntPtr(1));
            Pump();
            Require(keys.Count == 0, "Composition Enter and VK_PROCESSKEY are not forwarded as game keys.");
            SendImeMessage(host.HostHandle, 0x0286, new IntPtr('中'), new IntPtr(1));
            SendImeMessage(host.HostHandle, 0x0286, new IntPtr('文'), new IntPtr(1));
            SendImeMessage(host.HostHandle, 0x010E, IntPtr.Zero, IntPtr.Zero);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var expected = Encoding.GetEncoding((int)GetACP()).GetBytes("中文");
            Wait(() => received.Count >= expected.Length, "ANSI game receives committed text.");
            Require(received.SequenceEqual(expected), "Chinese IME result reaches an ANSI game in its code page exactly once.");
            received.Clear();
            PostImeMessage(host.HostHandle, 0x0100, new IntPtr(0x41), new IntPtr(0x001E0001));
            PostImeMessage(host.HostHandle, 0x0101, new IntPtr(0x41), new IntPtr(unchecked((int)0xC01E0001)));
            Wait(() => keys.Count > 0 && received.Count > 0, "English input arrives after IME composition.");
            Require(keys.SequenceEqual(new[] { 0x41 }) && received.SequenceEqual(new byte[] { 0x61 }), $"English keys and text still reach the game after composition (keys={string.Join(',', keys)}, text={Convert.ToHexString(received.ToArray())}).");
            SendImeMessage(host.HostHandle, 0x010D, IntPtr.Zero, IntPtr.Zero);
            host.KeyTarget = IntPtr.Zero;
            Require(!(bool)typeof(GameHost).GetField("_imeComposing", Hidden)!.GetValue(host)!, "Disconnecting a client clears its pending composition.");
            host.KeyTarget = target;
            PostImeMessage(host.HostHandle, 0x0100, new IntPtr(0x42), new IntPtr(1));
            Wait(() => keys.Count >= 2, "Input arrives after changing clients.");
            Require(keys.Last() == 0x42, "Switching clients does not leave keyboard input in composition mode.");
        }
        finally
        {
            if (target != IntPtr.Zero) PostImeMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero);
            if (fixtureThread.IsAlive) fixtureThread.Join(3000);
            CloseDesk(main);
            GC.KeepAlive(callback);
        }
    }

    private delegate IntPtr ImeWindowProc(IntPtr hwnd, uint message, IntPtr key, IntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct ImeWindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu;
        public string ClassName;
    }

    [DllImport("user32.dll", EntryPoint = "GetFocus")] private static extern IntPtr GetNativeFocus();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendImeMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostImeMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern ushort RegisterClassA(ref ImeWindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern bool UnregisterClassA(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr CreateWindowExA(int extendedStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcA(IntPtr hwnd, uint message, IntPtr key, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMessageA")] private static extern int GetImeMessage(out System.Windows.Interop.MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage")] private static extern bool TranslateImeMessage(ref System.Windows.Interop.MSG message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageA")] private static extern IntPtr DispatchImeMessage(ref System.Windows.Interop.MSG message);
    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")] private static extern void PostImeQuitMessage(int result);
    [DllImport("imm32.dll", EntryPoint = "ImmGetContext")] private static extern IntPtr TestImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll", EntryPoint = "ImmReleaseContext")] private static extern bool TestImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll", EntryPoint = "ImmSetOpenStatus")] private static extern bool TestImmSetOpenStatus(IntPtr context, bool open);
    [DllImport("kernel32.dll")] private static extern uint GetACP();
}
