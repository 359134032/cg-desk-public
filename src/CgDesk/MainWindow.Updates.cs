using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CgDesk.Models;
using CgDesk.Services;

namespace CgDesk;

public partial class MainWindow
{
    private UpdateService? _updateService;
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _updateDownloadCancel;
    private DispatcherTimer? _updateTimer;
    private AvailableUpdate? _availableUpdate;
    private string? _downloadedUpdate;
    private bool _checkingUpdates;
    private bool _updatesClosed;
    private string _lastUpdateNotification = "";
    private int _updateSourceGeneration;

    private void InitializeUpdateChecks()
    {
        if (_config.UpdateChannel is not ("stable" or "beta")) _config.UpdateChannel = "stable";
        // Regression desks never read deployment files or contact public endpoints automatically.
        if (_persistConfiguration) LoadDeploymentUpdateSources();
        UpdateSourceDefaults.Apply(_config);
        UpdateChecksBox.IsChecked = _config.UpdateChecksEnabled;
        RefreshUpdateSources();
        Loaded += async (_, _) =>
        {
            if (!_persistConfiguration || _updatesClosed || _updateTimer != null) return;
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
            _updateTimer.Start();
            try { await Task.Delay(TimeSpan.FromSeconds(8), _updateLifetime.Token); await CheckForUpdatesAsync(false); }
            catch (OperationCanceledException) { }
        };
    }

    private void LoadDeploymentUpdateSources()
    {
        if (!UpdateSourceDefaults.NeedsMigration(_config.UpdateManifestUrl)) return;
        var path = Path.Combine(AppContext.BaseDirectory, "update-sources.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return;
            var sources = JsonSerializer.Deserialize<DeploymentSources>(File.ReadAllText(path), UpdateService.Json);
            if (sources == null || string.IsNullOrWhiteSpace(sources.Primary)) return;
            UpdateService.PublicHttps(sources.Primary);
            if (!string.IsNullOrWhiteSpace(sources.Backup)) UpdateService.PublicHttps(sources.Backup);
            _config.UpdateManifestUrl = sources.Primary;
            _config.UpdateBackupManifestUrl = sources.Backup ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or UpdateException) { }
    }

    private void RefreshUpdateSources()
    {
        var configured = !string.IsNullOrWhiteSpace(_config.UpdateManifestUrl);
        var channel = _config.UpdateChannel == "beta" ? "测试版" : "稳定版";
        UpdateSourceText.Text = configured ? $"{channel} · 主源：{SourceHost(_config.UpdateManifestUrl)}" : "尚未配置更新源，不会发起网络检查";
        if (configured && !string.IsNullOrWhiteSpace(_config.UpdateBackupManifestUrl)) UpdateSourceText.Text += $"\n备用：{SourceHost(_config.UpdateBackupManifestUrl)}";
    }
    private static string SourceHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "地址无效";

    private void UpdateChecks_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.UpdateChecksEnabled = UpdateChecksBox.IsChecked == true;
        SaveConfig();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(true);
    private void UpdateSources_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdates || _updateDownloadCancel != null) { UpdateStatusText.Text = "请等待检查完成，或取消下载后再调整更新源。"; return; }
        var dialog = new UpdateSettingsWindow(_config) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _config.UpdateManifestUrl = dialog.Primary;
        _config.UpdateBackupManifestUrl = dialog.Backup;
        _config.UpdateChannel = dialog.Channel;
        _updateSourceGeneration++;
        _config.NextUpdateCheckUtc = default;
        _availableUpdate = null; _downloadedUpdate = null;
        UpdateBar.Visibility = Visibility.Collapsed;
        RefreshUpdateSources();
        SaveConfig();
        UpdateStatusText.Text = "已保存，点击「检查更新」可测试连接。";
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _updatesClosed || _updateDownloadCancel != null) return;
        if (!manual && (!_config.UpdateChecksEnabled || string.IsNullOrWhiteSpace(_config.UpdateManifestUrl) || _config.NextUpdateCheckUtc > DateTimeOffset.UtcNow)) return;
        _checkingUpdates = true; CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        var channel = _config.UpdateChannel;
        var generation = _updateSourceGeneration;
        try
        {
            _updateService ??= new UpdateService();
            _config.HighestUpdateVersions ??= [];
            _config.HighestUpdateVersions.TryGetValue(channel, out var highest);
            var update = await _updateService.CheckAsync([_config.UpdateManifestUrl, _config.UpdateBackupManifestUrl], channel, highest, _updateLifetime.Token);
            if (_updatesClosed || _config.UpdateChannel != channel || generation != _updateSourceGeneration) return;
            _config.HighestUpdateVersions[channel] = update.Manifest.Version;
            _config.NextUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(6);
            SaveConfig();
            UpdateStatusText.Text = $"{DateTime.Now:HH:mm} · {update.SourceHost} · " + (update.IsNewer ? $"发现 {update.Manifest.Version}" : "当前已是最新版本");
            if (!update.IsNewer) { _availableUpdate = null; _downloadedUpdate = null; UpdateBar.Visibility = Visibility.Collapsed; return; }
            if (_availableUpdate?.Manifest.Version != update.Manifest.Version) _downloadedUpdate = null;
            _availableUpdate = update;
            UpdateNoticeText.Text = $"发现新版本 {update.Manifest.Version} · {update.Package.Size / 1048576d:F1} MB";
            UpdateNotesText.Text = update.Manifest.ReleaseNotes;
            UpdateNotesText.ToolTip = update.Manifest.ReleaseNotes;
            DownloadUpdateButton.Content = _downloadedUpdate == null ? "下载更新" : "打开更新包";
            UpdateBar.Visibility = Visibility.Visible;
            if (!manual && _lastUpdateNotification != update.Manifest.Version && _tray != null)
            {
                _lastUpdateNotification = update.Manifest.Version;
                _tray.Visible = true;
                _tray.ShowBalloonTip(5000, "魔力窗口台有新版本", $"{update.Manifest.Version} 已可下载，打开窗口台查看。", System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is UpdateException or IOException or UnauthorizedAccessException or FormatException)
        {
            if (_updatesClosed || generation != _updateSourceGeneration) return;
            _config.NextUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(1);
            UpdateStatusText.Text = error is UpdateException ? error.Message : "更新检查未完成，请检查配置或稍后重试。";
            if (_persistConfiguration) { try { SaveConfig(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        finally { _checkingUpdates = false; if (!_updatesClosed) CheckUpdatesButton.IsEnabled = true; DisposeClosedUpdateResources(); }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate == null || _updateDownloadCancel != null) return;
        if (_downloadedUpdate != null && File.Exists(_downloadedUpdate)) { OpenUpdatePackage(_downloadedUpdate); return; }
        _updateDownloadCancel = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        DownloadUpdateButton.IsEnabled = false; CancelUpdateButton.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0; UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CgDesk", "updates");
            var progress = new Progress<double>(value => { if (!_updatesClosed) UpdateProgress.Value = value; });
            _downloadedUpdate = await (_updateService ??= new UpdateService()).DownloadAsync(_availableUpdate, directory, progress, _updateDownloadCancel.Token);
            if (_updatesClosed) return;
            UpdateNoticeText.Text = "更新包已下载并通过签名信息和 SHA256 校验";
            UpdateNotesText.Text = "可在方便时退出窗口台，解压新版本到独立目录运行。游戏不会被自动关闭。";
            DownloadUpdateButton.Content = "打开更新包";
            OpenUpdatePackage(_downloadedUpdate);
        }
        catch (OperationCanceledException) { if (!_updatesClosed) UpdateNoticeText.Text = "下载已取消，可以重新下载"; }
        catch (Exception error) when (error is UpdateException or IOException or UnauthorizedAccessException)
        { if (!_updatesClosed) UpdateNoticeText.Text = error is UpdateException ? error.Message : "无法保存更新包，请检查磁盘空间或权限。"; }
        finally
        {
            _updateDownloadCancel.Dispose(); _updateDownloadCancel = null;
            if (!_updatesClosed) { DownloadUpdateButton.IsEnabled = true; CancelUpdateButton.Visibility = Visibility.Collapsed; UpdateProgress.Visibility = Visibility.Collapsed; }
            DisposeClosedUpdateResources();
        }
    }
    private void OpenUpdatePackage(string path)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(path); Process.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { UpdateStatusText.Text = "文件夹未能打开，更新包保存在本机 CgDesk 更新目录。"; }
    }
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => _updateDownloadCancel?.Cancel();
    private void CloseUpdateChecks()
    {
        _updatesClosed = true; _updateTimer?.Stop(); _updateLifetime.Cancel(); DisposeClosedUpdateResources();
    }
    private void DisposeClosedUpdateResources()
    {
        if (_updatesClosed && !_checkingUpdates && _updateDownloadCancel == null) { _updateService?.Dispose(); _updateLifetime.Dispose(); }
    }
    private sealed record DeploymentSources(string Primary, string? Backup);
}
