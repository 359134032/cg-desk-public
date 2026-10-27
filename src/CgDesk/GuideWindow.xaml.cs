using System.ComponentModel;
using System.Windows;
using CgDesk.Services;

namespace CgDesk;

public partial class GuideWindow : Window
{
    private MainWindow? _host;
    private bool _allowClose;
    private bool _placed;

    public GuideWindow()
    {
        InitializeComponent();
    }

    internal void Attach(MainWindow host)
    {
        _host = host;
        Owner = host;
    }

    internal void AllowClose() => _allowClose = true;

    internal void EnsurePlaced()
    {
        if (_placed || _host == null)
        {
            return;
        }

        var config = _host.Config;
        Width = config.GuideWidth >= 360 ? config.GuideWidth : 440;
        Height = config.GuideHeight >= 480 ? config.GuideHeight : 720;
        Topmost = config.GuideTopmost;
        if (config.GuideLeft >= 0 && config.GuideTop >= 0)
        {
            Left = config.GuideLeft;
            Top = config.GuideTop;
        }
        else if (Owner != null)
        {
            Left = Owner.Left + Owner.Width + 16;
            Top = Owner.Top;
        }

        ClampToVirtualScreen();
        _placed = true;
        PinBox.IsChecked = config.GuideTopmost;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DeskTheme.ApplyTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(() =>
        {
            Hide();
            _host?.NotifyGuideHidden();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            GuideView.Dispose();
        }
        catch (Exception)
        {
            // 关闭时浏览器控件可能已经释放。
        }

        base.OnClosed(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        Remember();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Remember();
    }

    private void Remember()
    {
        if (!_placed || _host == null || !IsVisible || WindowState != WindowState.Normal)
        {
            return;
        }

        _host.NoteGuideBounds(Left, Top, Width, Height, Topmost);
    }

    private void ClampToVirtualScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (Width > right - left)
        {
            Width = Math.Max(360, right - left);
        }

        if (Height > bottom - top)
        {
            Height = Math.Max(480, bottom - top);
        }

        if (Left + 80 > right)
        {
            Left = right - Width;
        }

        if (Top + 80 > bottom)
        {
            Top = bottom - Height;
        }

        if (Left + Width < left + 80)
        {
            Left = left;
        }

        if (Top + Height < top + 80)
        {
            Top = top;
        }
    }

    private void PinBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_placed)
        {
            return;
        }

        Topmost = PinBox.IsChecked == true;
        Remember();
    }

    private void GuideSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => _host?.GuideSearchBox_PreviewKeyDown(sender, e);

    private void GuideSearch_Click(object sender, RoutedEventArgs e) => _host?.GuideSearch_Click(sender, e);

    private void GuideHome_Click(object sender, RoutedEventArgs e) => _host?.GuideHome_Click(sender, e);

    private void GuideBack_Click(object sender, RoutedEventArgs e) => _host?.GuideBack_Click(sender, e);

    private void GuideForward_Click(object sender, RoutedEventArgs e) => _host?.GuideForward_Click(sender, e);

    private void GuideReload_Click(object sender, RoutedEventArgs e) => _host?.GuideReload_Click(sender, e);

    private void GuideOpenExternal_Click(object sender, RoutedEventArgs e) => _host?.GuideOpenExternal_Click(sender, e);

    private void GuideRuntime_Click(object sender, RoutedEventArgs e) => _host?.GuideRuntime_Click(sender, e);

    private void GuideRetry_Click(object sender, RoutedEventArgs e) => _host?.RetryGuide();
}
