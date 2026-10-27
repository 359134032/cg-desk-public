using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CgDesk.Services;

internal static class WindowThumbs
{
    private static readonly HashSet<IntPtr> Registered = [];
    private static readonly Dictionary<IntPtr, NativeMethods.RectNative> Destinations = [];
    internal static int ActiveCount => Registered.Count;
    internal static IReadOnlyList<NativeMethods.RectNative> ActiveDestinations => Destinations.Values.ToArray();
    private const int Destination = 0x00000001;
    private const int Source = 0x00000002;
    private const int Visible = 0x00000008;
    private const int Opacity = 0x00000004;
    private const int SourceClientAreaOnly = 0x00000010;

    public static bool TryRegister(IntPtr destination, IntPtr source, out IntPtr thumbnail)
    {
        thumbnail = IntPtr.Zero;
        var registered = destination != IntPtr.Zero
            && source != IntPtr.Zero
            && source != destination
            && DwmRegisterThumbnail(destination, source, out thumbnail) == 0
            && thumbnail != IntPtr.Zero;
        if (registered) Registered.Add(thumbnail);
        return registered;
    }

    public static void Update(IntPtr thumbnail, NativeMethods.RectNative destination)
    {
        Update(thumbnail, destination, 0, 0);
    }

    public static void Update(IntPtr thumbnail, NativeMethods.RectNative destination, int sourceWidth, int sourceHeight, NativeMethods.RectNative? clip = null)
    {
        if (thumbnail == IntPtr.Zero || destination.Right <= destination.Left || destination.Bottom <= destination.Top)
        {
            return;
        }

        if ((sourceWidth <= 0 || sourceHeight <= 0) && DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0)
        {
            sourceWidth = size.Width;
            sourceHeight = size.Height;
        }
        if (!TryPlacement(destination, sourceWidth, sourceHeight, clip ?? destination, out var fitted, out var source))
        {
            var hidden = new ThumbnailProperties { Flags = Visible, Visible = 0 };
            if (DwmUpdateThumbnailProperties(thumbnail, ref hidden) == 0) Destinations.Remove(thumbnail);
            return;
        }

        var properties = new ThumbnailProperties
        {
            Flags = Destination | Source | Visible | Opacity | SourceClientAreaOnly,
            Destination = fitted,
            Source = source,
            Opacity = 255,
            Visible = 1,
            SourceClientAreaOnly = 1
        };
        if (DwmUpdateThumbnailProperties(thumbnail, ref properties) == 0) Destinations[thumbnail] = fitted;
    }

    internal static bool Intersects(NativeMethods.RectNative a, NativeMethods.RectNative b) =>
        Math.Max(a.Left, b.Left) < Math.Min(a.Right, b.Right) && Math.Max(a.Top, b.Top) < Math.Min(a.Bottom, b.Bottom);

    internal static bool TryPlacement(NativeMethods.RectNative destination, int width, int height, NativeMethods.RectNative clip,
        out NativeMethods.RectNative visible, out NativeMethods.RectNative source)
    {
        visible = default;
        source = default;
        if (width <= 0 || height <= 0 || destination.Right <= destination.Left || destination.Bottom <= destination.Top) return false;
        var fitted = Fit(destination, width, height);
        if (!Intersects(fitted, clip)) return false;
        visible = new NativeMethods.RectNative
        {
            Left = Math.Max(fitted.Left, clip.Left), Top = Math.Max(fitted.Top, clip.Top),
            Right = Math.Min(fitted.Right, clip.Right), Bottom = Math.Min(fitted.Bottom, clip.Bottom)
        };
        var scaleX = width / (double)(fitted.Right - fitted.Left);
        var scaleY = height / (double)(fitted.Bottom - fitted.Top);
        source = new NativeMethods.RectNative
        {
            Left = Math.Clamp((int)Math.Floor((visible.Left - fitted.Left) * scaleX), 0, width),
            Top = Math.Clamp((int)Math.Floor((visible.Top - fitted.Top) * scaleY), 0, height),
            Right = Math.Clamp((int)Math.Ceiling((visible.Right - fitted.Left) * scaleX), 0, width),
            Bottom = Math.Clamp((int)Math.Ceiling((visible.Bottom - fitted.Top) * scaleY), 0, height)
        };
        return source.Right > source.Left && source.Bottom > source.Top;
    }

    public static BitmapSource? CaptureClient(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.Responds(hwnd) || !NativeMethods.GetWindowRect(hwnd, out var window))
        {
            return null;
        }

        if (!NativeMethods.GetClientRect(hwnd, out var client))
        {
            return null;
        }

        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        var windowWidth = window.Right - window.Left;
        var windowHeight = window.Bottom - window.Top;
        if (clientWidth < 8 || clientHeight < 8 || windowWidth < 8 || windowHeight < 8)
        {
            return null;
        }

        var origin = new PointNative();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return null;
        }

        var cropX = Math.Clamp(origin.X - window.Left, 0, windowWidth - 1);
        var cropY = Math.Clamp(origin.Y - window.Top, 0, windowHeight - 1);
        var cropWidth = Math.Min(clientWidth, windowWidth - cropX);
        var cropHeight = Math.Min(clientHeight, windowHeight - cropY);
        if (cropWidth < 8 || cropHeight < 8)
        {
            return null;
        }

        using var full = new Bitmap(windowWidth, windowHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        _ = DrawWindow(hwnd, full);
        var crop = new Rectangle(cropX, cropY, cropWidth, cropHeight);
        using var first = full.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Bitmap cropped = first;
        if (!HasPicture(first))
        {
            if (!DrawClient(hwnd, full, cropX, cropY, cropWidth, cropHeight))
            {
                return null;
            }

            cropped = full.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            if (!HasPicture(cropped))
            {
                cropped.Dispose();
                return null;
            }
        }

        try
        {
            var handle = cropped.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                _ = DeleteObject(handle);
            }
        }
        finally
        {
            if (!ReferenceEquals(cropped, first))
            {
                cropped.Dispose();
            }
        }
    }

    public static void Close(IntPtr thumbnail)
    {
        if (thumbnail != IntPtr.Zero)
        {
            _ = DwmUnregisterThumbnail(thumbnail);
            Registered.Remove(thumbnail);
            Destinations.Remove(thumbnail);
        }
    }

    public static NativeMethods.RectNative ClientRect(IntPtr hwnd, FrameworkElement element)
    {
        var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        var bottomRight = element.PointToScreen(new System.Windows.Point(Math.Max(0, element.ActualWidth), Math.Max(0, element.ActualHeight)));
        var origin = new PointNative { X = (int)Math.Floor(topLeft.X), Y = (int)Math.Floor(topLeft.Y) };
        var far = new PointNative { X = (int)Math.Ceiling(bottomRight.X), Y = (int)Math.Ceiling(bottomRight.Y) };
        _ = ScreenToClient(hwnd, ref origin);
        _ = ScreenToClient(hwnd, ref far);
        return new NativeMethods.RectNative
        {
            Left = origin.X,
            Top = origin.Y,
            Right = far.X,
            Bottom = far.Y
        };
    }

    private static bool DrawWindow(IntPtr hwnd, Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try
        {
            return PrintWindow(hwnd, dc, 2) || PrintWindow(hwnd, dc, 0);
        }
        finally
        {
            graphics.ReleaseHdc(dc);
        }
    }

    private static bool DrawClient(IntPtr hwnd, Bitmap bitmap, int x, int y, int width, int height)
    {
        using var graphics = Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        var windowDc = GetDC(hwnd);
        try
        {
            if (windowDc == IntPtr.Zero)
            {
                return false;
            }

            return BitBlt(dc, x, y, width, height, windowDc, 0, 0, 0x00CC0020);
        }
        finally
        {
            if (windowDc != IntPtr.Zero)
            {
                _ = ReleaseDC(hwnd, windowDc);
            }

            graphics.ReleaseHdc(dc);
        }
    }

    private static bool HasPicture(Bitmap bitmap)
    {
        var total = 0;
        var lit = 0;
        var stepX = Math.Max(1, bitmap.Width / 24);
        var stepY = Math.Max(1, bitmap.Height / 24);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var color = bitmap.GetPixel(x, y);
                total++;
                if (color.R > 12 || color.G > 12 || color.B > 12)
                {
                    lit++;
                }
            }
        }

        return total > 0 && lit * 8 >= total;
    }

    private static NativeMethods.RectNative Fit(NativeMethods.RectNative bounds, int sourceWidth, int sourceHeight)
    {
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var scale = Math.Min(width / (double)sourceWidth, height / (double)sourceHeight);
        var fittedWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var fittedHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var left = bounds.Left + (width - fittedWidth) / 2;
        var top = bounds.Top + (height - fittedHeight) / 2;
        return new NativeMethods.RectNative
        {
            Left = left,
            Top = top,
            Right = left + fittedWidth,
            Bottom = top + fittedHeight
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public int Flags;
        public NativeMethods.RectNative Destination;
        public NativeMethods.RectNative Source;
        public byte Opacity;
        public byte Pad1;
        public byte Pad2;
        public byte Pad3;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeNative
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SizeNative size);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int width, int height, IntPtr source, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
