using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiniVideoFloat;

internal sealed class RecentVideoFrames
{
    internal sealed record Frame(long Milliseconds, BitmapSource Image);
    private readonly Queue<Frame> _frames = new();
    internal void Add(long timestamp, BitmapSource image)
    {
        _frames.Enqueue(new Frame(timestamp, image));
        while (_frames.Count > 85 || (_frames.Count > 0 && timestamp - _frames.Peek().Milliseconds > 5000)) _frames.Dequeue();
    }
    internal Frame[] Snapshot()
    {
        var frames = _frames.ToArray();
        return frames.Length >= 20 && frames[^1].Milliseconds - frames[0].Milliseconds >= 4500 ? frames : [];
    }
    internal void Clear() => _frames.Clear();

    internal static BitmapSource? Capture(IntPtr window, int width, int height, WindowRelay.CropRect crop)
    {
        using var full = CropSelectionForm.CaptureClient(window, width, height);
        if (full is null || crop.Left < 0 || crop.Top < 0 || crop.Right > full.Width || crop.Bottom > full.Height || crop.Right <= crop.Left || crop.Bottom <= crop.Top) return null;
        using var small = new Bitmap(155, 98, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, new Rectangle(0, 0, 155, 98), new Rectangle(crop.Left, crop.Top, crop.Right-crop.Left, crop.Bottom-crop.Top), GraphicsUnit.Pixel);
        }
        var rect = new Rectangle(0, 0, small.Width, small.Height);
        var bits = small.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[small.Width * small.Height * 4];
            for (var y = 0; y < small.Height; y++) Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, y * small.Width * 4, small.Width * 4);
            // PrintWindow may leave alpha unset; WPF must treat the bitmap as opaque.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(small.Width, small.Height, 96, 96, PixelFormats.Bgra32, null, pixels, small.Width * 4);
            result.Freeze();
            return result;
        }
        finally { small.UnlockBits(bits); }
    }
}
