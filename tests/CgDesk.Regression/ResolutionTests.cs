using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CgDesk;
using CgDesk.Models;
using CgDesk.Services;

internal static partial class Program
{
    private static void TestLaunchResolutions(App app)
    {
        foreach (var monitor in new[] { (640,480), (1366,768), (1920,1080), (1080,1920), (3840,2160) })
        {
            var modes = LaunchResolutions.ForMonitor(monitor.Item1, monitor.Item2);
            Require(modes[0].Native, "Native launch remains available to disable the experiment.");
            var defaults = new AppConfig();
            Require(LaunchResolutions.Select(defaults, modes) == modes.Last() && !modes.Last().Native,
                "New configuration defaults to the largest 4:3 resolution fitting the monitor.");
            Require(modes.All(mode => mode.Width <= monitor.Item1 && mode.Height <= monitor.Item2), "Choices fit both axes of the current monitor.");
        }
        var fullHd = LaunchResolutions.ForMonitor(1920,1080);
        Require(fullHd.All(mode => mode.Native || mode.Width * 3 == mode.Height * 4), "All selectable resolutions are exactly 4:3.");
        Require(!fullHd.Any(mode => mode.Width > 1600 || mode.Height > 1200), "Resolution catalog never exceeds 1600x1200.");
        Require(LaunchResolutions.ForMonitor(2560,1440).Any(mode => mode.Width == 1600 && mode.Height == 1200)
            && fullHd.Any(mode => mode.Width == 1440 && mode.Height == 1080), "4:3 modes reach 1600x1200 on larger monitors and 1440x1080 on Full HD.");
        Require(!LaunchResolutions.Supported(1920,1080) && !LaunchResolutions.Supported(1920,1440), "Wide or above-cap modes are rejected.");
        foreach (var available in new[] { (2345,1721),(800,600),(791,550),(3200,2400) })
        {
            var fitted = LaunchResolutions.FitFourByThree(available.Item1,available.Item2);
            Require(fitted.Width * 3 == fitted.Height * 4 && fitted.Width <= available.Item1 && fitted.Height <= available.Item2,
                "FSR display fits the pane with exact 4:3 and no stretch.");
        }
        var fit = LaunchResolutions.FitDesk(1024,768,280,160,1920,1040);
        Require(fit == (1304,928), "Desk size includes the game and actual surrounding controls.");
        var small = LaunchResolutions.FitDesk(1366,768,280,160,1280,720);
        Require(small == (1280,720), "Oversized desk stays inside the monitor work area.");
        var legacy = JsonSerializer.Deserialize<AppConfig>("{\"EnhanceMainWindow\":true,\"EnhanceSharpness\":0.9}")!;
        Require(LaunchResolutions.Select(legacy, fullHd).Width == 1440 && !JsonSerializer.Serialize(legacy).Contains("Enhance"),
            "Old enhancement settings migrate to enabled resolution default without retaining enhancement fields.");
        var previousDefault = JsonSerializer.Deserialize<AppConfig>("{\"LaunchWidth\":0,\"LaunchHeight\":0}")!;
        Require(!LaunchResolutions.Select(previousDefault, fullHd).Native, "Previous default-off configuration migrates once to enabled mode.");
        previousDefault.LaunchWidth = previousDefault.LaunchHeight = 0;
        var explicitNative = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(previousDefault))!;
        Require(LaunchResolutions.Select(explicitNative, fullHd).Native, "Explicitly disabling the experiment survives save and restart.");
        var manual = new AppConfig { LaunchWidth = 1024, LaunchHeight = 768 };
        Require(LaunchResolutions.Select(manual, fullHd).Width == 1024, "An existing manually selected valid resolution is preserved.");
        var automatic = new AppConfig();
        Require(LaunchResolutions.Select(automatic, fullHd).Width == 1440
            && LaunchResolutions.Select(automatic, LaunchResolutions.ForMonitor(1366,768)).Width == 1024
            && LaunchResolutions.Select(automatic, LaunchResolutions.ForMonitor(2560,1440)).Width == 1600,
            "Automatic default tracks monitor changes without persisting an intermediate screen size.");

        var folder = Directory.CreateTempSubdirectory("cgdesk-resolution-").FullName;
        try
        {
            var client = Path.Combine(folder,"cg_se_3000.exe");
            var resource = Path.Combine(folder,"Config.dat");
            File.WriteAllText(client,"fixture: never executed");
            File.WriteAllText(resource,"fixture resource");
            var before = File.ReadAllBytes(client);
            var resourceBefore = File.ReadAllBytes(resource);
            var alias = Path.Combine(folder,"fixture-system.dll");
            File.WriteAllText(alias,"fixture alias: never executed");
            var running = false;
            var files = new ViewportLaunchFiles(system:alias,
                clientHash:Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant(), running:() => running);
            files.Prepare(client,new(1024,768));
            var profile = Path.Combine(folder,"cgdesk_viewport.ini");
            var diagnosticLog = Path.Combine(folder,"cgdesk-graphics-state.jsonl");
            files.Prepare(client,new(1024,768),diagnosticLog);
            Require(File.ReadAllText(profile).Contains("DiagnosticPath=" + diagnosticLog) && !File.Exists(diagnosticLog),
                "Explicit diagnostics configure a local log without installing or writing client resources.");
            files.Prepare(client,new(1024,768));
            Require(!File.ReadAllText(profile).Contains("DiagnosticPath="), "Normal launch leaves graphics diagnostics disabled.");
            var originalProfile = File.ReadAllText(profile);
            Require(originalProfile.Contains("Width=1024") && originalProfile.Contains("Height=768"), "Launch writes the selected size to its own component profile.");
            Require(File.ReadAllBytes(client).SequenceEqual(before) && File.ReadAllBytes(resource).SequenceEqual(resourceBefore), "Installation leaves the original EXE and resource bytes unchanged.");
            running = true;
            files.Prepare(client,new(1024,768));
            var blocked = false;
            try { files.Prepare(client,new(1152,864)); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked && File.ReadAllText(profile) == originalProfile, "Running clients prevent profile changes while identical launches can be reused.");
            running = false;
            File.AppendAllText(profile,"foreign change");
            blocked = false;
            try { files.Prepare(client,new(0,0)); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked && File.Exists(profile), "Changed component files are not silently removed.");
            File.WriteAllText(profile,originalProfile);
            files.Prepare(client,new(0,0));
            Require(!File.Exists(profile) && !File.Exists(Path.Combine(folder,"ddraw.dll")) && !File.Exists(Path.Combine(folder,"cgdesk_viewport.install.json")), "Native selection removes only the verified owned additions.");
            File.WriteAllText(Path.Combine(folder,"ddraw.dll"),"foreign renderer");
            blocked = false;
            try { files.Prepare(client,new(1024,768)); } catch (InvalidOperationException) { blocked = true; }
            Require(blocked && File.ReadAllText(Path.Combine(folder,"ddraw.dll")) == "foreign renderer", "Foreign DirectDraw files are never overwritten.");
        }
        finally { Directory.Delete(folder,true); }

        var main = NewDesk(app);
        var clientStart = Clients.Count;
        try
        {
            main.Show(); Pump();
            var picker = (ComboBox)main.FindName("ResolutionBox");
            Require(picker.SelectedItem is LaunchResolution { Native:false }, "Launcher UI starts with experimental resolution enabled.");
            var hint = (TextBlock)main.FindName("ResolutionHintText");
            Require(hint.Text.Contains("被检测或封号风险"), "Enabled default retains the visible detection and account risk warning.");
            picker.SelectedItem = ((IEnumerable<LaunchResolution>)picker.ItemsSource).First(mode => mode.Native);
            Require(main.Config.LaunchWidth == 0 && main.Config.LaunchResolutionSettingsVersion == 1,
                "Selecting native mode records an explicit opt-out.");
            Require(main.FindName("EnhanceBox") == null && main.FindName("SharpnessSlider") == null, "Enhancement controls are removed.");
            File.WriteAllText(Marker,"CgDesk resolution fixture");
            var client = StartClient();
            var host = Get<GameHost>(main,"_mainHost");
            Wait(() => GameEmbedder.IsAttached(client.Handle,host.HostHandle),"Resolution fixture connects.");
            NativeMethods.SetWindowPos(client.Handle,IntPtr.Zero,0,0,1024,768,NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder);
            Call(main,"RefreshStatus");
            Wait(() => ((FrameworkElement)main.FindName("MainViewport")).ActualWidth * VisualTreeHelper.GetDpi(main).DpiScaleX >= 1023,
                "A 1024x768 native client expands the desk viewport without FSR.");
            var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(main).Handle);
            Require(main.ActualWidth * VisualTreeHelper.GetDpi(main).DpiScaleX <= screen.WorkingArea.Width + 2,
                "Automatically resized desk stays within the current monitor.");
            NativeMethods.SetWindowPos(client.Handle,IntPtr.Zero,0,0,1600,1200,NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder);
            Call(main,"RefreshStatus");
            Wait(() => ((FrameworkElement)main.FindName("MainViewport")).ActualWidth * VisualTreeHelper.GetDpi(main).DpiScaleX >= 1599,
                "A 1600x1200 native client retains its full viewport in the desk.");
            Require(main.ActualWidth * VisualTreeHelper.GetDpi(main).DpiScaleX <= screen.WorkingArea.Width + 2
                && main.ActualHeight * VisualTreeHelper.GetDpi(main).DpiScaleY <= screen.WorkingArea.Height + 2,
                "The desk containing a 1600x1200 client is clamped on both monitor axes.");
        }
        finally
        {
            CloseDesk(main);
            foreach (var client in Clients.Skip(clientStart)) if (!client.HasExited) { client.Kill(); client.WaitForExit(3000); }
            File.Delete(Marker);
        }
    }
}
