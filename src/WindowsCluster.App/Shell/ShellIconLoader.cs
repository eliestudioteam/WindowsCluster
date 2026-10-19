using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsCluster.App.Interop;
using static WindowsCluster.App.Interop.NativeMethods;
using static WindowsCluster.App.Interop.ShellInterop;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Loads shell images (icons, or thumbnails for images/documents) through
/// IShellItemImageFactory, the API Explorer uses for its own views.
///
/// Images are requested at the exact pixel size they are displayed (icon size × DPI).
/// Windows icons contain different artwork per size (a text file's 48 px icon shows
/// lines, its 256 px one is a blank page), so asking for the displayed size yields the
/// same pixels Explorer draws, perfectly sharp, rather than a scaled-down jumbo image.
/// Work runs on one dedicated STA thread because shell extensions require it.
/// </summary>
public sealed class ShellIconLoader : IDisposable
{
    private readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<ImageSource?> Result)> _queue = new();
    private readonly ConcurrentDictionary<(string Path, int Size), Task<ImageSource?>> _cache = new();
    private readonly Thread _worker;

    public ShellIconLoader()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "WindowsCluster.ShellIcons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public static ShellIconLoader Shared { get; } = new();

    /// <summary>Returns a frozen image of <paramref name="pixelSize"/>² for a file path or parsing name.</summary>
    public Task<ImageSource?> GetAsync(string parsingName, int pixelSize) =>
        _cache.GetOrAdd((parsingName.ToUpperInvariant(), pixelSize), key =>
        {
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((parsingName, key.Size, tcs));
            return tcs.Task;
        });

    /// <summary>Drops every cached size of an item, e.g. after its file or folder content changed.</summary>
    public void Invalidate(string parsingName)
    {
        var path = parsingName.ToUpperInvariant();
        foreach (var key in _cache.Keys.Where(k => k.Path == path))
            _cache.TryRemove(key, out _);
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        foreach (var (path, size, result) in _queue.GetConsumingEnumerable())
        {
            try
            {
                result.SetResult(Load(path, size));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellIconLoader] {path}: {ex.Message}");
                result.SetResult(null);
            }
        }
    }

    private static ImageSource? Load(string path, int size)
    {
        var iid = IID_IShellItemImageFactory;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        try
        {
            var factory = (IShellItemImageFactory)item;
            var requested = new ShellInterop.SIZE(size, size);
            // ResizeToFit returns a thumbnail when one exists (images, videos, documents)
            // and the icon otherwise, matching Explorer's icon views.
            var hr = factory.GetImage(requested, SIIGBF.ResizeToFit, out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                hr = factory.GetImage(requested, SIIGBF.IconOnly, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                return null;

            try
            {
                return ToBitmapSource(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>
    /// Copies the bitmap as top-down 32-bit BGRA keeping its alpha channel. GetDIBits with a
    /// negative height lets GDI handle the source orientation, which shell bitmaps do not
    /// report consistently (reading raw DIB bits flipped some icons upside down).
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0)
            return null;
        var width = dib.dsBm.bmWidth;
        var height = Math.Abs(dib.dsBm.bmHeight);
        if (width <= 0 || height <= 0)
            return null;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // Some thumbnails carry no alpha at all (every alpha byte is 0): they are opaque.
        if (HasNoAlpha(pixels))
        {
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 0xFF;
        }

        // Shell images are normally premultiplied; fall back to straight alpha when any
        // color channel exceeds its alpha, which premultiplied data cannot contain.
        var format = IsPremultiplied(pixels) ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;

        var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool HasNoAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
                return false;
        }
        return true;
    }

    private static bool IsPremultiplied(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a)
                return false;
        }
        return true;
    }
}
