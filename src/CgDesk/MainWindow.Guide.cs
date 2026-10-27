using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using CgDesk.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CgDesk;

public partial class MainWindow
{
    private const string GuideOrigin = "https://www.molibaike.com";
    private const string GuideHome = GuideOrigin + "/Mission";
    private const string GuideRuntimeX64 = "https://go.microsoft.com/fwlink/?linkid=2124701";
    private const string GuideRuntimeX86 = "https://go.microsoft.com/fwlink/?linkid=2099617";
    private const string GuideRuntimeArm64 = "https://go.microsoft.com/fwlink/?linkid=2099616";
    private const string GuideFitScript = """
        (() => {
          const apply = () => {
            const host = location.hostname || "";
            if (host !== "molibaike.com" && !host.endsWith(".molibaike.com")) return;
            if (!document.getElementById("cgdesk-guide-fit")) {
              const style = document.createElement("style");
              style.id = "cgdesk-guide-fit";
              style.textContent = `
                .topbar, #headerPic, .nav-ads-swiper, #navSearchBar, footer.footer,
                a:has(img[src*="/images/ads/"]) { display: none !important; }
                body { padding-top: 0 !important; }
                .position-sticky, .main-navbar-wrap { position: static !important; top: auto !important; }
                .container-fixed { width: auto !important; max-width: 100% !important; min-width: 0 !important; }
                #subContainer, .main-navbar-wrap, .main-navbar, .position-sticky, .container-fixed {
                  overflow: visible !important;
                }
                .main-navbar > li:hover > .sub-nav { display: block !important; z-index: 50 !important; }
                .main { margin-bottom: 8px !important; }
                .main img { max-width: 100% !important; height: auto !important; }
              `;
              (document.head || document.documentElement).appendChild(style);
            }
          };
          if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", apply);
          else apply();
        })();
        """;

    private static readonly string[] GuideCategories =
    [
        "Mission", "Article", "Item", "Monster", "Role", "Map", "Profession", "Skill", "MonsterSkill", "Npc"
    ];
    private string _guideCategory = "Mission";

    private bool _guideStarted;
    private bool _guideReady;
    private bool _guideClosed;
    private bool _guideInstalling;
    private string? _guidePending;
    private string? _guideSetupError;
    private CancellationTokenSource? _guideSetupCancel;

    private WebView2 GuideView => _guideWindow.GuideView;
    private System.Windows.Controls.TextBlock GuideStateText => _guideWindow.GuideStateText;
    private System.Windows.Controls.Border GuideFallback => _guideWindow.GuideFallback;
    private System.Windows.Controls.TextBox GuideSearchBox => _guideWindow.GuideSearchBox;
    private System.Windows.Controls.Button GuideBackButton => _guideWindow.GuideBackButton;
    private System.Windows.Controls.Button GuideForwardButton => _guideWindow.GuideForwardButton;
    private System.Windows.Controls.Button GuideReloadButton => _guideWindow.GuideReloadButton;
    private System.Windows.Controls.Button GuideHomeButton => _guideWindow.GuideHomeButton;
    private System.Windows.Controls.TextBlock GuideFallbackText => _guideWindow.GuideFallbackText;
    private System.Windows.Controls.Button GuideRuntimeButton => _guideWindow.GuideRuntimeButton;
    private System.Windows.Controls.Button GuideRetryButton => _guideWindow.GuideRetryButton;

    private void ApplyGuideSurface()
    {
        GuideView.DefaultBackgroundColor = DeskTheme.Dark
            ? System.Drawing.Color.FromArgb(255, 0x1F, 0x1F, 0x1F)
            : System.Drawing.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
    }

    private void PrepareGuide() => ApplyGuideSurface();

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Owner requires the main window's native handle, which is not created in its constructor.
        _guideWindow.Attach(this);
        if (_config.ShowGuide)
        {
            ShowGuideWindow(false);
        }
    }

    private async Task StartGuideAsync()
    {
        if (_guideStarted || _guideClosed)
        {
            return;
        }

        _guideStarted = true;
        try
        {
            var browserFolder = await PrepareGuideBrowserAsync();
            if (_guideClosed)
            {
                return;
            }

            if (browserFolder == GuideBrowserMissing)
            {
                var message = string.IsNullOrWhiteSpace(EnvBarText.Text)
                    ? "攻略组件没有就绪，页面打不开。"
                    : EnvBarText.Text;
                ShowGuideFallback(message, true);
                return;
            }

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CgDesk", "WebView2");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(browserFolder, folder);
            if (_guideClosed)
            {
                return;
            }

            await GuideView.EnsureCoreWebView2Async(environment);
            if (_guideClosed || GuideView.CoreWebView2 == null)
            {
                return;
            }

            var core = GuideView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(GuideFitScript);
            if (_guideClosed || GuideView.CoreWebView2 == null)
            {
                return;
            }

            core.NavigationStarting += OnGuideNavigationStarting;
            core.NavigationCompleted += OnGuideNavigationCompleted;
            core.NewWindowRequested += OnGuideNewWindow;
            core.HistoryChanged += (_, _) => UpdateGuideNav();
            core.DocumentTitleChanged += (_, _) => UpdateGuideTitle();
            _guideReady = true;
            GuideReloadButton.IsEnabled = true;
            GuideHomeButton.IsEnabled = true;
            var start = _guidePending ?? GuideHome;
            _guidePending = null;
            core.Navigate(start);
        }
        catch (Exception ex)
        {
            if (_guideClosed)
            {
                return;
            }

            ShowGuideFallback("攻略页面没有打开。" + ex.Message, false);
        }
    }

    private const string GuideBrowserMissing = "\0";

    private static string? BundledBrowserFolder()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "WebView2");
        return File.Exists(Path.Combine(folder, "msedgewebview2.exe")) ? folder : null;
    }

    private static string? BrowserVersion(string? browserFolder)
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(browserFolder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> PrepareGuideBrowserAsync()
    {
        var bundled = BundledBrowserFolder();
        if (!string.IsNullOrEmpty(BrowserVersion(bundled)))
        {
            HideEnvBar();
            return bundled;
        }

        if (!string.IsNullOrEmpty(BrowserVersion(null)))
        {
            HideEnvBar();
            return null;
        }

        if (_guideClosed)
        {
            return GuideBrowserMissing;
        }

        _guideSetupCancel?.Dispose();
        _guideSetupCancel = new CancellationTokenSource();
        var token = _guideSetupCancel.Token;
        _guideInstalling = true;
        _guideSetupError = null;
        ShowEnvBar("缺少攻略运行环境，正在自动下载。如果弹出权限确认，点确定后会继续，进度显示在这里。", null);
        try
        {
            string? installer;
            try
            {
                installer = await GetGuideInstallerAsync(token);
            }
            catch (OperationCanceledException)
            {
                return GuideBrowserMissing;
            }

            if (_guideClosed)
            {
                return GuideBrowserMissing;
            }

            if (installer == null)
            {
                ShowEnvFailure(_guideSetupError ?? "没能下载攻略组件。请联网后点重新安装。");
                return GuideBrowserMissing;
            }

            ShowEnvBar("正在安装攻略组件。如果弹出权限确认，点确定后会继续。窗口台没有卡住。", null);
            int code;
            try
            {
                code = await RunGuideInstallerAsync(installer, token);
            }
            catch (OperationCanceledException)
            {
                return GuideBrowserMissing;
            }
            catch (Exception ex)
            {
                ShowEnvFailure("安装程序没有启动。" + ex.Message);
                return GuideBrowserMissing;
            }

            if (_guideClosed)
            {
                return GuideBrowserMissing;
            }

            if (GuideInstallSucceeded(code))
            {
                for (var i = 0; i < 8 && string.IsNullOrEmpty(BrowserVersion(null)); i++)
                {
                    if (_guideClosed)
                    {
                        return GuideBrowserMissing;
                    }

                    UpdateEnvText("正在确认攻略组件是否已经安装好。");
                    await Task.Delay(500);
                }
            }

            if (!GuideInstallSucceeded(code) || string.IsNullOrEmpty(BrowserVersion(null)))
            {
                ShowEnvFailure(GuideInstallSucceeded(code)
                    ? "安装已结束，但攻略组件仍不可用。可以点重新安装。"
                    : "攻略组件没有装上（代码 " + code + "）。可以点重新安装。");
                return GuideBrowserMissing;
            }

            HideEnvBar();
            StatusText.Text = "攻略组件已安装。";
            GuideStateText.Text = "正在打开…";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_guideClosed)
            {
                ShowEnvFailure("攻略组件没有装上。" + ex.Message);
            }

            return GuideBrowserMissing;
        }
        finally
        {
            _guideInstalling = false;
        }
    }

    private void WarnMissingGuideRuntime()
    {
        if (!string.IsNullOrEmpty(BrowserVersion(BundledBrowserFolder())) || !string.IsNullOrEmpty(BrowserVersion(null)))
        {
            return;
        }

        ShowEnvBar("缺少攻略运行环境，正在准备下载安装。如果刚才确认了权限，进度会显示在这里。", null);
    }

    private async Task<string?> GetGuideInstallerAsync(CancellationToken token)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CgDesk", "setup");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, GuideInstallerFileName());
        if (IsInstallerFile(target))
        {
            return target;
        }

        var partial = target + ".partial";
        try
        {
            await DownloadGuideInstallerAsync(GuideInstallerUrl(), partial, target, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            _guideSetupError = "没能下载攻略组件。" + ex.Message;
        }

        if (IsInstallerFile(target))
        {
            return target;
        }

        var setup = Path.Combine(AppContext.BaseDirectory, "MicrosoftEdgeWebview2Setup.exe");
        if (File.Exists(setup))
        {
            _guideSetupError = null;
            return setup;
        }

        return null;
    }

    private async Task DownloadGuideInstallerAsync(string url, string partial, string target, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CgDesk");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            var buffer = new byte[81920];
            long got = 0;
            var lastUi = 0L;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), token);
                got += read;
                var now = Environment.TickCount64;
                if (now - lastUi < 150)
                {
                    continue;
                }

                lastUi = now;
                ShowDownloadProgress(got, total);
            }
        }

        if (!IsInstallerFile(partial))
        {
            TryDelete(partial);
            throw new InvalidDataException("下载到的文件不能安装。");
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(partial, target);
        ShowDownloadProgress(new FileInfo(target).Length, total);
    }

    private async Task<int> RunGuideInstallerAsync(string installer, CancellationToken token)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/silent /install",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (process == null)
        {
            throw new InvalidOperationException("没有拿到安装进程。");
        }

        using (process)
        {
            var started = Environment.TickCount64;
            var wait = process.WaitForExitAsync(token);
            while (!wait.IsCompleted)
            {
                var seconds = (Environment.TickCount64 - started) / 1000;
                UpdateEnvText("正在安装攻略组件，已进行 " + seconds + " 秒。窗口台没有卡住，请稍候。");
                await Task.WhenAny(wait, Task.Delay(1000));
            }

            await wait;
            return process.ExitCode;
        }
    }

    private void UpdateEnvText(string text)
    {
        if (_guideClosed)
        {
            return;
        }

        EnvBarText.Text = text;
        GuideStateText.Text = text;
        StatusText.Text = text;
    }

    private void ShowDownloadProgress(long got, long? total)
    {
        var done = got / 1048576d;
        if (total is > 0)
        {
            var all = total.Value / 1048576d;
            ShowEnvBar($"正在下载攻略组件 {done:0.0}/{all:0.0} MB", got * 100d / total.Value);
            return;
        }

        ShowEnvBar($"正在下载攻略组件，已下载 {done:0.0} MB", null);
    }

    private void ShowEnvBar(string text, double? percent)
    {
        if (_guideClosed)
        {
            return;
        }

        EnvBar.Visibility = Visibility.Visible;
        EnvRetryButton.Visibility = Visibility.Collapsed;
        EnvBarText.Text = text;
        GuideStateText.Text = text;
        StatusText.Text = text;
        if (percent is double value)
        {
            EnvBarProgress.IsIndeterminate = false;
            EnvBarProgress.Value = Math.Clamp(value, 0, 100);
            return;
        }

        EnvBarProgress.IsIndeterminate = true;
    }

    private void ShowEnvFailure(string text)
    {
        _guideSetupError = text;
        ShowEnvBar(text, 0);
        if (_guideClosed)
        {
            return;
        }

        EnvRetryButton.Visibility = Visibility.Visible;
    }

    private void HideEnvBar()
    {
        EnvBar.Visibility = Visibility.Collapsed;
        EnvRetryButton.Visibility = Visibility.Collapsed;
        EnvBarProgress.IsIndeterminate = false;
    }

    private static string GuideInstallerUrl()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => GuideRuntimeArm64,
            Architecture.X86 => GuideRuntimeX86,
            _ => GuideRuntimeX64
        };
    }

    private static string GuideInstallerFileName()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "WebView2Runtime-arm64.exe",
            Architecture.X86 => "WebView2Runtime-x86.exe",
            _ => "WebView2Runtime-x64.exe"
        };
    }

    private static bool GuideInstallSucceeded(int code) => code == 0 || code == 3010;

    private static bool IsInstallerFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 100_000)
            {
                return false;
            }

            using var stream = info.OpenRead();
            Span<byte> magic = stackalloc byte[2];
            return stream.Read(magic) == 2 && magic[0] == (byte)'M' && magic[1] == (byte)'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    internal void GuideSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        SearchGuide();
    }

    internal void GuideSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchGuide();
    }

    internal void GuideHome_Click(object sender, RoutedEventArgs e)
    {
        NavigateGuide(GuideListUrl(false));
    }

    internal void GuideBack_Click(object sender, RoutedEventArgs e)
    {
        if (GuideView.CoreWebView2?.CanGoBack == true)
        {
            GuideView.CoreWebView2.GoBack();
        }
    }

    internal void GuideForward_Click(object sender, RoutedEventArgs e)
    {
        if (GuideView.CoreWebView2?.CanGoForward == true)
        {
            GuideView.CoreWebView2.GoForward();
        }
    }

    internal void GuideReload_Click(object sender, RoutedEventArgs e)
    {
        GuideView.CoreWebView2?.Reload();
    }

    internal void GuideOpenExternal_Click(object sender, RoutedEventArgs e)
    {
        var url = GuideView.CoreWebView2?.Source;
        if (!IsGuideUri(url))
        {
            url = GuideHome;
        }

        OpenGuideExternal(url);
    }

    internal async void GuideRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (_guideInstalling || _guideClosed)
        {
            return;
        }

        _guideStarted = false;
        GuideFallback.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        await StartGuideAsync();
    }

    private void SearchGuide()
    {
        NavigateGuide(GuideListUrl(true));
    }

    private string GuideListUrl(bool includeKey)
    {
        var url = GuideOrigin + "/" + _guideCategory;
        if (!includeKey)
        {
            return url;
        }

        var key = GuideSearchBox.Text.Trim();
        return key.Length == 0 ? url : url + "?key=" + Uri.EscapeDataString(key);
    }

    private void NavigateGuide(string url)
    {
        if (_guideClosed || GuideFallback.Visibility == Visibility.Visible)
        {
            return;
        }

        GuideStateText.Text = "正在打开…";
        if (!_guideReady || GuideView.CoreWebView2 == null)
        {
            _guidePending = url;
            return;
        }

        GuideView.CoreWebView2.Navigate(url);
    }

    private void OnGuideNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsGuideUri(e.Uri))
        {
            GuideStateText.Text = "正在打开…";
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            OpenGuideExternal(e.Uri);
        }
    }

    private void OnGuideNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateGuideNav();
        if (e.IsSuccess)
        {
            GuideFallback.Visibility = Visibility.Collapsed;
            GuideView.Visibility = Visibility.Visible;
            GuideReloadButton.IsEnabled = true;
            GuideHomeButton.IsEnabled = true;
            UpdateGuideTitle();
            SyncGuideCategory(GuideView.CoreWebView2?.Source);
            _ = FitGuidePageAsync();
            return;
        }

        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        ShowGuideFallback("页面暂时无法打开。请检查网络后重试，或用系统浏览器打开。", false);
    }

    private void OnGuideNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsGuideUri(e.Uri))
        {
            NavigateGuide(e.Uri);
            return;
        }

        OpenGuideExternal(e.Uri);
    }

    private async Task FitGuidePageAsync()
    {
        var core = GuideView.CoreWebView2;
        if (core == null || !IsGuideUri(core.Source))
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(GuideFitScript);
        }
        catch (Exception)
        {
            // 页面已经换走时，补样式没有意义。
        }
    }

    private void UpdateGuideNav()
    {
        var core = GuideView.CoreWebView2;
        GuideBackButton.IsEnabled = core?.CanGoBack == true;
        GuideForwardButton.IsEnabled = core?.CanGoForward == true;
    }

    private void UpdateGuideTitle()
    {
        var title = GuideView.CoreWebView2?.DocumentTitle;
        GuideStateText.Text = string.IsNullOrWhiteSpace(title) ? "魔力百科" : title;
        GuideStateText.ToolTip = GuideStateText.Text;
    }

    private void SyncGuideCategory(string? source)
    {
        if (!IsGuideUri(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return;
        }

        var slug = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (slug == null)
        {
            return;
        }

        var category = GuideCategories.FirstOrDefault(value => string.Equals(value, slug, StringComparison.OrdinalIgnoreCase));
        if (category != null) _guideCategory = category;
    }

    private void ShowGuideFallback(string message, bool offerRuntime)
    {
        GuideView.Visibility = Visibility.Collapsed;
        GuideFallback.Visibility = Visibility.Visible;
        GuideFallbackText.Text = message;
        GuideRuntimeButton.Visibility = offerRuntime ? Visibility.Visible : Visibility.Collapsed;
        GuideRetryButton.Visibility = offerRuntime ? Visibility.Collapsed : Visibility.Visible;
        GuideStateText.Text = "攻略页面没有打开";
        GuideBackButton.IsEnabled = false;
        GuideForwardButton.IsEnabled = false;
        GuideReloadButton.IsEnabled = false;
        GuideHomeButton.IsEnabled = false;
    }

    internal void RetryGuide()
    {
        GuideFallback.Visibility = Visibility.Collapsed;
        GuideView.Visibility = Visibility.Visible;
        if (GuideView.CoreWebView2 is { } core)
        {
            core.Reload();
            return;
        }
        _guideStarted = false;
        _ = StartGuideAsync();
    }

    private void OpenGuideExternal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            GuideStateText.Text = "系统浏览器没有打开";
        }
    }

    private static bool IsGuideUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var host = parsed.IdnHost;
        return host.Equals("molibaike.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".molibaike.com", StringComparison.OrdinalIgnoreCase);
    }

}
