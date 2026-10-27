using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CgDesk;
using CgDesk.Models;
using CgDesk.Services;

internal static partial class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static readonly List<Process> Clients = [];
    private static readonly string Marker = Path.Combine(AppContext.BaseDirectory, "Config.dat");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--client"))
        {
            var countArg = args.FirstOrDefault(value => value.StartsWith("--windows="));
            return RunClient(countArg == null ? 1 : int.Parse(countArg[10..], CultureInfo.InvariantCulture));
        }
        if (File.Exists(Marker)) throw new InvalidOperationException("Test marker already exists; use a clean test output directory.");
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, "Application_Startup");
        try
        {
            if (args.Length == 3 && args[0] == "--verify-update")
            {
                using var verifier = new UpdateService();
                var result = verifier.Verify(File.ReadAllBytes(args[1]), args[2], null, "offline");
                Console.WriteLine($"PASS: Trusted manifest {result.Manifest.Version} ({result.Manifest.Channel}); package {result.Package.Size} bytes.");
                return 0;
            }
            if (args.Contains("--stress-layout")) { TestManyWindows(app, args.Contains("--enforce-clipping")); return 0; }
            if (args.Contains("--probe-theme")) { TestTheme(app); return 0; }
            if (args.Contains("--probe-fsr")) { TestSuperResolution(app); return 0; }
            if (args.Contains("--probe-dpi-mouse")) { TestScaledMouse(); Console.WriteLine($"PASS: {_checks} dpi mouse checks."); return 0; }
            if (args.Contains("--probe-cursor")) { TestCursorAfterGameExit(app, true); TestCursorAfterGameExit(app, false); return 0; }
            if (args.Contains("--probe-update")) { TestUpdates(app); Console.WriteLine($"PASS: {_checks} update checks."); return 0; }
            TestUpdates(app);
            TestLauncherEncoding();
            TestPreviewPlacement();
            TestConfigPrivacy();
            TestSlotReuse();
            TestSettingsPanel(app);
            TestGuide(app, false);
            TestGuide(app, true);
            TestTheme(app);
            if (args.Contains("--review"))
            {
                var main = NewDesk(app);
                main.Closed += (_, _) => Dispatcher.ExitAllFrames();
                main.Show();
                if (args.Contains("--with-clients"))
                {
                    File.WriteAllText(Marker, "CgDesk regression client marker");
                    StartClient();
                    StartClient();
                }
                Dispatcher.Run();
            }
            else
            {
                TestConnection(app);
                TestCursorAfterGameExit(app, false);
                TestCursorAfterGameExit(app, true);
                TestSuperResolution(app);
                TestScaledMouse();
            }
            Console.WriteLine($"PASS: {_checks} checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) CloseDesk(window);
            foreach (var client in Clients)
            {
                if (!client.HasExited) client.Kill();
                client.Dispose();
            }
            if (File.Exists(Marker)) File.Delete(Marker);
            app.Shutdown();
        }
    }

    private static MainWindow NewDesk(App app, bool showGuide = false, AppConfig? config = null)
    {
        var main = new MainWindow(config ?? new AppConfig { ShowGuide = showGuide, HotkeysEnabled = false }, initializeEnhancement: false,
            windowFilter: window => Clients.Any(client => client.Id == window.ProcessId), persistConfiguration: false);
        app.MainWindow = main;
        Field(main, "_guideStarted", true); // This check must not download/install browser components.
        return main;
    }

    private static void TestSettingsPanel(App app)
    {
        Require(new AppConfig().SettingsExpanded, "Settings are expanded by default for new users.");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"SettingsExpanded\":false,\"HotkeysEnabled\":false}")!;
        var main = NewDesk(app, config: legacy);
        string saved;
        try
        {
            main.Show();
            Pump();
            Require(((Border)main.FindName("SideBar")).IsVisible && legacy.SettingsExpanded, "Existing users see settings on their first upgraded launch.");
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("SettingsToggle");
            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Require(!((Border)main.FindName("SideBar")).IsVisible && toggle.IsVisible, "Collapsing settings leaves its toolbar entry visible.");
            saved = System.Text.Json.JsonSerializer.Serialize(main.Config);
        }
        finally { CloseDesk(main); }
        var restored = NewDesk(app, config: System.Text.Json.JsonSerializer.Deserialize<AppConfig>(saved)!);
        try
        {
            restored.Show();
            Pump();
            Require(!((Border)restored.FindName("SideBar")).IsVisible, "A user's later collapse preference survives restart.");
            var toggle = (System.Windows.Controls.Primitives.ToggleButton)restored.FindName("SettingsToggle");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump();
            Require(((System.Windows.Controls.TextBox)restored.FindName("LauncherPathBox")).IsVisible, "The persistent settings entry reopens the configuration panel.");
        }
        finally { CloseDesk(restored); }
    }

    private static void TestTheme(App app)
    {
        var main = NewDesk(app);
        try
        {
            main.Show();
            Pump();
            Console.WriteLine($"Theme before: {main.Background}; frozen palette: {((System.Windows.Media.SolidColorBrush)app.Resources["InkBrush"]).IsFrozen}");
            ((System.Windows.Controls.CheckBox)main.FindName("DarkBox")).IsChecked = true;
            Pump();
            Require(main.Config.DarkMode && DeskTheme.Dark, "Dark toggle updates the active theme preference.");
            Require(((System.Windows.Media.SolidColorBrush)main.Background).Color == System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18), "Dark toggle changes the visible window background.");
            Require(((System.Windows.Media.SolidColorBrush)((Border)main.FindName("Toolbar")).Background).Color == System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x1F), "Dark toggle updates existing panels.");
            var guide = Get<GuideWindow>(main, "_guideWindow");
            Call(main, "ShowGuideWindow", false);
            Pump();
            Require(((System.Windows.Media.SolidColorBrush)guide.Background).Color == System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x18), "The floating guide uses the active dark theme.");
            ((System.Windows.Controls.CheckBox)main.FindName("DarkBox")).IsChecked = false;
            Pump();
            Require(((System.Windows.Media.SolidColorBrush)main.Background).Color == System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3), "Light toggle restores the visible window background.");
            Require(((System.Windows.Media.SolidColorBrush)guide.Background).Color == System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3), "An already open guide follows theme changes.");
        }
        finally { CloseDesk(main); }
    }

    private static void TestCursorAfterGameExit(App app, bool enhanced)
    {
        var clientStart = Clients.Count;
        var main = NewDesk(app);
        try
        {
            main.Show();
            Pump();
            File.WriteAllText(Marker, "CgDesk regression client marker");
            var client = StartClient();
            var host = Get<GameHost>(main, "_mainHost");
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "Cursor fixture is connected.");
            if (enhanced)
            {
                ((System.Windows.Controls.CheckBox)main.FindName("EnhanceBox")).IsChecked = true;
                Wait(() => FsrSurface.Failed || FsrSurface.IsPresenting, "Cursor fixture starts FSR.");
                Require(FsrSurface.IsPresenting, $"Cursor exit regression exercises the real FSR layer. {FsrSurface.LastError}");
            }
            else
            {
                Wait(() => host.KeyTarget == client.Handle, "Native game input is ready.");
                GameCursorCloak.Sync(host.HostHandle, client.Handle);
            }
            var layerType = enhanced ? typeof(FsrSurface) : typeof(GameCursorCloak);
            var layer = (IntPtr)layerType.GetField("_hwnd", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            if (enhanced) Require(layer != IntPtr.Zero, "FSR cursor layer exists.");
            if (layer != IntPtr.Zero)
            {
                SendMessage(layer, 0x201, new IntPtr(1), new IntPtr((30 << 16) | 30));
                Require(GetCapture() == layer, "A held game click captures the pointer.");
                SendMessage(layer, 0x20, layer, new IntPtr(1));
            }
            else SetCursor(IntPtr.Zero);
            Require(GetCursor() == IntPtr.Zero, "The game exit starts with an invisible pointer.");
            PostMessage(client.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
            Wait(() => !NativeMethods.IsWindow(client.Handle), "Game exits before pointer cleanup.");
            Call(main, "RefreshStatus");
            Call(main, "SyncGameCloak");
            Require(layer != IntPtr.Zero ? GetCapture() != layer : GetCapture() == IntPtr.Zero, $"Game exit releases pointer capture (FSR={enhanced}).");
            Require(host.KeyTarget == IntPtr.Zero, $"Game exit clears stale input target (FSR={enhanced}).");
            Require(GetCursor() != IntPtr.Zero, $"Pointer returns immediately without leaving the desk (FSR={enhanced}).");
        }
        finally
        {
            CloseDesk(main);
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
            foreach (var client in Clients.Skip(clientStart))
            {
                if (!client.HasExited) { client.Kill(); client.WaitForExit(3000); }
                foreach (var kind in new[] { "mouse", "key" }) File.Delete(Path.Combine(AppContext.BaseDirectory, $"client-{client.Id}.{kind}"));
            }
            File.Delete(Marker);
        }
    }

    private static void TestSuperResolution(App app)
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
            ((System.Windows.Controls.CheckBox)main.FindName("EnhanceBox")).IsChecked = true;
            Wait(() => FsrSurface.IsPresenting || FsrSurface.Failed, "FSR pipeline reports a render result.");
            Console.WriteLine($"FSR presenting: {FsrSurface.IsPresenting}; failure: {FsrSurface.LastError}");
            if (!FsrSurface.IsPresenting)
            {
                foreach (var method in new[] { "StyleOf", "ExStyleOf" })
                    Console.WriteLine($"Capture source {method}: {typeof(GameEmbedder).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [client.Handle]):X}");
                Console.WriteLine($"Capture source ready: {GameEmbedder.IsReady(client.Handle)}; process {client.ProcessId}; handle {client.Handle:X}; visible {NativeMethods.IsWindowVisible(client.Handle)}");
            }
            Require(FsrSurface.IsPresenting, "Embedded client produces real FSR frames.");
            Wait(() => Get<bool>(main, "_upscaleLive"), "FSR output activates the expanded viewport.");
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
            var fsrWindow = (IntPtr)typeof(FsrSurface).GetField("_hwnd", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var center = new IntPtr(((FsrSurface.OutputSize.Height / 2) << 16) | (FsrSurface.OutputSize.Width / 2));
            PostMessage(fsrWindow, 0x201, new IntPtr(1), center);
            PostMessage(fsrWindow, 0x202, IntPtr.Zero, center);
            Wait(() => ReadFixtureInput(client.ProcessId, "mouse") == "400,300", "FSR forwards clicks to the correct coordinates in the native game.");
            PostMessage(host.HostHandle, 0x100, new IntPtr(0x41), new IntPtr(1));
            PostMessage(host.HostHandle, 0x101, new IntPtr(0x41), IntPtr.Zero);
            Wait(() => ReadFixtureInput(client.ProcessId, "key") == "65", "FSR mode forwards keyboard messages to the game.");
            var fsrGate = typeof(FsrSurface).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            lock (fsrGate)
                typeof(FsrSurface).GetMethod("Fail", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["Regression: capture interrupted"]);
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "Capture failure falls back to a usable native game window.");
            ((System.Windows.Controls.CheckBox)main.FindName("EnhanceBox")).IsChecked = false;
            Wait(() => GameEmbedder.IsAttached(client.Handle, host.HostHandle), "Disabling FSR restores native embedding.");
            Require(!FsrSurface.IsPresenting, "Disabling FSR hides the enlarged output.");
            ((System.Windows.Controls.CheckBox)main.FindName("EnhanceBox")).IsChecked = true;
            Wait(() => FsrSurface.Failed || FsrSurface.RenderedFrames >= 2, "FSR can be enabled again after a capture failure.");
            Require(!FsrSurface.Failed && FsrSurface.IsPresenting, "Retrying enhancement recovers real GPU frames.");
        }
        finally
        {
            CloseDesk(main);
            foreach (var client in Clients.Skip(clientStart)) if (!client.HasExited) { client.Kill(); client.WaitForExit(3000); }
            foreach (var client in Clients.Skip(clientStart))
                foreach (var kind in new[] { "mouse", "key" })
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

    private static void TestGuide(App app, bool enabled)
    {
        var main = NewDesk(app, enabled);
        var guide = Get<GuideWindow>(main, "_guideWindow");
        try
        {
            Require(new WindowInteropHelper(main).Handle == IntPtr.Zero && guide.Owner == null, "Construction defers owner until HWND exists.");
            main.Show();
            Pump();
            Require(main.IsLoaded && main.IsVisible, "Main window loads.");
            Require(ReferenceEquals(guide.Owner, main) && guide.IsVisible == enabled, $"Guide startup preference {enabled}.");
            guide.Hide();
            Call(main, "ShowGuideWindow", false);
            Pump();
            Require(guide.IsVisible && ReferenceEquals(guide.Owner, main), "Guide reopens with its owner.");
            Require(main.Width <= SystemParameters.WorkArea.Width && main.Height <= SystemParameters.WorkArea.Height, "Desk fits screen work area.");
            Require(((DockPanel)main.FindName("MainPane")).Visibility == Visibility.Collapsed, "No black native host before a game arrives.");
            Require(((StackPanel)main.FindName("EmptyState")).IsVisible, "Startup guidance is visible.");
        }
        finally { CloseDesk(main); }
        Require(!guide.IsVisible && app.Windows.Count == 0, "Closing the desk closes its guide.");
    }

    private static void TestConnection(App app)
    {
        var main = NewDesk(app);
        main.Show();
        Pump();
        File.WriteAllText(Marker, "CgDesk regression client marker");
        try
        {
            var first = StartClient();
            var host = Get<GameHost>(main, "_mainHost");
            Wait(() => GameEmbedder.IsAttached(first.Handle, host.HostHandle), "A client created after startup is automatically embedded by the timer.");
            Require(WindowNumber(main, first.Handle) == 1, "First client gets slot 1.");
            Require(((StackPanel)main.FindName("EmptyState")).Visibility == Visibility.Collapsed, "Guidance disappears after connection.");
            var primary = Get<IntPtr>(main, "_primaryHandle");
            Call(main, "FocusSeat", 8);
            Require(Get<IntPtr>(main, "_primaryHandle") == primary, "An unavailable hotkey slot never switches to a different client.");

            var second = StartClient();
            Wait(() => WindowNumber(main, second.Handle) == 2, "Second client is discovered.");
            Call(main, "FocusSeat", 1);
            Wait(() => GameEmbedder.IsAttached(second.Handle, host.HostHandle), "Selecting slot 2 swaps it into the real host.");
            PostMessage(first.Handle, 0x10, IntPtr.Zero, IntPtr.Zero);
            Wait(() => !NativeMethods.IsWindow(first.Handle), "First client closes independently.");
            Call(main, "RefreshStatus");
            Require(WindowNumber(main, second.Handle) == 2, "Closing client 1 does not renumber client 2.");
            Call(main, "FocusSeat", 0);
            Require(Get<IntPtr>(main, "_primaryHandle") == second.Handle, "A freed slot does not activate another client.");

            Call(main, "DockToCorner");
            Pump();
            Require(Get<bool>(main, "_docked") && ((WrapPanel)main.FindName("MiniWindows")).Children.Count == ReadSeats(main).Count, "Mini mode shows a card for every client, including the active one.");
            Require(!GameEmbedder.IsAttached(second.Handle, host.HostHandle), "Mini mode keeps game windows on the desktop.");
            Call(main, "ExpandFromDock");
            Wait(() => GameEmbedder.IsAttached(second.Handle, host.HostHandle), "Expanding mini mode automatically reconnects the active client.");

            main.WindowState = WindowState.Minimized;
            Pump();
            Require(main.ShowInTaskbar && main.IsVisible, "Minimize retains a taskbar entry.");
            main.WindowState = WindowState.Normal;
            Pump();
            Wait(() => GameEmbedder.IsAttached(second.Handle, host.HostHandle), "Restore retains the embedded client.");
            Wait(() => host.KeyTarget == second.Handle, "Restoring the desk reconnects its keyboard input target.");

            main.DisconnectWindow(second);
            Require(!GameEmbedder.IsAttached(second.Handle, host.HostHandle) && !GameEmbedder.HeldHandles.Contains(second.Handle), "Detach returns the client to the desktop.");
            _ = NativeMethods.GetWindowRect(second.Handle, out var restored);
            Require(Math.Abs(restored.Left - 40) <= 12 && Math.Abs(restored.Top - 40) <= 12, "Detach restores the client's original desktop position.");
            Call(main, "RefreshStatus");
            Require(!ReadSeats(main).Any(window => window.Handle == second.Handle), "Detached client is excluded from automatic reconnection.");

            File.Delete(Marker);
            var third = StartClient();
            Call(main, "RefreshStatus");
            Require(!ReadSeats(main).Any(window => window.Handle == third.Handle), "Unknown executables are not automatically captured.");
            Require(GameWindowScanner.Candidates(Environment.ProcessId).Any(window => window.Handle == third.Handle), "The picker lists an unrecognized client.");
            main.ConnectWindow(third);
            Wait(() => GameEmbedder.IsAttached(third.Handle, host.HostHandle), "Manual selection connects a client that automatic recognition misses.");
            Call(main, "RefreshStatus");
            Require(ReadSeats(main).Any(window => window.Handle == third.Handle), "Manually selected clients remain discoverable after parenting.");
            Require(!GameEmbedder.Place(IntPtr.Zero, host.HostHandle, 800, 600) && GameEmbedder.LastError.Length > 0, "Invalid embedding surfaces a diagnostic reason.");
        }
        finally
        {
            CloseDesk(main);
            foreach (var client in Clients) if (!client.HasExited) client.Kill();
        }
    }

    private static GameWindow StartClient()
        => StartClients(1)[0];

    private static List<GameWindow> StartClients(int count)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, $"--client --windows={count}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start fixture.");
        Clients.Add(process);
        var windows = new List<GameWindow>();
        for (var index = 0; index < count; index++)
        {
            var ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(10)) || !long.TryParse(ready.Result, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle))
                throw new InvalidOperationException("Fixture did not expose its HWND.");
            windows.Add(new GameWindow(new IntPtr(handle), process.Id, process.ProcessName, "CgDesk test client"));
        }
        return windows;
    }

    private static int RunClient(int count)
    {
        if (count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        var app = new System.Windows.Application();
        if (count == 1) { app.Run(CreateClientWindow(0)); return 0; }
        for (var index = 0; index < count; index++) CreateClientWindow(index).Show();
        app.Run();
        return 0;
    }

    private static Window CreateClientWindow(int index)
    {
        var liveText = new TextBlock { Text = "自动接入测试客户端", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var window = new Window
        {
            Title = $"CgDesk regression client {index + 1}", Width = 816, Height = 640, Left = 40 + index * 3, Top = 40 + index * 3,
            Content = liveText,
            Background = System.Windows.Media.Brushes.DarkSlateBlue, Foreground = System.Windows.Media.Brushes.White
        };
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var frame = 0;
        animation.Tick += (_, _) => liveText.Text = "自动接入测试客户端 · " + ++frame;
        animation.Start();
        window.SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(window);
            source.AddHook((IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled) =>
            {
                if (message == 0x201)
                {
                    var packed = unchecked((int)lparam.ToInt64());
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"client-{Environment.ProcessId}.mouse"), $"{(short)packed},{(short)(packed >> 16)}");
                }
                if (message == 0x100)
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"client-{Environment.ProcessId}.key"), wparam.ToInt64().ToString());
                return IntPtr.Zero;
            });
        };
        window.Loaded += (_, _) => { Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64().ToString("X")); Console.Out.Flush(); };
        return window;
    }

    private static void TestManyWindows(App app, bool enforceClipping)
    {
        var main = NewDesk(app);
        var measurements = new List<object>();
        try
        {
            main.Show();
            Pump();
            File.WriteAllText(Marker, "CgDesk regression client marker");
            var total = 0;
            foreach (var count in new[] { 4, 8, 12, 20 })
            {
                StartClients(count - total);
                total = count;
                Call(main, "RefreshStatus");
                Wait(() => ReadSeats(main).Count == count, $"Scanner discovers {count} independent client windows.");
                PumpFor(500);
                var strip = (StackPanel)main.FindName("ThumbStrip");
                var scroll = (ScrollViewer)main.FindName("ThumbScroller");
                Require(strip.Children.Count == count - 1, $"{count} windows produce one main view and {count - 1} preview cards.");
                var clip = WindowThumbs.ClientRect(new WindowInteropHelper(main).Handle, scroll);
                var overflow = WindowThumbs.ActiveDestinations.Count(rect => rect.Top < clip.Top || rect.Bottom > clip.Bottom || rect.Left < clip.Left || rect.Right > clip.Right);
                var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var elapsed = Stopwatch.StartNew();
                PumpFor(1500);
                process.Refresh();
                var row = new
                {
                    Windows = count, PreviewCards = strip.Children.Count,
                    VisibleCards = strip.Children.OfType<FrameworkElement>().Count(card =>
                    {
                        var bounds = card.TransformToAncestor(scroll).TransformBounds(new Rect(new Point(), card.RenderSize));
                        return bounds.Bottom > 0 && bounds.Top < scroll.ActualHeight;
                    }),
                    Scrollable = scroll.ScrollableHeight > 0, ScrollRange = Math.Round(scroll.ScrollableHeight),
                    ActiveThumbnails = WindowThumbs.ActiveCount, OverflowingThumbnails = overflow,
                    AppCpuPercent = Math.Round((process.TotalProcessorTime - cpu).TotalMilliseconds / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 2),
                    PrivateMemoryMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1), ProcessHandles = process.HandleCount
                };
                measurements.Add(row);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(row));
                if (enforceClipping) Require(overflow == 0, "Preview rendering stays inside its scroll viewport.");
            }
            var scroller = (ScrollViewer)main.FindName("ThumbScroller");
            scroller.ScrollToEnd();
            PumpFor(300);
            Require(scroller.VerticalOffset > 0, "The preview rail scrolls to its last client.");
            var previews = (StackPanel)main.FindName("ThumbStrip");
            ((System.Windows.Controls.Button)previews.Children[^1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpFor(300);
            Require(WindowNumber(main, Get<IntPtr>(main, "_primaryHandle")) == 20, "The last preview switches client 20 into the main view.");
            if (enforceClipping)
            {
                var clip = WindowThumbs.ClientRect(new WindowInteropHelper(main).Handle, scroller);
                Require(WindowThumbs.ActiveDestinations.All(rect => rect.Top >= clip.Top && rect.Bottom <= clip.Bottom && rect.Left >= clip.Left && rect.Right <= clip.Right), "Scrolled previews do not paint over the toolbar or footer.");
            }
            ((System.Windows.Controls.CheckBox)main.FindName("EnhanceBox")).IsChecked = true;
            Wait(() => FsrSurface.IsPresenting || FsrSurface.Failed, "FSR responds while all 20 clients are connected.");
            Require(FsrSurface.IsPresenting, $"The selected client renders FSR with 19 other previews. {FsrSurface.LastError}");
            Call(main, "DockToCorner");
            PumpFor(500);
            var mini = (ScrollViewer)main.FindName("MiniScroller");
            var miniCards = (System.Windows.Controls.WrapPanel)main.FindName("MiniWindows");
            Require(miniCards.Children.Count == 20 && mini.ScrollableHeight > 0, "Mini mode wraps all 20 clients into a scrolling grid.");
            var miniColumns = miniCards.Children.OfType<FrameworkElement>()
                .Select(card => card.TranslatePoint(new Point(), miniCards).X).Distinct().Count();
            Console.WriteLine($"Mini layout: {miniColumns} columns, {miniCards.Children.Count} cards, scroll range {mini.ScrollableHeight:F0} DIP.");
            mini.ScrollToEnd();
            PumpFor(300);
            var miniViewport = (FrameworkElement)mini.Template.FindName("PART_ScrollContentPresenter", mini);
            var miniClip = WindowThumbs.ClientRect(new WindowInteropHelper(main).Handle, miniViewport);
            Require(mini.VerticalOffset > 0 && WindowThumbs.ActiveCount > 0, "Mini mode scrolls to its last row with live previews.");
            if (enforceClipping) Require(WindowThumbs.ActiveDestinations.All(rect => rect.Top >= miniClip.Top && rect.Bottom <= miniClip.Bottom && rect.Left >= miniClip.Left && rect.Right <= miniClip.Right), "Mini previews stay inside the scrolled grid.");
            Call(main, "ExpandFromDock");
            PumpFor(300);
            Require(ReadSeats(main).Count == 20, "Expanding the mini console preserves all 20 clients.");
            Wait(() => FsrSurface.IsPresenting || FsrSurface.Failed, "Expanding the 20-client desk restarts FSR.");
            Require(FsrSurface.IsPresenting, $"The 20-client desk recovers live FSR after mini mode. {FsrSurface.LastError}");
            var reportDir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "qa");
            Directory.CreateDirectory(reportDir);
            File.WriteAllText(Path.Combine(reportDir, enforceClipping ? "multiwindow-after.json" : "multiwindow-before.json"),
                System.Text.Json.JsonSerializer.Serialize(measurements, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            CloseDesk(main);
            foreach (var client in Clients) if (!client.HasExited) { client.Kill(); client.WaitForExit(3000); }
            File.Delete(Marker);
        }
        Require(WindowThumbs.ActiveCount == 0, "Closing the desk releases every DWM thumbnail.");
    }

    private static void PumpFor(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(5); }
    }

    private static void TestSlotReuse()
    {
        var seats = new WindowSeats();
        var a = new GameWindow(new IntPtr(11), 1, "a", "a");
        var b = new GameWindow(new IntPtr(12), 2, "b", "b");
        seats.Update([a, b]);
        seats.Update([b]);
        Require(seats.Number(b.Handle) == 2, "Slot mapping survives removal of a preceding window.");
        seats.Update([b, a with { ProcessId = 3 }]);
        Require(seats.Number(b.Handle) == 2 && seats.Number(a.Handle) == 1, "A new client can use a free slot without renumbering others.");
    }

    private static void TestConfigPrivacy()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"LauncherPath\":\"launcher.exe\",\"Accounts\":[{\"Name\":\"old-user\"}],\"RememberPassword\":true}")!;
        var saved = System.Text.Json.JsonSerializer.Serialize(legacy);
        Require(legacy.LauncherPath == "launcher.exe" && !saved.Contains("old-user") && !saved.Contains("RememberPassword"), "Saving an older configuration drops retired account information and preserves the launcher path.");
    }

    private static void TestPreviewPlacement()
    {
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var bounds = new NativeMethods.RectNative { Left = 0, Top = 0, Right = (int)(200 * scale), Bottom = (int)(150 * scale) };
            var clip = bounds;
            clip.Top = (int)(50 * scale);
            Require(WindowThumbs.TryPlacement(bounds, 800, 600, clip, out var visible, out var source)
                && visible.Top == clip.Top && visible.Bottom == bounds.Bottom && source.Top == (int)Math.Floor(clip.Top * 600d / bounds.Bottom)
                && source.Bottom == 600 && source.Left == 0 && source.Right == 800,
                $"Partial preview cropping preserves source coordinates at {scale * 100}% geometry scale.");
        }
        var destination = new NativeMethods.RectNative { Right = 200, Bottom = 200 };
        Require(!WindowThumbs.TryPlacement(destination, 800, 600, new NativeMethods.RectNative { Left = 201, Right = 300, Bottom = 200 }, out _, out _), "A disjoint viewport hides the preview.");
        Require(!WindowThumbs.TryPlacement(destination, 0, 0, destination, out _, out _), "Unavailable source dimensions do not create a stretched preview.");
        Require(!WindowThumbs.TryPlacement(destination, 800, 600, new NativeMethods.RectNative { Right = 200, Bottom = 20 }, out _, out _), "A viewport intersecting only letterboxing hides the preview.");
    }

    private static void TestLauncherEncoding()
    {
        var root = Path.Combine(Path.GetTempPath(), "CgDesk-launcher-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var game = Path.Combine(root, "魔力宝贝");
            Directory.CreateDirectory(game);
            File.WriteAllText(Path.Combine(game, "Config.dat"), "test");
            File.WriteAllText(Path.Combine(game, "cg_se_3000.exe"), "test");
            File.WriteAllText(Path.Combine(root, "launcher.exe"), "test");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.GetEncoding(936), Encoding.Unicode })
            {
                File.WriteAllText(Path.Combine(root, "PlayOnline.ini"), $"[Path]\nGame100={game}\n", encoding);
                Require(LauncherInstall.TryLocate(Path.Combine(root, "launcher.exe"), out var found, out var exe)
                    && found == game && exe == Path.Combine(game, "cg_se_3000.exe"), $"Chinese launcher path loads with encoding {encoding.WebName}.");
                Require(GameInstallDirectory.FindDirectory(null, Path.Combine(root, "launcher.exe")) == game, $"Enhancement resolves the game directory with encoding {encoding.WebName}.");
            }
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(full, true);
        }
    }

    private static int WindowNumber(MainWindow main, IntPtr handle) => (int)Call(main, "WindowNumber", handle)!;
    private static List<GameWindow> ReadSeats(MainWindow main) => (List<GameWindow>)Call(main, "Seats", new object[] { null! })!;
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Field(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void CloseDesk(MainWindow main)
    {
        Field(main, "_allowClose", true);
        main.Close();
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> condition, string description)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(20); }
        Require(condition(), description);
    }
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description + $" Embed error: {GameEmbedder.LastError}");
        _checks++;
        Console.WriteLine("PASS: " + description);
    }
    private static void TestScaledMouse()
    {
        var root = SourceRoot();
        var project = Path.Combine(root, "tests", "DpiMouseChild", "DpiMouseChild.csproj");
        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release --nologo")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        build.WaitForExit();
        Require(build.ExitCode == 0, "The DPI-unaware mouse child builds. " + build.StandardError.ReadToEnd());
        var exe = Path.Combine(root, "tests", "DpiMouseChild", "bin", "Release", "net10.0", "DpiMouseChild.exe");
        var child = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var header = child.StandardOutput.ReadLine() ?? "";
            var hwnd = new IntPtr(long.Parse(header.Split(' ')[0].Split('=')[1], CultureInfo.InvariantCulture));
            var logical = header.Split(' ')[1].Split('=')[1].Split('x');
            var logicalW = int.Parse(logical[0], CultureInfo.InvariantCulture);
            var logicalH = int.Parse(logical[1], CultureInfo.InvariantCulture);
            _ = NativeMethods.GetClientRect(hwnd, out var physicalRect);
            var physicalW = physicalRect.Right - physicalRect.Left;
            var physicalH = physicalRect.Bottom - physicalRect.Top;
            var posted = FsrSurface.MapPointer(physicalW / 2, physicalH / 2, physicalW, physicalH, physicalW, physicalH);
            Require(PostMessage(hwnd, 0x201, new IntPtr(1), posted), "Scaled click is delivered to the unaware window.");
            var got = (child.StandardOutput.ReadLine() ?? "").Split('=')[1].Split(',');
            var gotX = int.Parse(got[0], CultureInfo.InvariantCulture);
            var gotY = int.Parse(got[1], CultureInfo.InvariantCulture);
            Console.WriteLine($"Scaled mouse: physical {physicalW}x{physicalH}, logical {logicalW}x{logicalH}, delivered {gotX},{gotY}");
            Require(Math.Abs(gotX - logicalW / 2) <= 2 && Math.Abs(gotY - logicalH / 2) <= 2, "A centered click lands on the center of a DPI-unaware game.");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CgDesk.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr GetCursor();
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
}
