using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Explorer decorates thumbnails according to the file type's "Treatment" registry value
/// (1 drop shadow, 2 photo border, 3 video sprockets). IShellItemImageFactory returns the bare
/// thumbnail, so the treatment is looked up the same way and drawn here.
/// </summary>
public static class ThumbnailTreatment
{
    public const int None = 0;
    public const int DropShadow = 1;
    public const int PhotoBorder = 2;
    public const int VideoSprockets = 3;

    private static readonly ConcurrentDictionary<string, int> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The treatment Explorer applies to thumbnails of <paramref name="path"/>'s type.</summary>
    public static int For(string path)
    {
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? None : Cache.GetOrAdd(extension, Lookup);
    }

    private static int Lookup(string extension)
    {
        using var classes = Registry.ClassesRoot;
        using var extKey = classes.OpenSubKey(extension);
        var progId = extKey?.GetValue(null) as string;
        var perceived = extKey?.GetValue("PerceivedType") as string;

        string?[] candidates =
        [
            $@"SystemFileAssociations\{extension}",
            progId,
            extension,
            perceived is null ? null : $@"SystemFileAssociations\{perceived}",
        ];
        foreach (var keyName in candidates)
        {
            if (keyName is null)
                continue;
            using var key = classes.OpenSubKey(keyName);
            if (key?.GetValue("Treatment") is int treatment)
                return treatment;
        }
        return None;
    }

    /// <summary>Draws <paramref name="thumbnail"/> centered in a square of <paramref name="size"/> pixels with its treatment.</summary>
    public static BitmapSource Apply(BitmapSource thumbnail, int treatment, int size)
    {
        double w = thumbnail.PixelWidth, h = thumbnail.PixelHeight;
        var rect = new Rect((size - w) / 2, (size - h) / 2, w, h);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            switch (treatment)
            {
                case DropShadow:
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null,
                        new Rect(rect.X + 1, rect.Y + 1, rect.Width, rect.Height));
                    dc.DrawImage(thumbnail, rect);
                    break;

                case PhotoBorder:
                    dc.DrawImage(thumbnail, rect);
                    dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1),
                        new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
                    break;

                case VideoSprockets:
                    dc.DrawImage(thumbnail, rect);
                    DrawSprockets(dc, rect);
                    break;

                default:
                    dc.DrawImage(thumbnail, rect);
                    break;
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Film strip bands with perforations on the left and right edges.</summary>
    private static void DrawSprockets(DrawingContext dc, Rect rect)
    {
        var band = Math.Max(3, Math.Round(rect.Width * 0.09));
        var black = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
        var hole = new SolidColorBrush(Color.FromArgb(0xE6, 0xF0, 0xF0, 0xF0));
        var holeWidth = Math.Max(1, Math.Round(band * 0.5));
        var holeHeight = Math.Max(1, Math.Round(band * 0.45));
        var step = Math.Max(holeHeight + 1, Math.Round(band * 1.1));

        foreach (var x in new[] { rect.Left, rect.Right - band })
        {
            dc.DrawRectangle(black, null, new Rect(x, rect.Top, band, rect.Height));
            var holeX = x + (band - holeWidth) / 2;
            for (var y = rect.Top + (step - holeHeight) / 2; y + holeHeight <= rect.Bottom; y += step)
                dc.DrawRectangle(hole, null, new Rect(holeX, y, holeWidth, holeHeight));
        }
    }
}
