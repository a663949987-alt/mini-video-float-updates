using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace MiniVideoFloat;

internal static class PixelFrameRenderer
{
    // Capture is performed off the UI thread. The bitmap is frozen before returning to WPF.
    internal static BitmapSource? Capture(IntPtr source, int width, int height, WindowRelay.CropRect? crop, int pixelSize)
    {
        using var frame = CropSelectionForm.CaptureClient(source, width, height);
        if (frame is null) return null;
        return Render(frame, crop, pixelSize);
    }

    internal static BitmapSource Render(Bitmap frame, WindowRelay.CropRect? crop, int pixelSize)
    {
        var width = frame.Width; var height = frame.Height;
        var fitted = crop is { } selected ? WindowRelay.FitCrop(selected, 155, 98) : new WindowRelay.CropRect(0, 0, width, height);
        var left = Math.Clamp(fitted.Left, 0, width - 1);
        var top = Math.Clamp(fitted.Top, 0, height - 1);
        var right = Math.Clamp(fitted.Right, left + 1, width);
        var bottom = Math.Clamp(fitted.Bottom, top + 1, height);
        var pixelWidth = Math.Max(1, 155 / Math.Max(1, pixelSize));
        var pixelHeight = Math.Max(1, 98 / Math.Max(1, pixelSize));
        using var small = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(frame, new Rectangle(0, 0, pixelWidth, pixelHeight), new Rectangle(left, top, right - left, bottom - top), GraphicsUnit.Pixel);
        }
        using var bytes = new MemoryStream();
        small.Save(bytes, ImageFormat.Png);
        bytes.Position = 0;
        var result = new BitmapImage();
        result.BeginInit();
        result.CacheOption = BitmapCacheOption.OnLoad;
        result.StreamSource = bytes;
        result.EndInit();
        result.Freeze();
        return result;
    }
}
