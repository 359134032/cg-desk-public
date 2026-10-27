using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CgDesk;

internal static class DpiFix
{
    [ModuleInitializer]
    internal static void Init()
    {
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
