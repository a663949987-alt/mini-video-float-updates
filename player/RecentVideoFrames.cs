using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MiniVideoFloat;

internal sealed class RecentVideoFrames
{
    internal sealed record CapturedFrame(BitmapSource Image, BitmapSource Subtitle25, BitmapSource Subtitle38, BitmapSource Subtitle50, double? DetectedSubtitle = null, int SubtitleTop = -1, SubtitleRegionDetector.Detection? DetectedSize = null, int SubtitleLeft = 0, int SubtitleWidth = 0, int SubtitleHeight = 0)
    {
        internal BitmapSource? Subtitle(int level) => level switch { 1 => Subtitle25, 3 => Subtitle38, 2 => Subtitle50, _ => null };
    }
    internal sealed record Frame(long Milliseconds, CapturedFrame Picture);
    private readonly Queue<Frame> _frames = new();
    internal void Add(long timestamp, CapturedFrame image)
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

    internal static CapturedFrame? Capture(IntPtr window, int width, int height, WindowRelay.CropRect crop, WindowRelay.CropRect? subtitleSource = null, int outputWidth = 155, int outputHeight = 98, int subtitleHeight = 12, double? centerY = null, bool detectSubtitle = false, SubtitleRegionDetector.Detection? subtitleSize = null, bool contain = false, bool anchorSubtitleBottom = false)
    {
        using var full = CropSelectionForm.CaptureClient(window, width, height);
        if (full is null || crop.Left < 0 || crop.Top < 0 || crop.Right > full.Width || crop.Bottom > full.Height || crop.Right <= crop.Left || crop.Bottom <= crop.Top) return null;
        var source = subtitleSource ?? crop;
        if (source.Left < 0 || source.Top < 0 || source.Right > full.Width || source.Bottom > full.Height || source.Right <= source.Left || source.Bottom <= source.Top) return null;
        var detected = detectSubtitle ? SubtitleRegionDetector.DetectDetails(full, source) : null;
        var placement = contain ? WindowRelay.FitInside(crop, outputWidth, outputHeight) : new WindowRelay.WindowRect(0, 0, outputWidth, outputHeight);
        var bandHeight = anchorSubtitleBottom
            ? WindowRelay.BottomSubtitleBandHeight(crop, source, centerY, subtitleSize?.HeightFraction, outputHeight, placement, subtitleHeight)
            : Math.Min(subtitleHeight, placement.Height);
        int badgeCut = placement.Top + placement.Height > outputHeight - 14 ? Math.Clamp(placement.Left + placement.Width - (outputWidth - 25), 0, placement.Width - 1) : 0;
        double Zoom(double requested) => WindowRelay.AdaptiveSubtitleZoom(source, placement.Width, bandHeight, requested, subtitleSize?.WidthFraction, subtitleSize?.HeightFraction, badgeCut);
        return new CapturedFrame(
            Render(full, crop, outputWidth, outputHeight, contain),
            Render(full, WindowRelay.SubtitleBand(source, placement.Width, bandHeight, Zoom(1.25), centerY), placement.Width, bandHeight),
            Render(full, WindowRelay.SubtitleBand(source, placement.Width, bandHeight, Zoom(1.38), centerY), placement.Width, bandHeight),
            Render(full, WindowRelay.SubtitleBand(source, placement.Width, bandHeight, Zoom(1.5), centerY), placement.Width, bandHeight), detected?.CenterY,
            WindowRelay.SubtitleTopInFrame(crop, source, centerY, outputHeight, placement, bandHeight, anchorSubtitleBottom), detected, placement.Left, placement.Width, bandHeight);
    }

    private static BitmapSource Render(Bitmap full, WindowRelay.CropRect crop, int outputWidth, int height, bool contain = false)
    {
        using var small = new Bitmap(outputWidth, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.Clear(System.Drawing.Color.Black);
            var fit = contain ? WindowRelay.FitInside(crop, outputWidth, height) : new WindowRelay.WindowRect(0, 0, outputWidth, height);
            g.DrawImage(full, new Rectangle(fit.Left, fit.Top, fit.Width, fit.Height), new Rectangle(crop.Left, crop.Top, crop.Right-crop.Left, crop.Bottom-crop.Top), GraphicsUnit.Pixel);
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
