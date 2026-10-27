using System.Runtime.InteropServices;

namespace CgDesk.Services;

internal static class GamePointer
{
    internal static bool ReleaseOwnedCapture(IntPtr window)
    {
        if (window == IntPtr.Zero || GetCapture() != window) return false;
        return ReleaseCapture();
    }

    internal static void RestoreArrow() => SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
}
