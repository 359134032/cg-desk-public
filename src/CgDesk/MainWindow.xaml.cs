using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CgDesk.Models;
using CgDesk.Services;
using Microsoft.Win32;

namespace CgDesk;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _enhanceTimer;
    private readonly Func<GameWindow, bool>? _windowFilter;
    private readonly bool _persistConfiguration;
    private readonly bool _manageEnhancementFiles;
    private AppConfig _config = new();
    private HotkeyHost? _hotkeys;
    private IntPtr _primaryHandle;
    private bool _loading = true;
    private string _stageSignature = "";
    private int _shownSeat = -1;
    private readonly List<ThumbView> _thumbs = [];
    private readonly List<SmallSlot> _smallSlots = [];
    private readonly HashSet<IntPtr> _knownHandles = [];
    private GameHost? _mainHost;
    private int _embedWaits;
    private int _hugWaits;
    private bool _docked;
    private bool _allowClose;
    private bool _adjusting;
    private bool _placing;
    private int _fittedWidth = WindowArranger.GameClientWidth;
    private int _fittedHeight = WindowArranger.GameClientHeight;
    private bool _upscaleLive;
    private bool _upscaleNoted;
    private int _freezeGeneration;
    private long _quietForegroundUntil;
    private Rect _normalBounds;
    private int _selfPid;
    private System.Windows.Forms.NotifyIcon? _tray;
    private ForegroundProc? _foregroundProc;
    private IntPtr _foregroundHook;
    private DispatcherTimer? _cursorTimer;
    private MouseHookProc? _mouseProc;
    private IntPtr _mouseHook;
    private IntPtr _deskHandle;
    private readonly IntPtr[] _cursorMasters = new IntPtr[SystemCursorIds.Length];
    private IntPtr _blankCursor;
    private bool _cursorMastersReady;
    private int _appliedCursorMode = -1;
    private GuideWindow _guideWindow = null!;
    private DispatcherTimer? _guideSaveTimer;

    public MainWindow() : this(ConfigStore.Load()) { }

    internal MainWindow(AppConfig config, bool initializeEnhancement = true, Func<GameWindow, bool>? windowFilter = null, bool persistConfiguration = true, UpdateService? updateService = null)
    {
        _windowFilter = windowFilter;
        _persistConfiguration = persistConfiguration;
        _manageEnhancementFiles = initializeEnhancement;
        _updateService = updateService;
        InitializeComponent();
        _mainHost = new GameHost();
        _mainHost.Clicked += () =>
        {
            if (!ReferenceEquals(Keyboard.FocusedElement, _mainHost))
            {
                Keyboard.Focus(_mainHost);
            }
        };
        MainViewport.Child = _mainHost;
        _selfPid = Process.GetCurrentProcess().Id;
        _config = config;
        DeskTheme.Apply(_config.DarkMode);
        _guideWindow = new GuideWindow();
        LoadConfigToUi();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        _enhanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _enhanceTimer.Tick += (_, _) =>
        {
            _enhanceTimer.Stop();
            if (_docked || _adjusting)
            {
                return;
            }

            ApplyEnhancedFrame();
            Dispatcher.BeginInvoke(() =>
            {
                if (_docked || _adjusting)
                {
                    return;
                }

                EmbedIntoHosts();
                if (_config.EnhanceMainWindow)
                {
                    GameEnhance.UpdateResolution(WindowArranger.GameClientWidth, WindowArranger.GameClientHeight);
                }
            }, DispatcherPriority.Loaded);
        };
        Stage.SizeChanged += (_, _) => ScheduleEnhancedFit();
        LocationChanged += (_, _) => KeepSmallWindowsCovered();
        SizeChanged += (_, _) => KeepSmallWindowsCovered();
        ThumbScroller.ScrollChanged += (_, _) => RefreshSmallPreviews();
        MiniScroller.ScrollChanged += (_, _) => { if (_docked) ApplyThumbnailRects(); };
        _loading = false;
        if (initializeEnhancement) ApplyEnhance(false);
        RefreshStatus();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
            {
                if (IsLoaded) Dispatcher.BeginInvoke(RefreshStatus, DispatcherPriority.Loaded);
                return;
            }

            if (_adjusting)
            {
                WindowState = WindowState.Normal;
                return;
            }

            // Minimize uses the taskbar. Keeping the game parented lets it return with the desk.
        };
        PrepareGuide();
        Loaded += MainWindow_Loaded;
        CreateTray();
        InitializeUpdateChecks();
        Reveal(SideBar, 0);
        Reveal(LayoutHeader, 40);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DeskTheme.ApplyTitleBar(this);
        Activate();
        Topmost = true;
        Topmost = false;
        ApplyViewportMinimum();
        ApplyHotkeys();
        if (PresentationSource.FromVisual(this) is HwndSource deskSource)
        {
            deskSource.AddHook(OnDeskMessage);
        }
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _foregroundProc, 0, 0, 2);
        _deskHandle = new WindowInteropHelper(this).Handle;
        EnableDoubleClick(_deskHandle);
        _mouseProc = OnLowLevelMouse;
        _mouseHook = SetWindowsHookEx(14, _mouseProc, GetModuleHandle(null), 0);
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTimer.Tick += (_, _) =>
        {
            SyncGameCloak();
            SyncSystemCursor();
        };
        _cursorTimer.Start();
        UpdateCursorPollingInterval();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemCursors();
        SyncSystemCursor();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && _config.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _guideClosed = true;
        _allowClose = true;
        _guideSetupCancel?.Cancel();
        _guideWindow.AllowClose();
        ReleaseEmbedded();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseUpdateChecks();
        _timer.Stop();
        _enhanceTimer.Stop();
        _guideSaveTimer?.Stop();
        _cursorTimer?.Stop();
        if (_mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        GameCursorCloak.Close();
        RestoreSystemCursors();
        _hotkeys?.Dispose();
        if (_foregroundHook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        FsrSurface.Close();
        CloseThumbs();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        SaveConfig();
        base.OnClosed(e);
    }

    private void LoadConfigToUi()
    {
        LauncherPathBox.Text = _config.LauncherPath;
        HotkeyBox.IsChecked = _config.HotkeysEnabled;
        EnhanceBox.IsChecked = _config.EnhanceMainWindow;
        SharpnessSlider.Value = Math.Clamp(_config.EnhanceSharpness, 0, 1);
        SharpnessLabel.Text = $"锐化强度 {Math.Round(SharpnessSlider.Value * 100)}%";
        LiveSmallBox.IsChecked = _config.LiveSmallPreview != false;
        DarkBox.IsChecked = _config.DarkMode;
        CloseToTrayBox.IsChecked = _config.CloseToTray;
        VersionText.Text = $"魔力窗口台 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        if (_config.SettingsLayoutVersion < 1)
        {
            _config.SettingsExpanded = true;
            _config.SettingsLayoutVersion = 1;
        }
        ApplySettingsLayout();
        ApplyGuideLayout(_config.ShowGuide);
    }

    private void SaveConfig()
    {
        if (_loading)
        {
            return;
        }

        _config.LauncherPath = LauncherPathBox.Text.Trim();
        _config.HotkeysEnabled = HotkeyBox.IsChecked == true;
        _config.EnhanceMainWindow = EnhanceBox.IsChecked == true;
        _config.EnhanceSharpness = Math.Clamp(SharpnessSlider.Value, 0, 1);
        _config.LiveSmallPreview = LiveSmallBox.IsChecked == true;
        _config.DarkMode = DarkBox.IsChecked == true;
        if (_persistConfiguration) ConfigStore.Save(_config);
    }

    private IEnumerable<string> ProcessNames()
    {
        var names = _config.GameProcessNames.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
            yield return name;
        }

        var gameExe = GameExe();
        if (!string.IsNullOrWhiteSpace(gameExe))
        {
            yield return System.IO.Path.GetFileNameWithoutExtension(gameExe);
        }

        yield return "cg";
        yield return "cg_se_3000";
        yield return "cg_se_6000";
        yield return "cg_item_6000";
    }

    private string GameExe()
    {
        if (LauncherInstall.TryLocate(LauncherPathBox.Text.Trim(), out _, out var associated) && associated.Length > 0)
        {
            return associated;
        }

        return _config.GamePath;
    }

    private void RefreshStatus()
    {
        GameEmbedder.ForgetClosed();
        if (_primaryHandle != IntPtr.Zero && !NativeMethods.IsWindow(_primaryHandle))
        {
            ResetGameInputPresentation();
            _primaryHandle = IntPtr.Zero;
        }

        UpdateStage();
        if (NeedsEmbed())
        {
            _embedWaits = 0;
            EmbedIntoHosts();
        }

        RefreshSmallPreviews();
    }

    private void UpdateStage()
    {
        var seats = Seats();
        var activeIndex = seats.FindIndex(seat => seat.Handle == _primaryHandle);
        if (activeIndex < 0)
        {
            activeIndex = seats.Count > 0 ? 0 : -1;
            _primaryHandle = seats.Count > 0 ? seats[0].Handle : IntPtr.Zero;
        }
        UpdateExperience(seats);

        var signature = (activeIndex < 0 ? 0 : _primaryHandle.ToInt64()) + "|"
            + string.Join(",", seats.Select(seat => seat.Handle.ToInt64() + ":" + WindowLabel(seat)));
        var handles = seats.Select(seat => seat.Handle).ToHashSet();
        if (_docked)
        {
            var miniSignature = "mini|" + signature;
            if (_stageSignature != miniSignature)
            {
                CloseThumbs();
                _smallSlots.Clear();
                ThumbStrip.Children.Clear();
                MiniWindows.Children.Clear();
                _knownHandles.Clear();
                foreach (var seat in seats)
                {
                    _knownHandles.Add(seat.Handle);
                    MiniWindows.Children.Add(CreateThumbCard(seat, WindowNumber(seat.Handle) - 1));
                }
                _stageSignature = miniSignature;
            }
            Dispatcher.BeginInvoke(ApplyThumbnailRects, DispatcherPriority.Loaded);
            return;
        }
        var smallCount = activeIndex < 0 ? seats.Count : seats.Count - 1;
        if (handles.SetEquals(_knownHandles) && _smallSlots.Count == smallCount)
        {
            if (signature != _stageSignature)
            {
                _stageSignature = signature;
                AssignSeats(seats, activeIndex);
            }

            return;
        }

        _stageSignature = signature;
        _knownHandles.Clear();
        foreach (var handle in handles)
        {
            _knownHandles.Add(handle);
        }

        foreach (var slot in _smallSlots)
        {
            WindowThumbs.Close(slot.Thumbnail);
            slot.Thumbnail = IntPtr.Zero;
            GameEmbedder.Park(slot.Game);
        }

        CloseThumbs();
        _smallSlots.Clear();
        ThumbStrip.Children.Clear();
        var hasSeats = seats.Count > 0;
        PreviewEmptyText.Visibility = hasSeats ? Visibility.Collapsed : Visibility.Visible;
        MainPane.Visibility = hasSeats ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSeats)
        {
            SetMainMark(0, "");
            ApplySmallColumn();
            return;
        }

        SetMainMark(WindowNumber(seats[activeIndex].Handle), WindowLabel(seats[activeIndex]));
        for (var index = 0; index < seats.Count; index++)
        {
            if (index == activeIndex)
            {
                continue;
            }

            ThumbStrip.Children.Add(CreateSmallCard(seats[index], index));
        }

        Dispatcher.BeginInvoke(() => AssignSeats(seats, activeIndex), DispatcherPriority.Loaded);
        ApplySmallColumn();
    }

    private void ApplySmallColumn()
    {
        var show = _smallSlots.Count > 0;
        SmallColumn.Width = new GridLength(show ? 188 : 0);
        ThumbScroller.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AssignSeats(IReadOnlyList<GameWindow> seats, int activeIndex, bool retry = true)
    {
        if (_docked || activeIndex < 0 || activeIndex >= seats.Count)
        {
            return;
        }

        if (retry && (MainViewport.ActualWidth < 8 || _smallSlots.Any(slot => slot.Target.ActualWidth < 8)))
        {
            Dispatcher.BeginInvoke(() => AssignSeats(seats, activeIndex, false), DispatcherPriority.Loaded);
            return;
        }

        EnsureRoomForGame();

        var active = seats[activeIndex];
        _primaryHandle = active.Handle;
        SetMainMark(WindowNumber(active.Handle), WindowLabel(active));
        MainCaption.ContextMenu = WindowMenu(active);
        MainCaption.ToolTip = "右键可备注窗口或移回桌面";
        var smalls = new List<GameWindow>();
        for (var index = 0; index < seats.Count; index++)
        {
            if (index != activeIndex)
            {
                smalls.Add(seats[index]);
            }
        }

        if (smalls.Count != _smallSlots.Count)
        {
            return;
        }

        for (var index = 0; index < smalls.Count; index++)
        {
            var number = WindowNumber(smalls[index].Handle);
            if (_smallSlots[index].Game != smalls[index].Handle)
            {
                WindowThumbs.Close(_smallSlots[index].Thumbnail);
                _smallSlots[index].Thumbnail = IntPtr.Zero;
                _smallSlots[index].Still = null;
                _smallSlots[index].Preview.Source = null;
            }

            _smallSlots[index].Game = smalls[index].Handle;
            _smallSlots[index].Label.Text = WindowLabel(smalls[index]);
            _smallSlots[index].Label.ToolTip = WindowLabel(smalls[index]);
            _smallSlots[index].Frame.ContextMenu = WindowMenu(smalls[index]);
            _smallSlots[index].Button.ContextMenu = WindowMenu(smalls[index]);
            _smallSlots[index].Button.ToolTip = $"切到 {number} 号 · {WindowLabel(smalls[index])}";
            _smallSlots[index].Shortcut.Text = SeatShortcut(number);
            PaintSeat(_smallSlots[index].Badge, _smallSlots[index].BadgeText, number);
            if (_smallSlots[index].Target is Border screen)
            {
                screen.BorderBrush = SeatBrush(number);
            }
        }

        EmbedIntoHosts();
    }

    private System.Windows.Controls.Button CreateSmallCard(GameWindow seat, int index)
    {
        var number = WindowNumber(seat.Handle);
        var preview = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var canvas = new Grid();
        canvas.Children.Add(preview);
        var screen = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 29, 31)),
            BorderBrush = SeatBrush(number),
            BorderThickness = new Thickness(3),
            Width = 156,
            Height = 117,
            Child = canvas
        };
        var slot = new SmallSlot(seat.Handle, canvas, screen, preview);
        slot.Badge = new Border
        {
            Width = 22,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 4, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        slot.BadgeText = new TextBlock
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 25, 21))
        };
        slot.Badge.Child = slot.BadgeText;
        PaintSeat(slot.Badge, slot.BadgeText, number);
        slot.Label = new TextBlock
        {
            Text = WindowLabel(seat),
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        slot.Label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var caption = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        caption.Children.Add(slot.Badge);
        caption.Children.Add(slot.Label);
        var captionGroup = new StackPanel();
        captionGroup.Children.Add(caption);
        slot.Shortcut = new TextBlock { Text = SeatShortcut(number), FontSize = 10, Opacity = 0.7, Margin = new Thickness(0, 3, 0, 0) };
        captionGroup.Children.Add(slot.Shortcut);
        DockPanel.SetDock(captionGroup, Dock.Bottom);
        var panel = new DockPanel();
        panel.Children.Add(captionGroup);
        panel.Children.Add(screen);
        var button = new System.Windows.Controls.Button
        {
            Width = screen.Width + 16,
            Height = screen.Height + 54,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(4),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch,
            Content = panel,
            ToolTip = $"切到 {number} 号 · {WindowLabel(seat)}",
            ContextMenu = WindowMenu(seat)
        };
        button.Click += (_, _) => SwapMain(slot.Game);
        slot.Button = button;
        slot.Label.Opacity = 0;
        slot.Label.Loaded += (_, _) => Reveal(slot.Label, 16);
        _smallSlots.Add(slot);
        return button;
    }

    private void SwapMain(IntPtr next)
    {
        if (next == IntPtr.Zero || next == _primaryHandle || !NativeMethods.IsWindow(next))
        {
            return;
        }

        var seats = Seats();
        if (seats.All(seat => seat.Handle != next))
        {
            return;
        }

        var previous = _primaryHandle;
        _primaryHandle = next;
        _stageSignature = "";
        UpdateStage();
        var number = WindowNumber(next);
        var title = WindowLabel(seats.First(seat => seat.Handle == next));
        SetStatus(previous == IntPtr.Zero
            ? $"主窗口：{number} 号 {title}"
            : $"已交换：{number} 号 {title} 放到主窗口。");
    }

    private void ApplyViewportMinimum()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        MainViewport.MinWidth = WindowArranger.GameClientWidth / dpi.DpiScaleX;
        MainViewport.MinHeight = WindowArranger.GameClientHeight / dpi.DpiScaleY;
        if (_docked)
        {
            return;
        }

        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(760, work.Width);
        MinHeight = Math.Min(560, work.Height);
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);
    }

    private bool EmbedPrimary()
    {
        if (_primaryHandle == IntPtr.Zero || !NativeMethods.IsWindow(_primaryHandle))
        {
            return false;
        }

        if (_docked)
        {
            SetStatus("先点展开。缩在左下角时主区域放不下 800×600 的游戏窗口。");
            return false;
        }

        EnsureRoomForGame();
        if (!HostCanHoldGame())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (HostCanHoldGame())
                {
                    EmbedIntoHosts();
                }
            }), DispatcherPriority.Loaded);
            SetStatus("主区域还没到 800×600，游戏窗口先留在原处。");
            return false;
        }

        var embedded = EmbedIntoHosts();
        EnsureToolVisible();
        return embedded;
    }

    private bool NeedsEmbed()
    {
        if (_docked || !IsVisible || WindowState == WindowState.Minimized)
        {
            return false;
        }

        if (_primaryHandle != IntPtr.Zero
            && NativeMethods.IsWindow(_primaryHandle)
            && (_mainHost == null || _mainHost.KeyTarget != _primaryHandle || !IsMainConnected))
        {
            return true;
        }

        return _smallSlots.Any(slot =>
            slot.Game != IntPtr.Zero
            && NativeMethods.IsWindow(slot.Game)
            && !GameEmbedder.IsReady(slot.Game));
    }

    private bool EmbedIntoHosts()
    {
        if (_docked || !IsVisible || WindowState == WindowState.Minimized || _adjusting)
        {
            return false;
        }

        if (_mainHost == null || _mainHost.HostHandle == IntPtr.Zero)
        {
            if (_embedWaits++ < 8)
            {
                Dispatcher.BeginInvoke(new Action(() => EmbedIntoHosts()), DispatcherPriority.Loaded);
            }

            return false;
        }

        _embedWaits = 0;
        if (_primaryHandle != IntPtr.Zero && NativeMethods.IsWindow(_primaryHandle) && _mainHost.HostHandle != IntPtr.Zero
            && NativeMethods.GetClientRect(_mainHost.HostHandle, out var hostClient))
        {
            var shown = ShownGameSize();
            var hostWidth = hostClient.Right - hostClient.Left;
            var hostHeight = hostClient.Bottom - hostClient.Top;
            if ((hostWidth > shown.Width + 24 || hostHeight > shown.Height + 24) && _hugWaits < 6)
            {
                _hugWaits++;
                ApplyEnhancedFrame();
                Dispatcher.BeginInvoke(() => EmbedIntoHosts(), DispatcherPriority.Loaded);
                return false;
            }
        }

        _hugWaits = 0;
        _placing = true;
        var embedded = false;
        try
        {
            if (_primaryHandle != IntPtr.Zero && NativeMethods.IsWindow(_primaryHandle))
            {
                var host = _mainHost.HostHandle;
                var fresh = !IsMainConnected;
                var shown = _upscaleLive
                    ? (Width: WindowArranger.GameClientWidth, Height: WindowArranger.GameClientHeight)
                    : ShownGameSize();
                if (UseCapturedMain && TryCoveredOrigin(out var sourceX, out var sourceY))
                {
                    // Windows Graphics Capture rejects child windows. Keep the source
                    // at native size behind the desk and render FSR into the UI host.
                    embedded = GameEmbedder.ShowFull(_primaryHandle, sourceX, sourceY,
                        WindowArranger.GameClientWidth, WindowArranger.GameClientHeight, forCapture: true);
                }
                else
                {
                    embedded = GameEmbedder.Place(_primaryHandle, host, shown.Width, shown.Height);
                }
                _mainHost.KeyTarget = embedded ? _primaryHandle : IntPtr.Zero;
                if (embedded)
                {
                    EnableDoubleClick(_primaryHandle);
                }

                if (embedded && fresh)
                {
                    if (!UseCapturedMain) GameEmbedder.Focus(_primaryHandle);
                    GameEmbedder.NudgeActive(_primaryHandle);
                }
                SetConnectionError(embedded ? "" : GameEmbedder.LastError);
                ConnectionStateText.Text = embedded
                    ? $"已接入 · {_knownHandles.Count} 个窗口 · 当前 {WindowNumber(_primaryHandle)} 号"
                    : $"已识别 {_knownHandles.Count} 个窗口 · 接入未成功";
            }

            if (TryCoveredOrigin(out var x, out var y))
            {
                foreach (var slot in _smallSlots)
                {
                    if (slot.Game == IntPtr.Zero || !NativeMethods.IsWindow(slot.Game))
                    {
                        continue;
                    }

                    var size = SmallGameSize(slot.Game);
                    _ = GameEmbedder.ShowFull(slot.Game, x, y, size.Width, size.Height);
                }
            }

            return embedded;
        }
        finally
        {
            _placing = false;
            _quietForegroundUntil = Environment.TickCount64 + 600;
            SyncGameCloak();
            Dispatcher.BeginInvoke(RefreshSmallPreviews, DispatcherPriority.Loaded);
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            SyncSystemCursor();
            if (_adjusting || _placing || Environment.TickCount64 < _quietForegroundUntil || hwnd == _primaryHandle)
            {
                return;
            }

            if (_smallSlots.Any(slot => slot.Game == hwnd))
            {
                SwapMain(hwnd);
            }
        });
    }

    private bool HostCanHoldGame()
    {
        if (WindowState == WindowState.Minimized || !IsVisible)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return MainViewport.ActualWidth * dpi.DpiScaleX >= WindowArranger.GameClientWidth - 8
            && MainViewport.ActualHeight * dpi.DpiScaleY >= WindowArranger.GameClientHeight - 8;
    }

    private void EnsureRoomForGame()
    {
        if (_docked)
        {
            return;
        }

        ApplyViewportMinimum();
        var dpi = VisualTreeHelper.GetDpi(this);
        var missingWidth = WindowArranger.GameClientWidth / dpi.DpiScaleX - MainViewport.ActualWidth;
        var missingHeight = WindowArranger.GameClientHeight / dpi.DpiScaleY - MainViewport.ActualHeight;
        var work = SystemParameters.WorkArea;
        if (WindowState != WindowState.Normal) return;
        if (missingWidth > 1)
        {
            Width = Math.Min(work.Width, Width + missingWidth);
        }

        if (missingHeight > 1)
        {
            Height = Math.Min(work.Height, Height + missingHeight);
        }
    }

    private void EnsureToolVisible()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void ReleaseEmbedded()
    {
        ResetGameInputPresentation();
        GameEmbedder.ReleaseAll();
        _knownHandles.Clear();
        _stageSignature = "";
    }

    private void CreateTray()
    {
        var icon = LoadAppIcon();
        if (icon == null && !string.IsNullOrEmpty(Environment.ProcessPath))
        {
            try
            {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
            catch (ArgumentException)
            {
            }
        }
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "魔力窗口台",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开窗口台", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(ShowFromTray);
            }
        };
    }

    private void HideToTray()
    {
        ReleaseEmbedded();
        if (_guideWindow.IsVisible)
        {
            _guideWindow.Hide();
        }

        ShowInTaskbar = false;
        Hide();
        if (_tray != null)
        {
            _tray.Visible = true;
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (_config.ShowGuide)
        {
            ShowGuideWindow(false);
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        _stageSignature = "";
        UpdateStage();
        Dispatcher.BeginInvoke(EmbedPrimary, DispatcherPriority.Loaded);
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Close();
    }

    private System.Windows.Controls.Button CreateThumbCard(GameWindow seat, int index)
    {
        var host = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 29, 31)), MinHeight = 78 };
        var label = new TextBlock
        {
            Text = $"{WindowNumber(seat.Handle)}  {WindowLabel(seat)}",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        DockPanel.SetDock(label, Dock.Bottom);
        var panel = new DockPanel();
        panel.Children.Add(label);
        panel.Children.Add(host);
        var button = new System.Windows.Controls.Button
        {
            Width = 168,
            Height = 118,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(4),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch,
            Content = panel,
            Tag = index,
            ContextMenu = WindowMenu(seat),
            ToolTip = SeatShortcut(WindowNumber(seat.Handle))
        };
        button.Click += (_, _) => FocusSeat(index);
        _thumbs.Add(new ThumbView(seat.Handle, host));
        return button;
    }

    private void ApplyThumbnailRects()
    {
        var destination = new WindowInteropHelper(this).Handle;
        if (destination == IntPtr.Zero)
        {
            return;
        }

        var clip = PreviewClip(destination, MiniScroller);
        foreach (var thumb in _thumbs)
        {
            var bounds = WindowThumbs.ClientRect(destination, thumb.Host);
            if (thumb.Host.ActualWidth < 8 || thumb.Host.ActualHeight < 8 || !thumb.Host.IsVisible || !WindowThumbs.Intersects(bounds, clip))
            {
                WindowThumbs.Close(thumb.Thumbnail);
                thumb.Thumbnail = IntPtr.Zero;
                continue;
            }

            if (thumb.Thumbnail == IntPtr.Zero)
            {
                if (!WindowThumbs.TryRegister(destination, thumb.Source, out var created))
                {
                    continue;
                }

                thumb.Thumbnail = created;
            }

            NativeMethods.GetClientRect(thumb.Source, out var source);
            WindowThumbs.Update(thumb.Thumbnail, bounds, source.Right - source.Left, source.Bottom - source.Top, clip);
        }
    }

    private void CloseThumbs()
    {
        foreach (var thumb in _thumbs)
        {
            WindowThumbs.Close(thumb.Thumbnail);
            thumb.Thumbnail = IntPtr.Zero;
        }

        foreach (var slot in _smallSlots)
        {
            WindowThumbs.Close(slot.Thumbnail);
            slot.Thumbnail = IntPtr.Zero;
        }

        _thumbs.Clear();
    }

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        if (_docked)
        {
            ExpandFromDock();
        }
        else
        {
            DockToCorner();
        }
    }

    private void DockToCorner()
    {
        _adjusting = true;
        _normalBounds = new Rect(Left, Top, Width, Height);
        _docked = true;
        _upscaleLive = false;
        _upscaleNoted = false;
        ApplyEnhancedFrame();
        ReleaseEmbedded();
        SideBar.Visibility = Visibility.Collapsed;
        SideColumn.Width = new GridLength(0);
        LayoutHeader.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        DockBar.Visibility = Visibility.Visible;
        LayoutPane.Margin = new Thickness(0);
        Hide();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        MinWidth = 360;
        MinHeight = 260;
        Width = 460;
        Height = 340;
        MoveDockOnScreen();
        Show();
        if (_config.ShowGuide)
        {
            ShowGuideWindow(false);
        }

        WindowState = WindowState.Normal;
        Activate();
        Reveal(DockBar, 0);
        _stageSignature = "";
        UpdateStage();
        Dispatcher.BeginInvoke(() =>
        {
            MoveDockOnScreen();
            EnsureToolVisible();
            _adjusting = false;
            SetStatus("迷你控制台已开启。游戏留在桌面，点预览可切换；展开后自动接入。");
        }, DispatcherPriority.Loaded);
    }

    private void MoveDockOnScreen()
    {
        var work = SystemParameters.WorkArea;
        const double dockWidth = 460;
        const double dockHeight = 340;
        Width = dockWidth;
        Height = dockHeight;
        Left = work.Left + 8;
        Top = Math.Max(work.Top + 8, work.Bottom - dockHeight - 8);
    }

    private void ExpandFromDock()
    {
        _adjusting = true;
        _docked = false;
        Hide();
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        ApplyViewportMinimum();
        ApplySettingsLayout();
        Toolbar.Visibility = Visibility.Visible;
        LayoutHeader.Visibility = Visibility.Visible;
        DockBar.Visibility = Visibility.Collapsed;
        Left = _normalBounds.Left;
        Top = _normalBounds.Top;
        Width = Math.Min(SystemParameters.WorkArea.Width, Math.Max(MinWidth, _normalBounds.Width));
        Height = Math.Min(SystemParameters.WorkArea.Height, Math.Max(MinHeight, _normalBounds.Height));
        Show();
        if (_config.ShowGuide)
        {
            ShowGuideWindow(false);
        }

        WindowState = WindowState.Normal;
        EnsureToolVisible();
        Reveal(SideBar, 0);
        Reveal(LayoutHeader, 30);
        _stageSignature = "";
        UpdateStage();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                EnsureToolVisible();
                ApplyEnhancedFrame();
                _adjusting = false;
                if (EmbedPrimary())
                {
                    SetStatus("主窗口已嵌进主区域。");
                }
            }
            finally
            {
                _adjusting = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private List<GameWindow> Seats(IReadOnlyList<GameWindow>? windows = null)
    {
        windows ??= GameWindowScanner.Scan(ProcessNames().Distinct(StringComparer.OrdinalIgnoreCase), _selfPid, SelectedHandles());
        var seats = windows.Where(window => !IsDetached(window) && (_windowFilter?.Invoke(window) ?? true)).ToList();
        _windowSeats.Update(seats);
        foreach (var handle in _windowLabels.Keys.Where(handle => !seats.Any(window => window.Handle == handle)).ToArray())
            _windowLabels.Remove(handle);
        return seats.OrderBy(window => WindowNumber(window.Handle)).ToList();
    }

    private void Arrange_Click(object sender, RoutedEventArgs e)
    {
        ArrangeCurrent(_primaryHandle);
    }

    private void ArrangeCurrent(IntPtr preferredPrimary)
    {
        RefreshStatus();
        var seats = Seats();
        if (seats.Count == 0)
        {
            SetStatus("还没有游戏窗口。先在官方登录器里进入游戏。");
            return;
        }

        var primary = preferredPrimary;
        if (primary == IntPtr.Zero || seats.All(seat => seat.Handle != primary))
        {
            primary = _primaryHandle != IntPtr.Zero && seats.Any(seat => seat.Handle == _primaryHandle)
                ? _primaryHandle
                : seats[0].Handle;
        }

        _primaryHandle = primary;
        if (EmbedPrimary())
        {
            FocusPrimaryInput();
            SetStatus("主窗口已嵌进主区域。");
        }

        EnsureToolVisible();
        RefreshStatus();
    }

    private void FocusSeat(int index)
    {
        var seats = Seats();
        if (seats.Count == 0)
        {
            SetStatus("没有可切换的游戏窗口。");
            return;
        }

        var seat = seats.FirstOrDefault(window => WindowNumber(window.Handle) == index + 1);
        if (seat == null)
        {
            SetStatus($"窗口 {index + 1} 尚未接入，当前窗口保持不变。");
            return;
        }
        SwapMain(seat.Handle);
        EnsureToolVisible();
        FocusPrimaryInput();
    }

    private int ApplyHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        var chords = HotkeyCatalog.Normalize(_config.Hotkeys);
        if (HotkeyBox.IsChecked != true)
        {
            ShowHotkeyState("热键已关", false);
            return 0;
        }

        var source = PresentationSource.FromVisual(this) as HwndSource;
        if (source == null)
        {
            ShowHotkeyState("热键不可用", true);
            return 0;
        }

        var plan = HotkeyCatalog.Plan(chords);
        var host = new HotkeyHost(source);
        host.Pressed += OnHotkey;
        var failed = host.Register(plan.Slots);
        failed.AddRange(plan.DuplicateIds);
        if (host.Count == 0)
        {
            host.Dispose();
        }
        else
        {
            _hotkeys = host;
        }

        var summary = HotkeyCatalog.Summary(chords);
        if (failed.Count == 0)
        {
            ShowHotkeyState(summary, false);
            return 0;
        }

        var names = string.Join("、", failed.Distinct().Select(HotkeyCatalog.LabelForId));
        ShowHotkeyState(summary + "  ·  未生效：" + names, true);
        SetStatus(names + " 没能注册，可能被占用或重复。打开「设置 → 编辑热键」更换组合键。");
        return failed.Count;
    }

    private void ShowHotkeyState(string text, bool warn)
    {
        HotkeyStateText.Text = text;
        HotkeyStateText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "WarnBrush" : "GoldBrush");
        foreach (var slot in _smallSlots)
            slot.Shortcut.Text = SeatShortcut(WindowNumber(slot.Game));
    }

    private void OnHotkey(int id)
    {
        Dispatcher.Invoke(() =>
        {
            if (id is >= 1 and <= 9)
            {
                FocusSeat(id - 1);
                return;
            }

            if (id == HotkeyHost.NextId)
            {
                var seats = Seats();
                if (seats.Count == 0)
                {
                    return;
                }

                var current = seats.FindIndex(seat => seat.Handle == _primaryHandle);
                var next = seats[current < 0 ? 0 : (current + 1) % seats.Count];
                SwapMain(next.Handle);
                EnsureToolVisible();
                FocusPrimaryInput();
                return;
            }

            if (id == HotkeyHost.ArrangeId)
            {
                ArrangeCurrent(_primaryHandle);
            }
        });
    }

    private void BrowseLauncher_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExe("选择官方登录器");
        if (path == null)
        {
            return;
        }

        LauncherPathBox.Text = path;
        SaveConfig();
        RefreshStatus();
    }

    private static string? BrowseExe(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "程序|*.exe|所有文件|*.*"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OpenLauncher_Click(object sender, RoutedEventArgs e)
    {
        var path = LauncherPathBox.Text.Trim();
        if (!File.Exists(path))
        {
            path = BrowseExe("选择官方登录器");
            if (path == null) return;
            LauncherPathBox.Text = path;
            SaveConfig();
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
                UseShellExecute = true
            });
            _waitingForGame = true;
            RefreshStatus();
            SetStatus("已打开官方登录器。账号密码在登录器里输入，进入游戏后窗口会嵌到这里。");
        }
        catch (Exception ex)
        {
            SetStatus("打不开登录器：" + ex.Message);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        var count = Seats().Count;
        SetStatus(count == 0
            ? "已刷新。还没有游戏窗口。"
            : $"已刷新。游戏窗口 {count} 个。");
    }

    private void ToggleGuide_Click(object sender, RoutedEventArgs e)
    {
        ApplyGuideLayout(!_config.ShowGuide);
        SaveConfig();
    }

    private void DarkBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.DarkMode = DarkBox.IsChecked == true;
        DeskTheme.Apply(_config.DarkMode);
        ApplyGuideSurface();
        SaveConfig();
    }

    internal AppConfig Config => _config;

    internal void NoteGuideBounds(double left, double top, double width, double height, bool topmost)
    {
        if (_loading)
        {
            return;
        }

        _config.GuideLeft = left;
        _config.GuideTop = top;
        _config.GuideWidth = Math.Max(360, (int)Math.Round(width));
        _config.GuideHeight = Math.Max(480, (int)Math.Round(height));
        _config.GuideTopmost = topmost;
        _guideSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        if (_guideSaveTimer.Tag == null)
        {
            _guideSaveTimer.Tag = true;
            _guideSaveTimer.Tick += (_, _) =>
            {
                _guideSaveTimer.Stop();
                SaveConfig();
            };
        }

        _guideSaveTimer.Stop();
        _guideSaveTimer.Start();
    }

    internal void NotifyGuideHidden()
    {
        if (_allowClose || _loading)
        {
            return;
        }

        _config.ShowGuide = false;
        GuideNav.IsChecked = false;
        SaveConfig();
    }

    private void ShowGuideWindow(bool activate)
    {
        _guideWindow.EnsurePlaced();
        _guideWindow.ShowActivated = activate;
        if (!_guideWindow.IsVisible)
        {
            _guideWindow.Show();
        }
        else if (activate)
        {
            _guideWindow.Activate();
        }

        if (_guideStarted)
        {
            return;
        }

        WarnMissingGuideRuntime();
        _ = StartGuideAsync();
    }

    private void ApplyGuideLayout(bool show)
    {
        _config.ShowGuide = show;
        GuideNav.IsChecked = show;
        if (!IsLoaded)
        {
            return;
        }

        if (!show)
        {
            if (_guideWindow.IsVisible)
            {
                _guideWindow.Hide();
            }

            return;
        }

        ShowGuideWindow(true);
    }

    private void EnhanceBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveConfig();
        _upscaleLive = false;
        _upscaleNoted = false;
        if (EnhanceBox.IsChecked == true)
        {
            FsrSurface.Reset();
        }
        else
        {
            FsrSurface.Hide();
        }

        ApplyEnhancedFrame();
        ApplyEnhance(true);
        EmbedIntoHosts();
    }

    private void ScheduleEnhancedFit()
    {
        if (!_config.EnhanceMainWindow || _docked || _adjusting)
        {
            return;
        }

        _enhanceTimer.Stop();
        _enhanceTimer.Start();
    }

    private void ApplyEnhancedFrame()
    {
        if (_docked)
        {
            return;
        }

        if (_upscaleLive && TryFitPane(out var fittedWidth, out var fittedHeight))
        {
            _fittedWidth = fittedWidth;
            _fittedHeight = fittedHeight;
        }
        else
        {
            _fittedWidth = WindowArranger.GameClientWidth;
            _fittedHeight = WindowArranger.GameClientHeight;
        }

        var scale = VisualTreeHelper.GetDpi(this);
        FitViewport(
            scale.DpiScaleX,
            scale.DpiScaleY,
            MainViewport.BorderThickness.Left + MainViewport.BorderThickness.Right,
            MainViewport.BorderThickness.Top + MainViewport.BorderThickness.Bottom);
    }

    private void FitViewport(double scaleX, double scaleY, double borderX, double borderY)
    {
        var shown = ShownGameSize();
        MainViewport.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        MainViewport.VerticalAlignment = VerticalAlignment.Center;
        MainViewport.Width = shown.Width / scaleX + borderX;
        MainViewport.Height = shown.Height / scaleY + borderY;
    }

    private bool TryFitPane(out int width, out int height)
    {
        width = WindowArranger.GameClientWidth;
        height = WindowArranger.GameClientHeight;
        if (MainPane.ActualWidth < 32 || MainPane.ActualHeight < 32)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var caption = MainCaption.ActualHeight + MainCaption.Margin.Bottom;
        var innerWidth = MainPane.ActualWidth * dpi.DpiScaleX;
        var innerHeight = Math.Max(0, MainPane.ActualHeight - caption) * dpi.DpiScaleY;
        var fitted = WindowArranger.FitClient(innerWidth, innerHeight);
        width = fitted.Width;
        height = fitted.Height;
        return true;
    }

    private (int Width, int Height) ShownGameSize()
    {
        if (_upscaleLive)
        {
            return (_fittedWidth, _fittedHeight);
        }

        var width = Math.Max(WindowArranger.GameClientWidth, _fittedWidth);
        var height = Math.Max(WindowArranger.GameClientHeight, _fittedHeight);
        if (_primaryHandle != IntPtr.Zero && NativeMethods.GetClientRect(_primaryHandle, out var client))
        {
            var gameWidth = Math.Max(0, client.Right - client.Left);
            var gameHeight = Math.Max(0, client.Bottom - client.Top);
            if (gameWidth >= 640 && gameHeight >= 480 && (gameWidth + 16 < width || gameHeight + 16 < height))
            {
                return (gameWidth, gameHeight);
            }
        }

        return (width, height);
    }

    private void ApplyEnhance(bool announce)
    {
        if (!_manageEnhancementFiles) return;
        var directory = GameInstallDirectory.FindDirectory(_config.GamePath, LauncherPathBox.Text.Trim());
        var message = GameEnhance.Apply(
            directory,
            _config.EnhanceMainWindow,
            WindowArranger.GameClientWidth,
            WindowArranger.GameClientHeight,
            GameExe());
        if (!announce)
        {
            return;
        }

        SetStatus(message);
    }

    private void LiveSmallBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveConfig();
        _freezeGeneration++;
        foreach (var slot in _smallSlots)
        {
            slot.Still = null;
            if (_config.LiveSmallPreview != false)
            {
                slot.Preview.Source = null;
            }
        }

        RefreshSmallPreviews();
        if (_config.LiveSmallPreview == false)
        {
            _ = RecaptureLater(_freezeGeneration);
        }
    }

    private void KeepSmallWindowsCovered()
    {
        if (_loading || _docked || _adjusting || _placing || !IsVisible || WindowState == WindowState.Minimized)
        {
            RefreshSmallPreviews();
            return;
        }

        if (TryCoveredOrigin(out var x, out var y))
        {
            if (UseCapturedMain) GameEmbedder.MoveStaged(_primaryHandle, x, y);
            foreach (var slot in _smallSlots)
            {
                GameEmbedder.MoveStaged(slot.Game, x, y);
            }
        }

        RefreshSmallPreviews();
    }

    private bool TryCoveredOrigin(out int x, out int y)
    {
        x = 0;
        y = 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        x = rect.Left + 12;
        y = rect.Top + 48;
        return true;
    }

    private static (int Width, int Height) SmallGameSize(IntPtr game)
    {
        var width = WindowArranger.GameClientWidth;
        var height = WindowArranger.GameClientHeight;
        if (NativeMethods.GetClientRect(game, out var client))
        {
            var currentWidth = Math.Max(0, client.Right - client.Left);
            var currentHeight = Math.Max(0, client.Bottom - client.Top);
            if (currentWidth >= width - 8 && currentHeight >= height - 8)
            {
                return (currentWidth, currentHeight);
            }
        }

        return (width, height);
    }

    private void RefreshSmallPreviews()
    {
        if (_docked || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        var destination = new WindowInteropHelper(this).Handle;
        if (destination == IntPtr.Zero)
        {
            return;
        }

        var live = _config.LiveSmallPreview != false;
        var clip = PreviewClip(destination, ThumbScroller);
        foreach (var slot in _smallSlots)
        {
            if (slot.Game == IntPtr.Zero || !NativeMethods.IsWindow(slot.Game) || slot.Target.ActualWidth < 8 || slot.Target.ActualHeight < 8)
            {
                continue;
            }
            var bounds = WindowThumbs.ClientRect(destination, slot.Target);
            if (!WindowThumbs.Intersects(bounds, clip))
            {
                WindowThumbs.Close(slot.Thumbnail);
                slot.Thumbnail = IntPtr.Zero;
                continue;
            }

            if (!live)
            {
                WindowThumbs.Close(slot.Thumbnail);
                slot.Thumbnail = IntPtr.Zero;
                slot.Still ??= WindowThumbs.CaptureClient(slot.Game);
                slot.Preview.Source = slot.Still;
                slot.Preview.Visibility = slot.Still == null ? Visibility.Collapsed : Visibility.Visible;
                continue;
            }

            slot.Preview.Source = null;
            slot.Still = null;
            slot.Preview.Visibility = Visibility.Collapsed;
            if (slot.Thumbnail == IntPtr.Zero)
            {
                if (!WindowThumbs.TryRegister(destination, slot.Game, out var created) || created == IntPtr.Zero)
                {
                    var shot = WindowThumbs.CaptureClient(slot.Game);
                    if (shot != null)
                    {
                        slot.Preview.Source = shot;
                        slot.Preview.Visibility = Visibility.Visible;
                    }

                    continue;
                }

                slot.Thumbnail = created;
            }

            var sourceWidth = 0;
            var sourceHeight = 0;
            if (NativeMethods.GetClientRect(slot.Game, out var client))
            {
                sourceWidth = Math.Max(0, client.Right - client.Left);
                sourceHeight = Math.Max(0, client.Bottom - client.Top);
            }

            WindowThumbs.Update(slot.Thumbnail, bounds, sourceWidth, sourceHeight, clip);
        }
    }

    private static NativeMethods.RectNative PreviewClip(IntPtr destination, ScrollViewer viewer)
    {
        var viewport = viewer.Template?.FindName("PART_ScrollContentPresenter", viewer) as FrameworkElement ?? viewer;
        return WindowThumbs.ClientRect(destination, viewport);
    }

    private async Task RecaptureLater(int generation)
    {
        await Task.Delay(450);
        if (generation != _freezeGeneration || _config.LiveSmallPreview != false)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (generation != _freezeGeneration || _config.LiveSmallPreview != false)
            {
                return;
            }

            foreach (var slot in _smallSlots)
            {
                slot.Still = null;
            }

            RefreshSmallPreviews();
        });
    }

    private void HotkeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SaveConfig();
        ApplyHotkeys();
    }

    private void EditHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        ShowHotkeyState("正在编辑热键", false);
        var accepted = false;
        try
        {
            var dialog = new HotkeyEditorWindow(HotkeyCatalog.Normalize(_config.Hotkeys))
            {
                Owner = this
            };
            accepted = dialog.ShowDialog() == true;
            if (accepted)
            {
                _config.Hotkeys = dialog.Export();
                SaveConfig();
            }
        }
        finally
        {
            var failed = ApplyHotkeys();
            if (accepted && failed == 0)
            {
                SetStatus("热键已更新。");
            }
        }
    }

    private void SyncGameCloak()
    {
        if (_config.EnhanceMainWindow && FsrSurface.Failed && _mainHost != null
            && GameEmbedder.IsReady(_primaryHandle) && !GameEmbedder.IsAttached(_primaryHandle, _mainHost.HostHandle))
        {
            _upscaleLive = false;
            Dispatcher.BeginInvoke(() => { ApplyEnhancedFrame(); EmbedIntoHosts(); }, DispatcherPriority.Loaded);
            return;
        }
        if (_docked || !IsVisible || WindowState == WindowState.Minimized
            || _mainHost == null || _primaryHandle == IntPtr.Zero
            || !IsMainConnected)
        {
            if ((_mainHost != null && _mainHost.KeyTarget != IntPtr.Zero) || _upscaleLive || _appliedCursorMode == CursorModeGame)
                ResetGameInputPresentation();
            else
            {
                GameCursorCloak.Hide();
                FsrSurface.Hide();
            }
            return;
        }

        var host = _mainHost.HostHandle;
        if (UseCapturedMain && !GameEmbedder.IsReady(_primaryHandle))
        {
            // Layout can defer re-placement. Never start capture while the source
            // is still a child window from the non-enhanced mode.
            if (!_placing) Dispatcher.BeginInvoke(() => EmbedIntoHosts(), DispatcherPriority.Loaded);
            return;
        }
        if (!_config.EnhanceMainWindow)
        {
            if (_upscaleLive || FsrSurface.IsPresenting)
            {
                _upscaleLive = false;
                FsrSurface.Hide();
                Dispatcher.BeginInvoke(() => { ApplyEnhancedFrame(); EmbedIntoHosts(); });
            }

            GameCursorCloak.Sync(host, _primaryHandle);
            return;
        }

        FsrSurface.Sync(host, _primaryHandle, (float)_config.EnhanceSharpness);
        if (FsrSurface.Failed)
        {
            if (!_upscaleNoted)
            {
                _upscaleNoted = true;
                _upscaleLive = false;
                SetStatus(FsrSurface.LastError);
                Dispatcher.BeginInvoke(() => { ApplyEnhancedFrame(); EmbedIntoHosts(); });
            }

            GameCursorCloak.Sync(host, _primaryHandle);
            return;
        }

        if (FsrSurface.IsPresenting)
        {
            GameCursorCloak.Hide();
            if (!_upscaleLive)
            {
                _upscaleLive = true;
                _upscaleNoted = false;
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyEnhancedFrame();
                    EmbedIntoHosts();
                    SetStatus("画质增强开着。画面按主区域的 4:3 铺满。");
                });
            }

            return;
        }

        GameCursorCloak.Sync(host, _primaryHandle);
    }

    private void SharpnessSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SharpnessLabel == null)
        {
            return;
        }

        var clarity = Math.Clamp(SharpnessSlider.Value, 0, 1);
        SharpnessLabel.Text = $"锐化强度 {Math.Round(clarity * 100)}%";
        _config.EnhanceSharpness = clarity;
        FsrSurface.SetSharpness((float)clarity);
        SaveConfig();
    }

    private IntPtr OnLowLevelMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<LowLevelMouse>(lParam);
            SyncSystemCursor(info.Point);
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void SyncSystemCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        SyncSystemCursor(point);
    }

    private void SyncSystemCursor(NativePoint point)
    {
        var mode = IsMainConnected && DeskIsForeground()
            ? PointerInsideMainGame(point) ? CursorModeGame : CursorModeDesk
            : CursorModeSystem;
        if (mode == _appliedCursorMode || !EnsureCursorMasters())
        {
            return;
        }

        ApplyCursorMode(mode);
        _appliedCursorMode = mode;
        _ = SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(CursorArrow)));
    }

    private void ResetGameInputPresentation()
    {
        GameCursorCloak.Hide();
        FsrSurface.Hide();
        if (_mainHost != null) _mainHost.KeyTarget = IntPtr.Zero;
        _upscaleLive = false;
        RestoreSystemCursors();
        System.Windows.Input.Mouse.UpdateCursor();
        GamePointer.RestoreArrow();
    }

    private void RestoreSystemCursors()
    {
        if (!_cursorMastersReady || _appliedCursorMode == CursorModeSystem)
        {
            _appliedCursorMode = CursorModeSystem;
            return;
        }

        ApplyCursorMode(CursorModeSystem);
        _appliedCursorMode = CursorModeSystem;
    }

    private bool PointerInsideMainGame(NativePoint point)
    {
        if (!TryMainGameRect(out var rect))
        {
            return false;
        }

        return point.X >= rect.Left && point.Y >= rect.Top && point.X < rect.Right && point.Y < rect.Bottom;
    }

    private bool TryMainGameRect(out NativeMethods.RectNative rect)
    {
        rect = default;
        if (_mainHost == null || _primaryHandle == IntPtr.Zero || _mainHost.HostHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!GameEmbedder.IsAttached(_primaryHandle, _mainHost.HostHandle)
            || !NativeMethods.GetWindowRect(_mainHost.HostHandle, out var host)
            || !NativeMethods.GetWindowRect(_primaryHandle, out var game))
        {
            return false;
        }

        var left = Math.Max(game.Left, host.Left);
        var top = Math.Max(game.Top, host.Top);
        var right = Math.Min(game.Right, host.Right);
        var bottom = Math.Min(game.Bottom, host.Bottom);
        if (right - left < 8 || bottom - top < 8)
        {
            return false;
        }

        rect = new NativeMethods.RectNative { Left = left, Top = top, Right = right, Bottom = bottom };
        return true;
    }

    private bool EnsureCursorMasters()
    {
        if (_cursorMastersReady)
        {
            return _blankCursor != IntPtr.Zero;
        }

        for (var i = 0; i < SystemCursorIds.Length; i++)
        {
            _cursorMasters[i] = CopyIcon(LoadCursor(IntPtr.Zero, new IntPtr(SystemCursorIds[i])));
            if (_cursorMasters[i] == IntPtr.Zero)
            {
                return false;
            }
        }

        _blankCursor = CreateBlankCursor();
        _cursorMastersReady = _blankCursor != IntPtr.Zero;
        return _cursorMastersReady;
    }

    private void ApplyCursorMode(int mode)
    {
        for (var i = 0; i < SystemCursorIds.Length; i++)
        {
            var source = mode switch
            {
                CursorModeGame => _blankCursor,
                CursorModeDesk when SystemCursorIds[i] is CursorWait or CursorStarting => _cursorMasters[0],
                _ => _cursorMasters[i]
            };
            var copy = CopyIcon(source);
            if (copy != IntPtr.Zero)
            {
                _ = SetSystemCursor(copy, SystemCursorIds[i]);
            }
        }
    }

    private static IntPtr CreateBlankCursor()
    {
        var width = GetSystemMetrics(13);
        var height = GetSystemMetrics(14);
        if (width < 16 || height < 16)
        {
            width = 32;
            height = 32;
        }

        var rowBytes = ((width + 15) / 16) * 2;
        var mask = new byte[rowBytes * height];
        Array.Fill(mask, (byte)0xFF);
        var clear = new byte[mask.Length];
        return CreateCursor(GetModuleHandle(null), 0, 0, width, height, mask, clear);
    }

    private static void EnableDoubleClick(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetClassLongPtr(hwnd, -26);
        if ((style.ToInt64() & 0x0008) != 0)
        {
            return;
        }

        _ = SetClassLongPtr(hwnd, -26, new IntPtr(style.ToInt64() | 0x0008));
    }

    private bool DeskIsForeground()
    {
        if (_deskHandle == IntPtr.Zero)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        for (var hop = 0; hop < 6 && foreground != IntPtr.Zero; hop++)
        {
            if (foreground == _deskHandle || GetAncestor(foreground, 2) == _deskHandle)
            {
                return true;
            }

            var owner = GetWindow(foreground, 4);
            if (owner == IntPtr.Zero || owner == foreground)
            {
                return false;
            }

            foreground = owner;
        }

        return false;
    }

    private const int CursorModeSystem = 0;
    private const int CursorModeDesk = 1;
    private const int CursorModeGame = 2;
    private const uint CursorArrow = 32512;
    private const uint CursorWait = 32514;
    private const uint CursorStarting = 32650;
    private static readonly uint[] SystemCursorIds = [32512, 32513, 32514, 32649, 32650];

    private delegate IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouse
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr cursor, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateCursor(IntPtr instance, int xHot, int yHot, int width, int height, byte[] andMask, byte[] xorMask);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int id, MouseHookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    private IntPtr OnDeskMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (0x0100 or 0x0101 or 0x0102 or 0x0104 or 0x0105 or 0x0106))
        {
            return IntPtr.Zero;
        }

        if (_primaryHandle == IntPtr.Zero || _mainHost == null || !IsMainConnected)
        {
            return IntPtr.Zero;
        }

        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or PasswordBox or System.Windows.Controls.ComboBox or System.Windows.Controls.ListBox or System.Windows.Controls.ListView)
        {
            return IntPtr.Zero;
        }

        if (msg is 0x0104 or 0x0105 && wParam == new IntPtr(0x73))
        {
            return IntPtr.Zero;
        }

        _ = PostMessage(_primaryHandle, (uint)msg, wParam, lParam);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void SetStatus(string text)
    {
        if (StatusText.Text == text)
        {
            return;
        }

        StatusText.Text = text;
        StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static System.Drawing.Icon? LoadAppIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
        if (resource?.Stream == null)
        {
            return null;
        }

        using (resource.Stream)
        {
            return new System.Drawing.Icon(resource.Stream);
        }
    }

    private static void Reveal(UIElement element, int delayMs)
    {
        if (element.Visibility != Visibility.Visible)
        {
            return;
        }

        var translate = element.RenderTransform as TranslateTransform;
        if (translate == null)
        {
            translate = new TranslateTransform();
            element.RenderTransform = translate;
        }

        element.Opacity = 0;
        translate.Y = 8;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var delay = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = delay,
            EasingFunction = ease
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
        {
            BeginTime = delay,
            EasingFunction = ease
        });
    }

    private void SetMainMark(int number, string title)
    {
        MainThumbLabel.Text = title;
        if (number <= 0)
        {
            _shownSeat = -1;
            MainBadge.Visibility = Visibility.Collapsed;
            MainViewport.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            return;
        }

        var changed = number != _shownSeat;
        _shownSeat = number;
        var brush = SeatBrush(number);
        MainBadge.Visibility = Visibility.Visible;
        MainBadge.Background = brush;
        MainBadgeText.Text = number.ToString();
        MainViewport.BorderBrush = brush;
        if (changed)
        {
            AnimateMain();
        }
    }

    private void AnimateMain()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MainCaption.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease
        });
        if (MainBadge.RenderTransform is not ScaleTransform scale)
        {
            MainBadge.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            scale = new ScaleTransform(1, 1);
            MainBadge.RenderTransform = scale;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease
        });
    }

    private static void PaintSeat(Border badge, TextBlock text, int number)
    {
        badge.Background = SeatBrush(number);
        text.Text = number.ToString();
    }

    private static readonly SolidColorBrush[] SeatBrushes = CreateSeatBrushes();

    private static SolidColorBrush[] CreateSeatBrushes()
    {
        var colors = new[]
        {
            System.Windows.Media.Color.FromRgb(196, 161, 90),
            System.Windows.Media.Color.FromRgb(107, 163, 214),
            System.Windows.Media.Color.FromRgb(208, 122, 106),
            System.Windows.Media.Color.FromRgb(125, 171, 110),
            System.Windows.Media.Color.FromRgb(176, 140, 196),
            System.Windows.Media.Color.FromRgb(91, 184, 176),
            System.Windows.Media.Color.FromRgb(212, 146, 90),
            System.Windows.Media.Color.FromRgb(208, 128, 160),
            System.Windows.Media.Color.FromRgb(138, 154, 106)
        };
        var brushes = new SolidColorBrush[colors.Length];
        for (var index = 0; index < colors.Length; index++)
        {
            var brush = new SolidColorBrush(colors[index]);
            brush.Freeze();
            brushes[index] = brush;
        }

        return brushes;
    }

    private static SolidColorBrush SeatBrush(int number)
    {
        var index = number <= 0 ? 0 : (number - 1) % SeatBrushes.Length;
        return SeatBrushes[index];
    }

    private delegate void ForegroundProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, ForegroundProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private sealed class SmallSlot(IntPtr game, FrameworkElement target, Border frame, System.Windows.Controls.Image preview)
    {
        public IntPtr Game { get; set; } = game;
        public FrameworkElement Target { get; } = target;
        public Border Frame { get; } = frame;
        public System.Windows.Controls.Image Preview { get; } = preview;
        public IntPtr Thumbnail { get; set; }
        public ImageSource? Still { get; set; }
        public TextBlock Label { get; set; } = null!;
        public Border Badge { get; set; } = null!;
        public TextBlock BadgeText { get; set; } = null!;
        public System.Windows.Controls.Button Button { get; set; } = null!;
        public TextBlock Shortcut { get; set; } = null!;
    }

    private sealed class ThumbView(IntPtr source, FrameworkElement host)
    {
        public IntPtr Source { get; } = source;
        public FrameworkElement Host { get; } = host;
        public IntPtr Thumbnail { get; set; }
    }
}
