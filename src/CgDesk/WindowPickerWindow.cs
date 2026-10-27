using System.Windows;
using System.Windows.Controls;
using CgDesk.Services;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CgDesk;

internal sealed class WindowPickerWindow : Window
{
    private readonly ListBox _windows = new() { MinHeight = 160, MaxHeight = 360 };
    private readonly Button _connect = new() { Content = "接入所选窗口", IsEnabled = false, MinWidth = 120 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    internal GameWindow? SelectedWindow { get; private set; }

    internal WindowPickerWindow(int ownProcessId)
    {
        Title = "接入游戏窗口";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "InkBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _windows.SetResourceReference(Control.BackgroundProperty, "FieldBrush");
        _windows.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _windows.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        var panel = new DockPanel { Margin = new Thickness(16) };
        var help = new TextBlock
        {
            Text = "选中仍在桌面上的游戏客户端。接入后可在窗口菜单中将它移回桌面。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(help, Dock.Top);
        panel.Children.Add(help);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var refresh = new Button { Content = "刷新列表", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Refresh(ownProcessId);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        actions.Children.Add(refresh);
        actions.Children.Add(cancel);
        actions.Children.Add(_connect);
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        _message.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(_message, Dock.Bottom);
        panel.Children.Add(_message);
        panel.Children.Add(_windows);
        Content = panel;
        _windows.SelectionChanged += (_, _) => _connect.IsEnabled = _windows.SelectedItem is ListBoxItem;
        _connect.Click += (_, _) =>
        {
            if (_windows.SelectedItem is not ListBoxItem { Tag: GameWindow window }) return;
            if (!NativeMethods.IsWindow(window.Handle))
            {
                _message.Text = "这个窗口已经关闭，请刷新列表。";
                return;
            }
            SelectedWindow = window;
            DialogResult = true;
        };
        Refresh(ownProcessId);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DeskTheme.ApplyTitleBar(this);
    }

    private void Refresh(int ownProcessId)
    {
        _windows.Items.Clear();
        foreach (var window in GameWindowScanner.Candidates(ownProcessId))
        {
            var text = new StackPanel { Margin = new Thickness(6) };
            text.Children.Add(new TextBlock { Text = window.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = $"{window.ProcessName}.exe · 进程 {window.ProcessId}", Opacity = 0.65, Margin = new Thickness(0, 3, 0, 0) });
            _windows.Items.Add(new ListBoxItem { Content = text, Tag = window });
        }
        _message.Text = _windows.Items.Count == 0 ? "没有可接入的窗口。请先进入游戏，再刷新列表。" : "自动接入失败的客户端也可以在这里选择。";
        _connect.IsEnabled = false;
    }
}
