using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CgDesk;
using CgDesk.Services;

internal static partial class Program
{
    private static void TestFourByThreeFsr(App app)
    {
        var main = NewDesk(app);
        var clientStart = Clients.Count;
        try
        {
            main.WindowState = WindowState.Maximized;
            main.Show();
            Pump();
            Get<DispatcherTimer>(main, "_timer").Stop();
            File.WriteAllText(Marker, "CgDesk regression client marker");
            var client = StartClient();
            Call(main, "RefreshStatus");
            var host = Get<GameHost>(main, "_mainHost");
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "FSR fixture is embedded before enhancement.");
            // WPF's decorated test window has a theme-dependent client height.
            // Once borderless, give the fixture the game's exact drawing size.
            NativeMethods.SetWindowPos(client.Handle, IntPtr.Zero, 0, 0, 800, 600,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder);
            Call(main, "ApplyNativeFrame");
            Pump();
            Call(main, "EnableFsrForResize");
            Call(main, "UpdatePresentationFit");
            PumpFor(300);
            Wait(() => FsrSurface.IsPresenting || FsrSurface.Failed, "FSR pipeline reports a render result.");
            Console.WriteLine($"FSR presenting: {FsrSurface.IsPresenting}; failure: {FsrSurface.LastError}");
            if (!FsrSurface.IsPresenting)
            {
                foreach (var method in new[] { "StyleOf", "ExStyleOf" })
                    Console.WriteLine($"Capture source {method}: {typeof(GameEmbedder).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [client.Handle]):X}");
                Console.WriteLine($"Capture source ready: {GameEmbedder.IsReady(client.Handle)}; process {client.ProcessId}; handle {client.Handle:X}; visible {NativeMethods.IsWindowVisible(client.Handle)}");
            }
            Require(FsrSurface.IsPresenting, "Embedded client produces real FSR frames.");
            Wait(() => Get<bool>(main, "_fsrRequested"), "FSR output activates the expanded viewport.");
            var viewport = (Border)main.FindName("MainViewport");
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(main);
            Console.WriteLine($"FSR viewport: {viewport.ActualWidth * dpi.DpiScaleX:0} x {viewport.ActualHeight * dpi.DpiScaleY:0}");
            Require(viewport.ActualWidth * dpi.DpiScaleX > 800 && viewport.ActualHeight * dpi.DpiScaleY > 600, "FSR output grows beyond the original 800x600 image.");
            Wait(() => FsrSurface.Failed || (FsrSurface.RenderedFrames >= 3
                && Math.Abs(FsrSurface.OutputSize.Width - viewport.ActualWidth * dpi.DpiScaleX) <= 2
                && Math.Abs(FsrSurface.OutputSize.Height - viewport.ActualHeight * dpi.DpiScaleY) <= 2), "FSR swap chain reaches the expanded viewport size.");
            Require(!FsrSurface.Failed && FsrSurface.IsPresenting, "FSR keeps rendering after its initial expansion: " + FsrSurface.LastError);
            var oldFrames = FsrSurface.RenderedFrames;
            main.WindowState = WindowState.Normal;
            main.Width = Math.Min(1150, SystemParameters.WorkArea.Width);
            main.Height = Math.Min(900, SystemParameters.WorkArea.Height);
            Pump();
            Call(main, "EnableFsrForResize");
            Call(main, "UpdatePresentationFit");
            Wait(() => FsrSurface.Failed || (FsrSurface.RenderedFrames > oldFrames + 2
                && Math.Abs(FsrSurface.OutputSize.Width - viewport.ActualWidth * dpi.DpiScaleX) <= 2), "FSR output resizes with the desk.");
            Require(!FsrSurface.Failed && FsrSurface.IsPresenting, "Resizing the desk keeps GPU rendering alive: " + FsrSurface.LastError);
            var sampled = ReadFsrPixel();
            Console.WriteLine($"FSR output pixel: R={sampled.R}, G={sampled.G}, B={sampled.B}");
            Require(Math.Abs(sampled.R - 72) < 8 && Math.Abs(sampled.G - 61) < 8 && Math.Abs(sampled.B - 139) < 8, "FSR GPU output contains the captured source image instead of a blank surface.");
            Require(Math.Abs(FsrSurface.OutputSize.Width * 3 - FsrSurface.OutputSize.Height * 4) <= 8, "Expanded output preserves the 4:3 aspect ratio.");
            _ = NativeMethods.GetClientRect(client.Handle, out var nativeClient);
            Require(nativeClient.Right == 800 && nativeClient.Bottom == 600, "Super resolution leaves the game client at 800x600.");
            Call(main, "FocusSeat", 0);
            Require(GetNativeFocus() == host.HostHandle, "FSR selection focuses the native IME host.");
            var fsrWindow = (IntPtr)typeof(FsrSurface).GetField("_hwnd", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var center = new IntPtr(((FsrSurface.OutputSize.Height / 2) << 16) | (FsrSurface.OutputSize.Width / 2));
            PostMessage(fsrWindow, 0x201, new IntPtr(1), center);
            PostMessage(fsrWindow, 0x202, IntPtr.Zero, center);
            Wait(() => ReadFixtureInput(client.ProcessId, "mouse") == "400,300", "FSR forwards clicks to the correct coordinates in the native game.");
            // A letter may legitimately start Chinese composition in this desktop's
            // active IME. Use a game function key to test ordinary key forwarding.
            PostMessage(host.HostHandle, 0x100, new IntPtr(0x77), new IntPtr(0x00420001));
            PostMessage(host.HostHandle, 0x101, new IntPtr(0x77), new IntPtr(unchecked((int)0xC0420001)));
            Wait(() => ReadFixtureInput(client.ProcessId, "key") == "119", "FSR mode forwards keyboard messages to the game.");
            PostImeMessage(host.HostHandle, 0x0286, new IntPtr('中'), new IntPtr(1));
            PostImeMessage(host.HostHandle, 0x0286, new IntPtr('文'), new IntPtr(1));
            Wait(() => ReadFixtureInput(client.ProcessId, "text").EndsWith("中文", StringComparison.Ordinal), "FSR mode delivers committed Chinese text to the separate game process.");
            var fsrGate = typeof(FsrSurface).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            lock (fsrGate)
                typeof(FsrSurface).GetMethod("Fail", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["Regression: capture interrupted"]);
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "Capture failure falls back to a usable native game window.");
            Call(main, "ResetFsrPresentation");
            Field(main, "_scaleAfterResize", false);
            Call(main, "ApplyNativeFrame");
            Call(main, "EmbedIntoHosts");
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "Disabling FSR restores native embedding.");
            Require(!FsrSurface.IsPresenting, "Disabling FSR hides the enlarged output.");
            Call(main, "EnableFsrForResize");
            Call(main, "UpdatePresentationFit");
            Wait(() => FsrSurface.Failed || FsrSurface.RenderedFrames >= 2, "FSR can be enabled again after a capture failure.");
            Require(!FsrSurface.Failed && FsrSurface.IsPresenting, "Retrying scaling recovers real GPU frames.");
            Call(main, "ResetFsrPresentation");
            Field(main, "_scaleAfterResize", false);
            Call(main, "EmbedIntoHosts");
            NativeMethods.SetWindowPos(client.Handle, IntPtr.Zero, 0, 0, 1600, 1200,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder);
            Call(main, "RefreshStatus");
            Pump();
            main.Width = Math.Min(1150, SystemParameters.WorkArea.Width);
            main.Height = Math.Min(900, SystemParameters.WorkArea.Height);
            Call(main, "EnableFsrForResize");
            Call(main, "UpdatePresentationFit");
            Wait(() => FsrSurface.Failed || FsrSurface.RenderedFrames >= 3, "1600x1200 source produces reduced frames.");
            Require(FsrSurface.IsPresenting && !FsrSurface.Failed && FsrSurface.OutputSize.Width < 1600
                && FsrSurface.OutputSize.Width * 3 == FsrSurface.OutputSize.Height * 4,
                "Large native drawing scales down with exact 4:3 output.");
            NativeMethods.GetClientRect(client.Handle, out nativeClient);
            Require(nativeClient.Right == 1600 && nativeClient.Bottom == 1200, "Resizing presentation retains the 1600x1200 drawing size.");
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            Wait(() => NativeMethods.GetWindowRect(client.Handle, out var sourceRect)
                && sourceRect.Right <= desktop.Left + 1 && sourceRect.Bottom <= desktop.Top + 1,
                "A reduced desk moves the initialized source off the desktop.");
            Wait(() =>
            {
                if (!FsrSurface.IsPresenting || FsrSurface.Failed) return false;
                sampled = ReadFsrPixel();
                return Math.Abs(sampled.R - 72) < 8 && Math.Abs(sampled.G - 61) < 8 && Math.Abs(sampled.B - 139) < 8;
            }, "Off-screen source continues supplying the complete captured image.");
            center = new IntPtr(((FsrSurface.OutputSize.Height / 2) << 16) | (FsrSurface.OutputSize.Width / 2));
            PostMessage(fsrWindow, 0x201, new IntPtr(1), center);
            PostMessage(fsrWindow, 0x202, IntPtr.Zero, center);
            Wait(() => ReadFixtureInput(client.ProcessId, "mouse") == "800,600", "Reduced 1600x1200 output keeps pointer coordinates accurate.");
            main.WindowState = WindowState.Minimized;
            Call(main, "SyncGameCloak");
            Require(!FsrSurface.IsPresenting, "Minimizing releases active capture.");
            main.WindowState = WindowState.Normal;
            Wait(() => FsrSurface.Failed || FsrSurface.IsPresenting, "Restoring the desk resumes scaling.");
            Require(FsrSurface.IsPresenting && !FsrSurface.Failed, "Restored desk displays captured frames again.");
            PostMessage(client.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
            Wait(() => !NativeMethods.IsWindow(client.Handle), "Captured client exits.");
            Call(main,"RefreshStatus"); Call(main,"SyncGameCloak");
            Require(!FsrSurface.IsPresenting && host.KeyTarget == IntPtr.Zero, "Captured game exit releases scaling and stale input.");
        }
        finally
        {
            CloseDesk(main);
            foreach (var client in Clients.Skip(clientStart)) if (!client.HasExited) { client.Kill(); client.WaitForExit(3000); }
            foreach (var client in Clients.Skip(clientStart))
                foreach (var kind in new[] { "mouse", "key", "text" })
                    File.Delete(Path.Combine(AppContext.BaseDirectory, $"client-{client.Id}.{kind}"));
            if (File.Exists(Marker)) File.Delete(Marker);
        }
    }

    private static string ReadFixtureInput(int processId, string kind)
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"client-{processId}.{kind}")); }
        catch (IOException) { return ""; }
    }

    private static (byte R, byte G, byte B) ReadFsrPixel()
    {
        var hiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
        var gate = typeof(FsrSurface).GetField("Gate", hiddenStatic)!.GetValue(null)!;
        lock (gate)
        {
            var texture = (Vortice.Direct3D11.ID3D11Texture2D)typeof(FsrSurface).GetField("_easuTex", hiddenStatic)!.GetValue(null)!;
            var device = (Vortice.Direct3D11.ID3D11Device)typeof(FsrSurface).GetField("_device", hiddenStatic)!.GetValue(null)!;
            var context = (Vortice.Direct3D11.ID3D11DeviceContext)typeof(FsrSurface).GetField("_context", hiddenStatic)!.GetValue(null)!;
            var description = texture.Description;
            description.Usage = Vortice.Direct3D11.ResourceUsage.Staging;
            description.BindFlags = Vortice.Direct3D11.BindFlags.None;
            description.CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read;
            description.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(description);
            context.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixel = mapped.DataPointer + (int)(mapped.RowPitch * (description.Height / 10) + description.Width / 10 * 4);
                return (Marshal.ReadByte(pixel, 2), Marshal.ReadByte(pixel, 1), Marshal.ReadByte(pixel));
            }
            finally { context.Unmap(staging, 0); }
        }
    }

}
