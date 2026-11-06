using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using CgDesk.Services;

namespace CgDesk;

public partial class MainWindow
{
    private readonly DispatcherTimer _scaleTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private bool _scaleAfterResize;
    private bool _fsrRequested;
    private IntPtr _fsrSource;
    private (int Width, int Height) _sourceBeforeFsr;
    private (int Width, int Height) _presentedSize;
    private long _captureReadyAt;
    private bool UseCapturedMain => _fsrRequested && !FsrSurface.Failed && !_docked;

    private void EnableFsrForResize()
    {
        if (_primaryHandle == IntPtr.Zero || _docked) return;
        _scaleAfterResize = true;
        if (FsrSurface.Failed) FsrSurface.Reset();
        SchedulePresentationFit();
    }

    private void SchedulePresentationFit()
    {
        if (_loading || _docked || _adjusting || WindowState == WindowState.Minimized || !_scaleAfterResize) return;
        _scaleTimer.Stop();
        _scaleTimer.Start();
    }

    private (int Width, int Height) PresentedGameSize() => UseCapturedMain && _presentedSize.Width > 0
        ? _presentedSize : ShownGameSize();

    private void UpdatePresentationFit()
    {
        _scaleTimer.Stop();
        if (_docked || _adjusting || !IsVisible || WindowState == WindowState.Minimized || _primaryHandle == IntPtr.Zero) return;
        var source = ShownGameSize();
        var dpi = VisualTreeHelper.GetDpi(this);
        var fit = LaunchResolutions.FitFourByThree(GameScroller.ActualWidth * dpi.DpiScaleX,
            GameScroller.ActualHeight * dpi.DpiScaleY);
        var wanted = _scaleAfterResize && !FsrSurface.Failed && source.Width * 3 == source.Height * 4
            && fit.Width >= 8 && fit.Height >= 6
            && (Math.Abs(fit.Width - source.Width) > 2 || Math.Abs(fit.Height - source.Height) > 2);
        if (wanted)
        {
            if (!_fsrRequested || _fsrSource != _primaryHandle)
            {
                _sourceBeforeFsr = source;
                _fsrSource = _primaryHandle;
                FsrSurface.Reset();
                _captureReadyAt = Environment.TickCount64 + 160;
            }
            _presentedSize = fit;
        }
        else if (_fsrRequested) FsrSurface.Hide();
        _fsrRequested = wanted;
        ApplyViewportMinimum();
        ApplyNativeFrame();
        Dispatcher.BeginInvoke(() => { if (!_docked) EmbedIntoHosts(); }, DispatcherPriority.Loaded);
    }

    private void ResetFsrPresentation()
    {
        _scaleTimer.Stop();
        FsrSurface.Hide();
        _fsrRequested = false;
        _fsrSource = IntPtr.Zero;
        _sourceBeforeFsr = default;
        _presentedSize = default;
    }

    private void SyncScaledGame()
    {
        if (!UseCapturedMain || _mainHost == null) return;
        GameCursorCloak.Hide();
        if (!GameEmbedder.IsReady(_primaryHandle)) return;
        // DWM needs a composition turn after changing a child into a top-level
        // capture source. CreateForWindow can reject it during that transition.
        if (Environment.TickCount64 < _captureReadyAt) return;
        FsrSurface.Sync(_mainHost.HostHandle, _primaryHandle, 0.7f);
        if (FsrSurface.IsPresenting && FsrSurface.RenderedFrames >= 3
            && TryCaptureOrigin(out var sourceX, out var sourceY))
            GameEmbedder.MoveStaged(_primaryHandle, sourceX, sourceY);
        if (FsrSurface.Failed)
        {
            var reason = FsrSurface.LastError;
            _fsrRequested = false;
            Dispatcher.BeginInvoke(() => { ApplyViewportMinimum(); ApplyNativeFrame(); EmbedIntoHosts(); SetStatus(reason); }, DispatcherPriority.Loaded);
        }
    }

    private bool TryCaptureOrigin(out int x, out int y)
    {
        if (!TryCoveredOrigin(out x, out y)) return false;
        var source = ShownGameSize();
        var dpi = VisualTreeHelper.GetDpi(this);
        if (source.Width * dpi.DpiScaleX + 24 <= ActualWidth * dpi.DpiScaleX
            && source.Height * dpi.DpiScaleY + 60 <= ActualHeight * dpi.DpiScaleY) return true;
        // Initialize the compositor surface while the window is on-screen.
        // Starting capture at the screen edge can otherwise yield black frames.
        if (!FsrSurface.IsPresenting || FsrSurface.RenderedFrames < 3) return true;
        // A reduced presentation must not expose the larger source outside the
        // desk. Keep one pixel on-screen so Windows continues producing frames;
        // WGC captures the complete top-level surface.
        var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
        var physical = GameEmbedder.CaptureSize(_primaryHandle, source.Width, source.Height);
        x = desktop.Left - physical.Width + 1;
        y = desktop.Top - physical.Height + 1;
        return true;
    }
}
