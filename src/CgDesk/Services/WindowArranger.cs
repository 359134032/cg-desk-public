namespace CgDesk.Services;

internal readonly record struct WindowSlot(int X, int Y, int Width, int Height, bool Primary);

internal static class WindowArranger
{
    public static IReadOnlyList<WindowSlot> Plan(int count, string layout, NativeMethods.RectNative work)
    {
        if (count <= 0)
        {
            return [];
        }

        var left = work.Left + 4;
        var top = work.Top + 4;
        var width = Math.Max(240, work.Right - work.Left - 8);
        var height = Math.Max(180, work.Bottom - work.Top - 8);
        const int gap = 6;

        if (count == 1 || !string.Equals(layout, "grid", StringComparison.OrdinalIgnoreCase))
        {
            return PlanPrimarySide(count, left, top, width, height, gap);
        }

        return PlanGrid(count, left, top, width, height, gap);
    }

    public static void Apply(IntPtr primary, IReadOnlyList<IntPtr> others, string layout, NativeMethods.RectNative work)
    {
        var windows = new List<IntPtr>();
        if (primary != IntPtr.Zero && NativeMethods.IsWindow(primary))
        {
            windows.Add(primary);
        }

        foreach (var handle in others)
        {
            if (handle == IntPtr.Zero || handle == primary || !NativeMethods.IsWindow(handle))
            {
                continue;
            }

            if (!windows.Contains(handle))
            {
                windows.Add(handle);
            }
        }

        var slots = Plan(windows.Count, layout, work);
        for (var i = 0; i < windows.Count; i++)
        {
            var handle = windows[i];
            var slot = slots[i];
            if (NativeMethods.IsIconic(handle))
            {
                _ = NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            }

            var flags = NativeMethods.SwpShowWindow | NativeMethods.SwpNoZOrder;
            if (handle != primary)
            {
                flags |= NativeMethods.SwpNoActivate;
            }

            _ = NativeMethods.SetWindowPos(handle, IntPtr.Zero, slot.X, slot.Y, slot.Width, slot.Height, flags);
        }

        if (primary != IntPtr.Zero)
        {
            NativeMethods.Focus(primary);
        }
    }

    public const int GameClientWidth = 800;
    public const int GameClientHeight = 600;
    public const int SmallClientWidth = 320;
    public const int SmallClientHeight = 240;

    public static (int Width, int Height) FitClient(double maxWidth, double maxHeight)
    {
        if (maxWidth < GameClientWidth || maxHeight < GameClientHeight)
        {
            return (GameClientWidth, GameClientHeight);
        }

        var width = maxWidth;
        var height = width * GameClientHeight / GameClientWidth;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * GameClientWidth / GameClientHeight;
        }

        var fittedWidth = (int)Math.Floor(width);
        fittedWidth -= fittedWidth % 8;
        if (fittedWidth < GameClientWidth)
        {
            return (GameClientWidth, GameClientHeight);
        }

        var fittedHeight = fittedWidth * GameClientHeight / GameClientWidth;
        if (fittedHeight > maxHeight)
        {
            fittedWidth -= 8;
            fittedHeight = fittedWidth * GameClientHeight / GameClientWidth;
        }

        if (fittedWidth < GameClientWidth || fittedHeight < GameClientHeight)
        {
            return (GameClientWidth, GameClientHeight);
        }

        return (fittedWidth, fittedHeight);
    }

    public static void PlacePrimary(IntPtr primary, NativeMethods.RectNative work)
    {
        if (!TryOuterSize(primary, out var width, out var height, out _))
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            primary,
            IntPtr.Zero,
            work.Left + 8,
            work.Top + 8,
            width,
            height,
            NativeMethods.SwpShowWindow);
        NativeMethods.Focus(primary);
    }

    public static void EnsureMinimumSize(IntPtr window)
    {
        if (!TryOuterSize(window, out var width, out var height, out var current)
            || (width == current.Width && height == current.Height))
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            window,
            IntPtr.Zero,
            0,
            0,
            width,
            height,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private static bool TryOuterSize(IntPtr window, out int width, out int height, out (int Width, int Height) current)
    {
        width = 0;
        height = 0;
        current = default;
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            _ = NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        }

        if (!NativeMethods.GetWindowRect(window, out var rect))
        {
            return false;
        }

        var outerWidth = Math.Max(1, rect.Right - rect.Left);
        var outerHeight = Math.Max(1, rect.Bottom - rect.Top);
        current = (outerWidth, outerHeight);
        var frameWidth = 0;
        var frameHeight = 0;
        if (NativeMethods.GetClientRect(window, out var client))
        {
            frameWidth = Math.Max(0, outerWidth - Math.Max(0, client.Right - client.Left));
            frameHeight = Math.Max(0, outerHeight - Math.Max(0, client.Bottom - client.Top));
        }

        width = Math.Max(outerWidth, GameClientWidth + frameWidth);
        height = Math.Max(outerHeight, GameClientHeight + frameHeight);
        return true;
    }

    private static List<WindowSlot> PlanPrimarySide(int count, int left, int top, int width, int height, int gap)
    {
        if (count == 1)
        {
            return [new WindowSlot(left, top, width, height, true)];
        }

        var sideWidth = Math.Clamp(width / 4, 220, 420);
        var mainWidth = Math.Max(240, width - sideWidth - gap);
        var slots = new List<WindowSlot>
        {
            new(left, top, mainWidth, height, true)
        };

        var sideCount = count - 1;
        var sideLeft = left + mainWidth + gap;
        var rowHeight = (height - gap * (sideCount - 1)) / sideCount;
        for (var i = 0; i < sideCount; i++)
        {
            var rowTop = top + i * (rowHeight + gap);
            var thisHeight = i == sideCount - 1 ? top + height - rowTop : rowHeight;
            slots.Add(new WindowSlot(sideLeft, rowTop, sideWidth, Math.Max(80, thisHeight), false));
        }

        return slots;
    }

    private static List<WindowSlot> PlanGrid(int count, int left, int top, int width, int height, int gap)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);
        var cellWidth = (width - gap * (columns - 1)) / columns;
        var cellHeight = (height - gap * (rows - 1)) / rows;
        var slots = new List<WindowSlot>(count);
        for (var i = 0; i < count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            slots.Add(new WindowSlot(
                left + column * (cellWidth + gap),
                top + row * (cellHeight + gap),
                cellWidth,
                cellHeight,
                i == 0));
        }

        return slots;
    }
}
