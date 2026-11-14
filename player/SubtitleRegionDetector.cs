using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MiniVideoFloat;

// Locate a bright, multi-stroke text line without reading or storing its content.
internal static class SubtitleRegionDetector
{
    internal readonly record struct Detection(double CenterY, double WidthFraction, double HeightFraction);
    internal static double? Detect(Bitmap image, WindowRelay.CropRect region) => DetectDetails(image, region)?.CenterY;
    internal static Detection? DetectDetails(Bitmap image, WindowRelay.CropRect region)
    {
        int rw = region.Right - region.Left, rh = region.Bottom - region.Top;
        if (rw < 50 || rh < 50) return null;
        int w = 400, h = Math.Clamp((int)Math.Round(rh * (double)w / rw), 80, 1100);
        using var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(image, new Rectangle(0, 0, w, h), new Rectangle(region.Left, region.Top, rw, rh), GraphicsUnit.Pixel);
        }
        var data = scaled.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * h]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var scores = new double[h]; var starts = new int[h]; var ends = new int[h]; int left = w / 8, right = w * 7 / 8;
            for (int y = Math.Max(2, h / 2); y < h * 97 / 100 - 2; y++)
            {
                int bright = 0, segments = 0, first = w, last = 0; bool was = false;
                for (int x = left; x < right; x++)
                {
                    int p = y * data.Stride + x * 4; int b = bytes[p], g = bytes[p + 1], r = bytes[p + 2];
                    bool white = Math.Min(r, Math.Min(g, b)) >= 190 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 55;
                    if (white) { bright++; first = Math.Min(first, x); last = x; if (!was) segments++; }
                    was = white;
                }
                int span = last - first;
                if (segments >= 4 && bright >= 8 && span >= w / 12 && span <= w * 3 / 4 && bright < span * .72)
                {
                    scores[y] = segments * 2 + bright * .08;
                    starts[y] = first; ends[y] = last;
                }
            }
            double best = 0; int bestY = -1;
            for (int y = h / 2 + 3; y < h * 97 / 100 - 3; y++)
            {
                double score = scores[y - 2] + scores[y - 1] + scores[y] + scores[y + 1] + scores[y + 2];
                if (score > best) { best = score; bestY = y; }
            }
            if (best < 65 || bestY < 0) return null;
            int maxHalf = Math.Max(4, h / 25), firstY = bestY, lastY = bestY, gaps = 0;
            for (int y = bestY - 1; y >= Math.Max(h / 2, bestY - maxHalf); y--) { if (scores[y] > 0) { firstY = y; gaps = 0; } else if (++gaps >= 3) break; }
            gaps = 0;
            for (int y = bestY + 1; y < Math.Min(h, bestY + maxHalf); y++) { if (scores[y] > 0) { lastY = y; gaps = 0; } else if (++gaps >= 3) break; }
            int firstX = w, lastX = 0;
            for (int y = firstY; y <= lastY; y++) if (scores[y] > 0) { firstX = Math.Min(firstX, starts[y]); lastX = Math.Max(lastX, ends[y]); }
            if (lastX <= firstX || lastY - firstY < 2) return null;
            return new Detection((firstY + lastY + 1) / (2d * h), (lastX - firstX + 1) / (double)w, (lastY - firstY + 1) / (double)h);
        }
        finally { scaled.UnlockBits(data); }
    }
}
