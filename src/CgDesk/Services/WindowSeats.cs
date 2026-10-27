namespace CgDesk.Services;

// Slots belong to a running window, not its current position in the list.
internal sealed class WindowSeats
{
    private readonly Dictionary<IntPtr, (int ProcessId, int Number)> _slots = [];

    public void Update(IReadOnlyList<GameWindow> windows)
    {
        var live = windows.ToDictionary(window => window.Handle, window => window.ProcessId);
        foreach (var handle in _slots.Keys.ToArray())
        {
            if (!live.TryGetValue(handle, out var processId) || _slots[handle].ProcessId != processId)
                _slots.Remove(handle);
        }
        foreach (var window in windows)
        {
            if (_slots.ContainsKey(window.Handle)) continue;
            var used = _slots.Values.Select(slot => slot.Number).ToHashSet();
            var number = 1;
            while (used.Contains(number)) number++;
            _slots[window.Handle] = (window.ProcessId, number);
        }
    }

    public int Number(IntPtr handle) => _slots.TryGetValue(handle, out var slot) ? slot.Number : 0;
}
