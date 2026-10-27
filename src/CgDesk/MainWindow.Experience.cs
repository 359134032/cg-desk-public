using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CgDesk.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CgDesk;

public partial class MainWindow
{
    private readonly WindowSeats _windowSeats = new();
    private readonly Dictionary<IntPtr, int> _selectedWindows = [];
    private readonly Dictionary<IntPtr, int> _detachedWindows = [];
    private readonly Dictionary<IntPtr, string> _windowLabels = [];
    private string _connectionError = "";
    private bool _waitingForGame;
    private bool UseCapturedMain => _config.EnhanceMainWindow && !FsrSurface.Failed && !_docked;
    private bool IsMainConnected => _mainHost != null &&
        (GameEmbedder.IsAttached(_primaryHandle, _mainHost.HostHandle) || UseCapturedMain && GameEmbedder.IsReady(_primaryHandle));

    private void FocusPrimaryInput()
    {
        if (!UseCapturedMain)
        {
            GameEmbedder.Focus(_primaryHandle);
            return;
        }
        // The source must stay behind the desk; input goes through the rendered host.
        Activate();
        if (_mainHost != null) System.Windows.Input.Keyboard.Focus(_mainHost);
        GameEmbedder.NudgeActive(_primaryHandle);
    }

    private string WindowLabel(GameWindow window) => _windowLabels.TryGetValue(window.Handle, out var label) ? label : window.Title;

    private string SeatShortcut(int number)
    {
        if (HotkeyBox.IsChecked != true) return "热键已关闭";
        var chord = HotkeyCatalog.Normalize(_config.Hotkeys).FirstOrDefault(item => item.Action == number.ToString());
        return chord == null || chord.Key == 0 ? "未设置热键" : HotkeyCatalog.Format(chord);
    }

    private IEnumerable<IntPtr> SelectedHandles()
    {
        foreach (var (handle, processId) in _selectedWindows.ToArray())
        {
            _ = NativeMethods.GetWindowThreadProcessId(handle, out var current);
            if (!NativeMethods.IsWindow(handle) || current != processId) _selectedWindows.Remove(handle);
        }
        return _selectedWindows.Keys;
    }

    private bool IsDetached(GameWindow window) => _detachedWindows.TryGetValue(window.Handle, out var pid) && pid == window.ProcessId;

    private void UpdateExperience(IReadOnlyList<GameWindow> seats)
    {
        var hasWindows = seats.Count > 0;
        UpdateCursorPollingInterval();
        EmptyState.Visibility = hasWindows ? Visibility.Collapsed : Visibility.Visible;
        MainPane.Visibility = hasWindows && !_docked ? Visibility.Visible : Visibility.Collapsed;
        MainFrame.Visibility = _docked ? Visibility.Collapsed : Visibility.Visible;
        MiniScroller.Visibility = _docked ? Visibility.Visible : Visibility.Collapsed;
        if (_docked) ThumbScroller.Visibility = Visibility.Collapsed;
        PreviewEmptyText.Visibility = hasWindows ? Visibility.Collapsed : Visibility.Visible;
        ArrangeButton.IsEnabled = hasWindows;
        MiniButton.IsEnabled = hasWindows;
        var hasLauncher = System.IO.File.Exists(LauncherPathBox.Text.Trim());
        LaunchButton.Content = hasLauncher ? "启动游戏" : "选择登录器";
        EmptyActionButton.Content = hasLauncher ? "打开登录器" : "选择登录器并启动";
        EmptyTitle.Text = _waitingForGame ? "等待游戏客户端出现" : "开始接入第一个游戏窗口";
        PreviewEmptyText.Text = _waitingForGame
            ? "请在官方登录器里进入游戏。窗口台会持续扫描；游戏已在桌面上时，也可以手动接入。"
            : hasLauncher ? "打开官方登录器，进入游戏后窗口会自动接入。" : "先选择官方登录器，或手动接入已打开的游戏窗口。";
        if (hasWindows) _waitingForGame = false;
        var active = seats.FirstOrDefault(window => window.Handle == _primaryHandle);
        var attached = IsMainConnected;
        ConnectionStateText.Text = !hasWindows ? "等待游戏窗口"
            : _docked ? $"已识别 {seats.Count} 个窗口 · 桌面控制模式"
            : attached ? $"已接入 · {seats.Count} 个窗口 · 当前 {WindowNumber(_primaryHandle)} 号"
            : $"已识别 {seats.Count} 个窗口 · 正在接入";
        if (active != null && attached) ConnectionStateText.ToolTip = WindowLabel(active);
        EnhanceStateText.Text = !_config.EnhanceMainWindow ? "画质增强已关闭"
            : !hasWindows ? "增强已开启 · 等待游戏"
            : _docked ? "增强已暂停 · 迷你模式"
            : FsrSurface.Failed ? "增强未生效 · 使用原始画面"
            : FsrSurface.IsPresenting ? $"FSR 已生效 · {FsrSurface.SourceSize.Width}×{FsrSurface.SourceSize.Height} → {FsrSurface.OutputSize.Width}×{FsrSurface.OutputSize.Height}"
            : attached ? "增强准备中 · 当前为原始画面" : "增强等待接入";
        EnhanceStateText.ToolTip = FsrSurface.Failed ? FsrSurface.LastError : "锐化仅作用于主窗口，其他客户端保持原始画面。";
        if (!hasWindows || attached || _docked) SetConnectionError("");
    }

    private int WindowNumber(IntPtr handle) => _windowSeats.Number(handle);

    private void UpdateCursorPollingInterval()
    {
        if (_cursorTimer == null) return;
        var active = _primaryHandle != IntPtr.Zero && IsVisible && WindowState != WindowState.Minimized && !_docked;
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(active ? 16 : 250);
    }

    private void SetConnectionError(string error)
    {
        _connectionError = error;
        ConnectionErrorText.Text = error;
        ConnectionErrorBar.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _config.SettingsExpanded = SettingsToggle.IsChecked == true;
        ApplySettingsLayout();
        SaveConfig();
    }

    private void ApplySettingsLayout()
    {
        var open = _config.SettingsExpanded && !_docked;
        SideBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SideColumn.Width = new GridLength(open ? 224 : 0);
        SettingsToggle.IsChecked = _config.SettingsExpanded;
        SettingsStateText.Text = open ? "收起" : "展开";
    }

    private void CollapseSettings_Click(object sender, RoutedEventArgs e)
    {
        _config.SettingsExpanded = false;
        ApplySettingsLayout();
        SaveConfig();
    }

    private void LauncherPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SaveConfig();
        RefreshStatus();
    }

    private void CloseBehavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.CloseToTray = CloseToTrayBox.IsChecked == true;
        SaveConfig();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => ExitFromTray();

    private void ConnectWindow_Click(object sender, RoutedEventArgs e)
    {
        var picker = new WindowPickerWindow(_selfPid) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedWindow is not { } window) return;
        ConnectWindow(window);
    }

    internal void ConnectWindow(GameWindow window)
    {
        if (!NativeMethods.IsWindow(window.Handle))
        {
            SetStatus("游戏窗口已经关闭，请重新扫描。");
            return;
        }
        _selectedWindows[window.Handle] = window.ProcessId;
        _detachedWindows.Remove(window.Handle);
        _primaryHandle = window.Handle;
        _stageSignature = "";
        if (_docked) ExpandFromDock();
        RefreshStatus();
        ArrangeCurrent(window.Handle);
    }

    private ContextMenu WindowMenu(GameWindow window)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "备注窗口（本次运行）…" };
        rename.Click += (_, _) => RenameWindow(window);
        var release = new MenuItem { Header = "移回桌面，停止自动接入" };
        release.Click += (_, _) =>
        {
            DisconnectWindow(window);
        };
        menu.Items.Add(rename);
        menu.Items.Add(release);
        return menu;
    }

    internal void DisconnectWindow(GameWindow window)
    {
        _detachedWindows[window.Handle] = window.ProcessId;
        _selectedWindows.Remove(window.Handle);
        GameEmbedder.Detach(window.Handle);
        _stageSignature = "";
        RefreshStatus();
        SetStatus("已将游戏移回桌面。需要时可通过「接入窗口」重新接入。");
    }

    private void RenameWindow(GameWindow window)
    {
        var dialog = new Window
        {
            Title = "窗口备注", Owner = this, Width = 390, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("InkBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };
        dialog.SetResourceReference(BackgroundProperty, "InkBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
        dialog.SourceInitialized += (_, _) => DeskTheme.ApplyTitleBar(dialog);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "给这个窗口起个便于辨认的名字，例如队长、医生、仓库。", TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { Text = WindowLabel(window), MaxLength = 50, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(name.Text)) _windowLabels.Remove(window.Handle);
        else _windowLabels[window.Handle] = name.Text.Trim();
        _stageSignature = "";
        UpdateStage();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = new StringBuilder();
            report.AppendLine($"魔力窗口台 {Assembly.GetExecutingAssembly().GetName().Version}");
            report.AppendLine($"系统：{Environment.OSVersion}；进程：{Environment.ProcessId}；64 位：{Environment.Is64BitProcess}");
            report.AppendLine($"接入状态：{ConnectionStateText.Text}");
            report.AppendLine($"画质状态：{EnhanceStateText.Text}");
            report.AppendLine($"接入错误：{_connectionError}");
            report.AppendLine($"增强错误：{FsrSurface.LastError}");
            report.AppendLine($"FSR 已渲染帧：{FsrSurface.RenderedFrames}；输入：{FsrSurface.SourceSize}；输出：{FsrSurface.OutputSize}");
            report.AppendLine($"主区域：{MainViewport.ActualWidth:0} × {MainViewport.ActualHeight:0}；迷你模式：{_docked}");
            foreach (var window in Seats())
            {
                var parent = _mainHost != null && GameEmbedder.IsAttached(window.Handle, _mainHost.HostHandle);
                report.AppendLine($"窗口 {WindowNumber(window.Handle)}：{window.ProcessName}.exe；PID {window.ProcessId}；HWND {window.Handle:X}；原生嵌入：{parent}；顶层采集源：{UseCapturedMain && window.Handle == _primaryHandle && GameEmbedder.IsReady(window.Handle)}");
            }
            System.Windows.Clipboard.SetText(report.ToString());
            SetStatus("诊断信息已复制，可粘贴用于排查。内容包含进程信息，不包含账号密码。");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus("剪贴板正被其他程序占用，请稍后重试。");
        }
    }
}
