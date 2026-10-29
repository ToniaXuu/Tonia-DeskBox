using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToniaDeskBox.Services;

public static class WallpaperColorService
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buffer, uint init);

    public static string? GetAverageColorHex()
    {
        var path = GetWallpaperPath();
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();

            double scale = Math.Min(16.0 / bmp.PixelWidth, 16.0 / bmp.PixelHeight);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return null;
            var small = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            small.Freeze();

            var source = small.Format.BitsPerPixel == 32
                ? (BitmapSource)small
                : new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            if (source.Format.BitsPerPixel != 32) return null;

            int w = source.PixelWidth, h = source.PixelHeight;
            var pixels = new byte[w * h * 4];
            source.CopyPixels(pixels, w * 4, 0);

            double r = 0, g = 0, b = 0;
            int count = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                r += pixels[i + 2];
                g += pixels[i + 1];
                b += pixels[i];
                count++;
            }
            if (count == 0) return null;

            return $"#{(byte)(r / count):X2}{(byte)(g / count):X2}{(byte)(b / count):X2}";
        }
        catch
        {
            return null;
        }
    }

    private static string? GetWallpaperPath()
    {
        var sb = new StringBuilder(520);
        return SystemParametersInfo(SPI_GETDESKWALLPAPER, 520, sb, 0) ? sb.ToString() : null;
    }
}
