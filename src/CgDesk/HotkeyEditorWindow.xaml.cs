using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CgDesk.Models;
using CgDesk.Services;

namespace CgDesk;

public partial class HotkeyEditorWindow : Window
{
    private readonly List<Row> _rows = [];

    public HotkeyEditorWindow(IReadOnlyList<HotkeyChord> chords)
    {
        InitializeComponent();
        Build(chords);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DeskTheme.ApplyTitleBar(this);
    }

    public List<HotkeyChord> Export()
    {
        return _rows.Select(row => new HotkeyChord
        {
            Action = row.Action,
            Modifiers = row.Modifiers,
            Key = row.Key
        }).ToList();
    }

    private void Build(IReadOnlyList<HotkeyChord> chords)
    {
        Rows.Children.Clear();
        _rows.Clear();
        var normalized = HotkeyCatalog.Normalize(chords);
        foreach (var action in HotkeyCatalog.Actions)
        {
            var chord = normalized.First(item => item.Action == action.Name);
            var row = CreateRow(action, chord.Modifiers, chord.Key);
            _rows.Add(row);
            Rows.Children.Add(row.Root);
        }

        var bound = normalized.Where(chord => chord.Key != 0 && chord.Modifiers != 0).Select(chord => chord.Modifiers).Distinct().ToList();
        SetModifierChecks(bound.Count == 1 ? bound[0] : HotkeyCatalog.DefaultModifiers);
        ShowMessage("改完点保存。没改的条目会保持现在的按键。", false);
    }

    private Row CreateRow(HotkeyAction action, int modifiers, int key)
    {
        var label = new TextBlock
        {
            Text = action.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 108
        };
        var text = new TextBlock
        {
            Text = HotkeyCatalog.Format(modifiers, key),
            VerticalAlignment = VerticalAlignment.Center
        };
        var box = new Border
        {
            Background = Brush("FieldBrush"),
            BorderBrush = Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Focusable = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = text
        };
        var root = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(box, 1);
        label.Margin = new Thickness(0, 0, 12, 0);
        root.Children.Add(label);
        root.Children.Add(box);

        var row = new Row(action.Name, root, box, text)
        {
            Modifiers = modifiers,
            Key = key
        };
        KeyboardNavigation.SetIsTabStop(box, true);
        box.GotKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = Brush("GoldBrush");
            ShowMessage("正在设置「" + action.Label + "」。按下组合键替换，Delete 清除，Esc 退出录入。", false);
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            box.BorderBrush = Brush("LineBrush");
            text.Text = HotkeyCatalog.Format(row.Modifiers, row.Key);
        };
        box.MouseLeftButtonDown += (_, args) =>
        {
            box.Focus();
            args.Handled = true;
        };
        return row;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var row = FocusedRow();
        if (row == null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var gesture = ReadModifiers();
        if (key == Key.F4 && gesture == ModifierKeys.Alt)
        {
            return;
        }

        if (key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat || IsModifier(key))
        {
            return;
        }

        if (key == Key.Escape && gesture == ModifierKeys.None)
        {
            CancelButton.Focus();
            return;
        }

        if (key is Key.Back or Key.Delete && gesture == ModifierKeys.None)
        {
            row.Modifiers = 0;
            row.Key = 0;
            row.Text.Text = "未设置";
            ShowMessage("已清除「" + HotkeyCatalog.Label(row.Action) + "」。", false);
            return;
        }

        var mods = ToNativeModifiers(gesture);
        if (mods == 0)
        {
            ShowMessage("请配合 Ctrl、Alt、Shift 或 Win，避免把单独按键占掉。", true);
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            ShowMessage("这个键不能用作热键。", true);
            return;
        }

        row.Modifiers = mods;
        row.Key = virtualKey;
        row.Text.Text = HotkeyCatalog.Format(mods, virtualKey);
        ShowMessage("已录上「" + HotkeyCatalog.Label(row.Action) + "」：" + row.Text.Text + "。", false);
    }

    private void ApplyModifiers_Click(object sender, RoutedEventArgs e)
    {
        var mods = SelectedModifiers();
        if (mods == 0)
        {
            ShowMessage("至少留一个修饰键。", true);
            return;
        }

        foreach (var row in _rows)
        {
            if (row.Key == 0)
            {
                continue;
            }

            row.Modifiers = mods;
            row.Text.Text = HotkeyCatalog.Format(row.Modifiers, row.Key);
        }

        var duplicate = HotkeyCatalog.DuplicateMessage(Export());
        ShowMessage(duplicate ?? "修饰键已换成 " + HotkeyCatalog.FormatModifiers(mods) + "，按键没动。点保存后生效。", duplicate != null);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            var chord = HotkeyCatalog.CreateDefault(row.Action);
            row.Modifiers = chord.Modifiers;
            row.Key = chord.Key;
            row.Text.Text = HotkeyCatalog.Format(chord);
        }

        SetModifierChecks(HotkeyCatalog.DefaultModifiers);
        ShowMessage("已恢复成 Ctrl+Alt+1~9、Ctrl+Alt+→、Ctrl+Alt+0。点保存后生效。", false);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var chords = Export();
        var duplicate = HotkeyCatalog.DuplicateMessage(chords);
        if (duplicate != null)
        {
            ShowMessage(duplicate, true);
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ShowMessage("窗口还没准备好，稍后再保存。", true);
            return;
        }

        var plan = HotkeyCatalog.Plan(chords);
        var failed = HotkeyHost.Probe(hwnd, plan.Slots);
        if (failed.Count > 0)
        {
            ShowMessage(HotkeyCatalog.OccupiedMessage(failed), true);
            return;
        }

        DialogResult = true;
    }

    private int SelectedModifiers()
    {
        var mods = 0;
        if (ModCtrlBox.IsChecked == true)
        {
            mods |= (int)NativeMethods.ModControl;
        }

        if (ModAltBox.IsChecked == true)
        {
            mods |= (int)NativeMethods.ModAlt;
        }

        if (ModShiftBox.IsChecked == true)
        {
            mods |= (int)NativeMethods.ModShift;
        }

        if (ModWinBox.IsChecked == true)
        {
            mods |= (int)NativeMethods.ModWin;
        }

        return mods;
    }

    private void SetModifierChecks(int mods)
    {
        ModCtrlBox.IsChecked = (mods & (int)NativeMethods.ModControl) != 0;
        ModAltBox.IsChecked = (mods & (int)NativeMethods.ModAlt) != 0;
        ModShiftBox.IsChecked = (mods & (int)NativeMethods.ModShift) != 0;
        ModWinBox.IsChecked = (mods & (int)NativeMethods.ModWin) != 0;
    }

    private void ShowMessage(string text, bool warn)
    {
        EditorMessage.Text = text;
        EditorMessage.Foreground = Brush(warn ? "WarnBrush" : "MutedBrush");
    }

    private System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);

    private static ModifierKeys ReadModifiers()
    {
        var mods = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            mods |= ModifierKeys.Control;
        }

        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
        {
            mods |= ModifierKeys.Alt;
        }

        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
        {
            mods |= ModifierKeys.Shift;
        }

        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            mods |= ModifierKeys.Windows;
        }

        return mods;
    }

    private static int ToNativeModifiers(ModifierKeys keys)
    {
        var mods = 0;
        if ((keys & ModifierKeys.Alt) != 0)
        {
            mods |= (int)NativeMethods.ModAlt;
        }

        if ((keys & ModifierKeys.Control) != 0)
        {
            mods |= (int)NativeMethods.ModControl;
        }

        if ((keys & ModifierKeys.Shift) != 0)
        {
            mods |= (int)NativeMethods.ModShift;
        }

        if ((keys & ModifierKeys.Windows) != 0)
        {
            mods |= (int)NativeMethods.ModWin;
        }

        return mods;
    }

    private Row? FocusedRow()
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current != null)
        {
            var row = _rows.FirstOrDefault(item => item.Box == current);
            if (row != null)
            {
                return row;
            }

            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsModifier(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }

    private sealed class Row
    {
        public Row(string action, Grid root, Border box, TextBlock text)
        {
            Action = action;
            Root = root;
            Box = box;
            Text = text;
        }

        public string Action { get; }
        public Grid Root { get; }
        public Border Box { get; }
        public TextBlock Text { get; }
        public int Modifiers { get; set; }
        public int Key { get; set; }
    }
}
