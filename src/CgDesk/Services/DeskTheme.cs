using System.Runtime.InteropServices;
using System.Windows.Interop;
using Media = System.Windows.Media;
using WpfApp = System.Windows.Application;
using Window = System.Windows.Window;

namespace CgDesk.Services;

internal static class DeskTheme
{
    public static bool Dark { get; private set; }

    public static void Apply(bool dark)
    {
        Dark = dark;
        Paint("InkBrush", dark ? "#181818" : "#F3F3F3");
        Paint("SideBrush", dark ? "#141414" : "#EBEBEB");
        Paint("PanelBrush", dark ? "#1F1F1F" : "#FFFFFF");
        Paint("LineBrush", dark ? "#2B2B2B" : "#E4E4E4");
        Paint("TextBrush", dark ? "#D4D4D4" : "#1E1E1E");
        Paint("MutedBrush", dark ? "#8A8A8A" : "#6E6E6E");
        Paint("GoldBrush", dark ? "#4DA3FF" : "#007ACC");
        Paint("AccentHoverBrush", dark ? "#6BB3FF" : "#0068B0");
        Paint("AccentPressBrush", dark ? "#3A8EE6" : "#005A99");
        Paint("FieldBrush", dark ? "#2A2A2A" : "#FFFFFF");
        Paint("HoverBrush", dark ? "#2A2A2A" : "#E8E8E8");
        Paint("PressBrush", dark ? "#333333" : "#DCDCDC");
        Paint("SelectBrush", dark ? "#094771" : "#D6E4F5");
        Paint("TrackBrush", dark ? "#3A3A3A" : "#D4D4D4");
        Paint("PopupBrush", dark ? "#252526" : "#FFFFFF");
        Paint("KnobBrush", "#FFFFFF");
        Paint("WarnBrush", dark ? "#F14C4C" : "#E5484D");
        Paint("WarnBarBrush", dark ? "#3A2E1A" : "#FFF6E8");
        Paint("WarnBarLineBrush", "#E09A3E");
        if (WpfApp.Current == null)
        {
            return;
        }

        foreach (Window window in WpfApp.Current.Windows)
        {
            ApplyTitleBar(window);
        }
    }

    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
    }

    private static void Paint(string key, string hex)
    {
        if (WpfApp.Current == null) return;
        // WPF freezes brushes used by templates. Replace the palette entry and let
        // DynamicResource consumers refresh rather than silently skipping frozen brushes.
        WpfApp.Current.Resources[key] = new Media.SolidColorBrush(
            (Media.Color)Media.ColorConverter.ConvertFromString(hex)!);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
