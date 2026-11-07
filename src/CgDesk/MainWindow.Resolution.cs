using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CgDesk.Services;

namespace CgDesk;

public partial class MainWindow
{
    private bool _updatingResolution;
    private bool _resolutionRiskAccepted;
    private string _resolutionMonitor = "";
    private (IntPtr Handle, int Width, int Height) _lastGameFit;

    private Rect DeskWorkArea()
    {
        var work = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        return new Rect(work.Left / scale.DpiScaleX, work.Top / scale.DpiScaleY,
            work.Width / scale.DpiScaleX, work.Height / scale.DpiScaleY);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        RefreshResolutionChoices();
        _lastGameFit = default;
        FitDeskAfterGameStarts();
    });

    private void RefreshResolutionChoices()
    {
        if (ResolutionBox == null || _updatingResolution) return;
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var signature = $"{screen.DeviceName}:{screen.Bounds.Width}:{screen.Bounds.Height}";
        if (signature == _resolutionMonitor) return;
        _resolutionMonitor = signature;
        var choices = LaunchResolutions.ForMonitor(screen.Bounds.Width, screen.Bounds.Height);
        var selected = LaunchResolutions.Select(_config, choices);
        _updatingResolution = true;
        try { ResolutionBox.ItemsSource = choices; ResolutionBox.SelectedItem = selected; }
        finally { _updatingResolution = false; }
        UpdateResolutionHint();
    }

    private void Resolution_DropDownOpened(object? sender, EventArgs e) => RefreshResolutionChoices();

    private void Resolution_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _updatingResolution || ResolutionBox.SelectedItem is not LaunchResolution mode) return;
        _config.LaunchWidth = mode.Width;
        _config.LaunchHeight = mode.Height;
        UpdateResolutionHint();
        SaveConfig();
    }

    private void UpdateResolutionHint()
    {
        var mode = ResolutionBox.SelectedItem as LaunchResolution;
        ResolutionHintText.Text = mode?.Native != false
            ? "已关闭分辨率实验。拖动窗口后使用 FSR 按 4:3 缩放。"
            : "默认开启：选择屏幕能容纳的最高 4:3 档位，上限 1600×1200；可手动调整或关闭。仅下次启动生效，之后拖动窗口使用 FSR。较高分辨率可能降低帧率。独立组件会调整运行内存，仍有被检测或封号风险，远处角色范围不保证扩大。";
    }

    private bool PrepareLaunchResolution(out string message)
    {
        RefreshResolutionChoices();
        var mode = ResolutionBox.SelectedItem as LaunchResolution ?? new LaunchResolution(0, 0);
        if (!mode.Native && !_resolutionRiskAccepted)
        {
            var answer = System.Windows.MessageBox.Show(this,
                $"将以 {mode.Width}×{mode.Height} 试运行。\n\n这会在客户端目录新增独立组件，并调整游戏运行内存。不会改写原 EXE 或资源文件，但无法保证不会被客户端保护检测或被认定为外挂。\n\n较高分辨率可能降低帧率，可选择较低档位并使用 FSR 缩放。更远处的角色仍可能受服务端限制。是否继续本次试验？",
                "实验分辨率风险", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) { message = "未启用实验分辨率。"; return false; }
            _resolutionRiskAccepted = true;
        }
        try { message = new ViewportLaunchFiles().Prepare(GameExe(), mode); return true; }
        catch (Exception error) { message = error.Message; return false; }
    }

    private void FitDeskAfterGameStarts()
    {
        if (_docked || _adjusting || !IsLoaded || _primaryHandle == IntPtr.Zero || !NativeMethods.IsWindow(_primaryHandle)) return;
        var game = ShownGameSize();
        var signature = (_primaryHandle, game.Width, game.Height);
        if (signature == _lastGameFit) return;
        _lastGameFit = signature;
        Dispatcher.BeginInvoke(() =>
        {
            if (_docked || _adjusting || !IsLoaded || _primaryHandle != signature._primaryHandle) return;
            if (_fsrSource != IntPtr.Zero && _fsrSource != _primaryHandle) ResetFsrPresentation();
            ApplyNativeFrame();
            UpdateLayout();
            var scale = VisualTreeHelper.GetDpi(this);
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            var work = screen.WorkingArea;
            var fit = LaunchResolutions.FitDesk(game.Width / scale.DpiScaleX, game.Height / scale.DpiScaleY,
                Math.Max(0, ActualWidth - MainViewport.ActualWidth), Math.Max(0, ActualHeight - MainViewport.ActualHeight),
                work.Width / scale.DpiScaleX, work.Height / scale.DpiScaleY);
            var width = Math.Max(1, (int)Math.Ceiling(fit.Width * scale.DpiScaleX));
            var height = Math.Max(1, (int)Math.Ceiling(fit.Height * scale.DpiScaleY));
            var handle = new WindowInteropHelper(this).Handle;
            if (WindowState == WindowState.Normal && NativeMethods.GetWindowRect(handle, out var rect))
            {
                _adjusting = true;
                try
                {
                    NativeMethods.SetWindowPos(handle, IntPtr.Zero, Math.Clamp(rect.Left, work.Left, work.Right - width),
                        Math.Clamp(rect.Top, work.Top, work.Bottom - height), width, height,
                        NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
                }
                finally { _adjusting = false; }
            }
            ApplyViewportMinimum();
            ApplyNativeFrame();
            Dispatcher.BeginInvoke(() => EmbedIntoHosts(), DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
}
