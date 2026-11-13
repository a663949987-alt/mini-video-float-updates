using System.Drawing;
using System.IO;

namespace MiniVideoFloat;

internal static class VideoRegionDetector
{
    private const int Columns = 80;
    private const int Rows = 56;
    private const int FrameCount = 7;

    internal static WindowRelay.CropRect? Detect(IntPtr hwnd, int width, int height, int aspectWidth, int aspectHeight)
    {
        if (width < 32 || height < 32 || aspectWidth <= 0 || aspectHeight <= 0 || WindowRelay.IsMinimized(hwnd)) return null;
        var scores = new float[Columns * Rows];
        float[]? previous = null;
        var usableFrames = 0;

        for (var frame = 0; frame < FrameCount; frame++)
        {
            using var bitmap = CropSelectionForm.CaptureClient(hwnd, width, height);
            if (bitmap is null) return null;
            if (frame == 0)
            {
                var layout = DetectRedFruitLayout(bitmap);
                if (layout is not null) return layout;
                try
                {
                    var debugDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniVideoFloat", "debug");
                    Directory.CreateDirectory(debugDirectory);
                    bitmap.Save(Path.Combine(debugDirectory, "connection-frame.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                catch { }
            }
            var current = Sample(bitmap);
            if (previous is not null)
            {
                for (var i = 0; i < scores.Length; i++) scores[i] += Math.Abs(current[i] - previous[i]);
                usableFrames++;
            }
            previous = current;
            if (frame + 1 < FrameCount) Thread.Sleep(350);
        }

        if (usableFrames == 0) return null;
        for (var i = 0; i < scores.Length; i++) scores[i] /= usableFrames;
        var moving = scores.Count(score => score >= 8f);
        if (moving < 8) return null;

        var mask = new bool[scores.Length];
        for (var i = 0; i < scores.Length; i++) mask[i] = scores[i] >= 8f;
        var expanded = Dilate(mask);
        var seen = new bool[expanded.Length];
        (int Left, int Top, int Right, int Bottom, int Count, float Motion)? best = null;

        for (var start = 0; start < expanded.Length; start++)
        {
            if (!expanded[start] || seen[start]) continue;
            var queue = new Queue<int>(); queue.Enqueue(start); seen[start] = true;
            var left = Columns; var right = -1; var top = Rows; var bottom = -1; var count = 0; float motion = 0;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue(); var x = cell % Columns; var y = cell / Columns;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                count++; motion += scores[cell];
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }

            var area = (right - left + 1) * (bottom - top + 1);
            if (area < 12 || count < 8) continue;
            var centerDistance = Math.Abs((left + right) * .5 - (Columns - 1) * .5) / Columns + Math.Abs((top + bottom) * .5 - (Rows - 1) * .5) / Rows;
            var score = motion * Math.Sqrt(area) / (1 + centerDistance * 2.5f);
            if (best is null || score > best.Value.Motion * Math.Sqrt((best.Value.Right - best.Value.Left + 1) * (best.Value.Bottom - best.Value.Top + 1)) /
                    (1 + (Math.Abs((best.Value.Left + best.Value.Right) * .5 - (Columns - 1) * .5) / Columns + Math.Abs((best.Value.Top + best.Value.Bottom) * .5 - (Rows - 1) * .5) / Rows) * 2.5f))
                best = (left, top, right, bottom, count, motion);

            void Visit(int x, int y)
            {
                if (x < 0 || x >= Columns || y < 0 || y >= Rows) return;
                var at = y * Columns + x;
                if (expanded[at] && !seen[at]) { seen[at] = true; queue.Enqueue(at); }
            }
        }

        if (best is null) return null;
        var selected = best.Value;
        var scaleX = width / (double)Columns; var scaleY = height / (double)Rows;
        var boxLeft = selected.Left * scaleX; var boxTop = selected.Top * scaleY;
        var boxRight = Math.Min(width, (selected.Right + 1) * scaleX); var boxBottom = Math.Min(height, (selected.Bottom + 1) * scaleY);
        var centerX = (boxLeft + boxRight) / 2; var centerY = (boxTop + boxBottom) / 2;
        var ratio = aspectWidth / (double)aspectHeight;
        var cropWidth = Math.Max((boxRight - boxLeft) * 1.55, (boxBottom - boxTop) * 1.55 * ratio);
        var cropHeight = cropWidth / ratio;
        var fit = Math.Min(1d, Math.Min(width / cropWidth, height / cropHeight));
        cropWidth *= fit; cropHeight *= fit;
        var l = (int)Math.Round(Math.Clamp(centerX - cropWidth / 2, 0, width - cropWidth));
        var t = (int)Math.Round(Math.Clamp(centerY - cropHeight / 2, 0, height - cropHeight));
        var r = Math.Min(width, (int)Math.Round(l + cropWidth));
        var b = Math.Min(height, (int)Math.Round(t + cropHeight));
        return r > l && b > t ? new WindowRelay.CropRect(l, t, r, b) : null;
    }

    // 红果桌面版的导航栏和公告栏是深色的，视频从它们的右下边缘开始。
    private static WindowRelay.CropRect? DetectRedFruitLayout(Bitmap bitmap)
    {
        var width = bitmap.Width; var height = bitmap.Height;
        double bestColumn = 0; var left = 0;
        for (var x = width / 12; x < width * 2 / 5; x += 2)
        {
            double sum = 0; var count = 0;
            for (var y = height / 5; y < height * 4 / 5; y += Math.Max(1, height / 45))
            {
                var a = bitmap.GetPixel(Math.Max(0, x - 3), y);
                var b = bitmap.GetPixel(Math.Min(width - 1, x + 3), y);
                if ((a.R + a.G + a.B) / 3d > 55) continue;
                sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                count++;
            }
            if (count < 20) continue;
            var score = sum / count;
            if (score > bestColumn) { bestColumn = score; left = x; }
        }
        if (bestColumn < 40) return null;
        double bestRow = 0; var top = 0;
        for (var y = height / 25; y < height / 4; y += 2)
        {
            double sum = 0; var count = 0;
            for (var x = left + (width - left) / 12; x < width - 5; x += Math.Max(1, (width - left) / 40))
            {
                var a = bitmap.GetPixel(x, Math.Max(0, y - 3));
                var b = bitmap.GetPixel(x, Math.Min(height - 1, y + 3));
                if ((a.R + a.G + a.B) / 3d > 60) continue;
                sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                count++;
            }
            if (count < 15) continue;
            var score = sum / count;
            if (score > bestRow) { bestRow = score; top = y; }
        }
        return bestRow >= 30 && left > 0 && top > 0
            ? new WindowRelay.CropRect(left, top, width, height)
            : null;
    }

    private static float[] Sample(Bitmap bitmap)
    {
        var result = new float[Columns * Rows];
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Columns; x++)
        {
            var px = Math.Clamp((int)((x + .5) * bitmap.Width / Columns), 0, bitmap.Width - 1);
            var py = Math.Clamp((int)((y + .5) * bitmap.Height / Rows), 0, bitmap.Height - 1);
            var color = bitmap.GetPixel(px, py);
            result[y * Columns + x] = (color.R * .2126f + color.G * .7152f + color.B * .0722f);
        }
        return result;
    }

    private static bool[] Dilate(bool[] source)
    {
        var result = new bool[source.Length];
        for (var y = 0; y < Rows; y++)
        for (var x = 0; x < Columns; x++)
        {
            var active = false;
            for (var dy = -1; dy <= 1 && !active; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var sx = x + dx; var sy = y + dy;
                if (sx >= 0 && sx < Columns && sy >= 0 && sy < Rows && source[sy * Columns + sx]) { active = true; break; }
            }
            result[y * Columns + x] = active;
        }
        return result;
    }
}
