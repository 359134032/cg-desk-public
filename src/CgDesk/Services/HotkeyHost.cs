using System.Windows.Interop;

namespace CgDesk.Services;

internal readonly record struct HotkeySlot(int Id, uint Modifiers, uint VirtualKey);

internal sealed class HotkeyHost : IDisposable
{
    public const int NextId = 20;
    public const int ArrangeId = 21;

    private readonly HwndSource _source;
    private readonly List<int> _ids = [];
    private bool _disposed;

    public HotkeyHost(HwndSource source)
    {
        _source = source;
        _source.AddHook(WndProc);
    }

    public event Action<int>? Pressed;

    public int Count => _ids.Count;

    public List<int> Register(IReadOnlyList<HotkeySlot> slots)
    {
        var failed = new List<int>();
        foreach (var slot in slots)
        {
            if (slot.VirtualKey == 0)
            {
                continue;
            }

            if (!TryRegister(slot.Id, slot.Modifiers, slot.VirtualKey))
            {
                failed.Add(slot.Id);
            }
        }

        return failed;
    }

    public static List<int> Probe(IntPtr hwnd, IReadOnlyList<HotkeySlot> slots)
    {
        var failed = new List<int>();
        var held = new List<int>();
        try
        {
            foreach (var slot in slots)
            {
                if (slot.VirtualKey == 0)
                {
                    continue;
                }

                var ok = NativeMethods.RegisterHotKey(
                    hwnd,
                    slot.Id,
                    slot.Modifiers | NativeMethods.ModNoRepeat,
                    slot.VirtualKey);
                if (ok)
                {
                    held.Add(slot.Id);
                }
                else
                {
                    failed.Add(slot.Id);
                }
            }
        }
        finally
        {
            foreach (var id in held)
            {
                _ = NativeMethods.UnregisterHotKey(hwnd, id);
            }
        }

        return failed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _ids)
        {
            _ = NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        _source.RemoveHook(WndProc);
    }

    private bool TryRegister(int id, uint modifiers, uint virtualKey)
    {
        var ok = NativeMethods.RegisterHotKey(
            _source.Handle,
            id,
            modifiers | NativeMethods.ModNoRepeat,
            virtualKey);
        if (ok)
        {
            _ids.Add(id);
        }

        return ok;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }
}
