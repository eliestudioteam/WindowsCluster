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
/// Loads high-resolution shell images (icons, or thumbnails for images/documents) through
/// IShellItemImageFactory, the same API Explorer uses for its large views. Images are
/// requested at 256×256 (jumbo) and scaled down by WPF, so they stay sharp at any DPI.
/// Work runs on one dedicated STA thread because shell extensions require it.
/// </summary>
public sealed class ShellIconLoader : IDisposable
{
    public const int JumboSize = 256;

    private readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource?> Result)> _queue = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _worker;

    public ShellIconLoader()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "WindowsCluster.ShellIcons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public static ShellIconLoader Shared { get; } = new();

    /// <summary>Returns a frozen 256×256 image for a file path or shell parsing name.</summary>
    public Task<ImageSource?> GetAsync(string parsingName) =>
        _cache.GetOrAdd(parsingName, path =>
        {
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((path, tcs));
            return tcs.Task;
        });

    /// <summary>Drops the cached image, e.g. after the file changed.</summary>
    public void Invalidate(string parsingName) => _cache.TryRemove(parsingName, out _);

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        foreach (var (path, result) in _queue.GetConsumingEnumerable())
        {
            try
            {
                result.SetResult(Load(path));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ShellIconLoader] {path}: {ex.Message}");
                result.SetResult(null);
            }
        }
    }

    private static ImageSource? Load(string path)
    {
        var iid = IID_IShellItemImageFactory;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
        try
        {
            var factory = (IShellItemImageFactory)item;
            var size = new ShellInterop.SIZE(JumboSize, JumboSize);
            // ResizeToFit returns a thumbnail when one exists (images, videos, documents)
            // and the jumbo icon otherwise, matching Explorer's large-icon view.
            var hr = factory.GetImage(size, SIIGBF.ResizeToFit, out var hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
                hr = factory.GetImage(size, SIIGBF.IconOnly, out hbitmap);
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

    /// <summary>
    /// Copies a 32-bit DIB section into a WPF bitmap keeping its alpha channel.
    /// (Imaging.CreateBitmapSourceFromHBitmap drops alpha, which produces black corners.)
    /// </summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0 ||
            dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
            return null;

        var width = dib.dsBm.bmWidth;
        var height = dib.dsBm.bmHeight;
        var stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        // A positive biHeight means the DIB is stored bottom-up.
        if (dib.dsBmih.biHeight > 0)
            FlipRows(pixels, stride, height);

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

    private static void FlipRows(byte[] pixels, int stride, int height)
    {
        var row = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(pixels, top * stride, row, 0, stride);
            Buffer.BlockCopy(pixels, bottom * stride, pixels, top * stride, stride);
            Buffer.BlockCopy(row, 0, pixels, bottom * stride, stride);
        }
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
