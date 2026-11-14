using MiniVideoFloat;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }

var startupTestDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mini-startup-test-" + Guid.NewGuid().ToString("N"));
System.IO.Directory.CreateDirectory(startupTestDir);
try
{
    var path = System.IO.Path.Combine(startupTestDir, "window.json");
    const string original = "{\"main\":{\"x\":10,\"y\":20,\"w\":1480,\"h\":760},\"mini\":{\"w\":2}}";
    System.IO.File.WriteAllText(path, original);
    using (var prepared = RedFruitStartupSize.Prepare(path))
    {
        Check(prepared is { OriginalWidth: 1480, OriginalHeight: 760 }, "remember the original source window size");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        Check(doc.RootElement.GetProperty("main").GetProperty("w").GetInt32() == 480 && doc.RootElement.GetProperty("main").GetProperty("h").GetInt32() == 270, "small startup geometry");
    }
    Check(System.IO.File.ReadAllText(path) == original, "restore unchanged source settings");
    using (RedFruitStartupSize.Prepare(path)) System.IO.File.WriteAllText(path, "{\"changed\":true}");
    Check(System.IO.File.ReadAllText(path) == "{\"changed\":true}", "preserve source settings changed by the user or source app");
}
finally { System.IO.Directory.Delete(startupTestDir, true); }

foreach (var height in new[] { 98, 16 })
{
    var full = new WindowRelay.CropRect(20, 30, 1570, 30 + height * 10);
    var (top, bottom) = WindowRelay.SplitForBadge(full, height);
    Check(top.Left == full.Left && top.Right == full.Right && top.Bottom == bottom.Top && bottom.Bottom == full.Bottom && bottom.Right == 1320, "badge split preserves video alignment");
}
var card = new WindowRelay.CropRect(0, 0, 2410, 1100);
var cardParts = WindowRelay.SplitForBadge(card, 110, 241);
Check(cardParts.Top.Bottom == cardParts.BottomLeft.Top && cardParts.BottomLeft.Right == 2160, "card badge split follows wider video");
var wideFit = WindowRelay.FitInside(new(0, 0, 1920, 1080), 241, 123);
Check(wideFit.Width == 219 && wideFit.Height == 123 && wideFit.Left == 11, "wide video fits completely without cropping");
var tallFit = WindowRelay.FitInside(new(0, 0, 1080, 1920), 241, 123);
Check(tallFit.Width == 69 && tallFit.Height == 123 && tallFit.Left == 86, "portrait video fits completely with side margins");
var paddedFit = WindowRelay.FitInside(new(0, 0, 1920, 800), 241, 123);
Check(WindowRelay.SubtitleTopInFrame(new(0, 0, 1920, 800), new(0, 0, 1920, 800), .85, 123, paddedFit, 16, true) == 107, "third skin subtitle remains at bottom of frame");
Check(WindowRelay.SubtitleTopInFrame(new(0, 0, 1920, 800), new(0, 0, 1920, 800), .85, 123, paddedFit, 16, false) != 107, "existing skins keep their detected subtitle position");
var fullSource = new WindowRelay.CropRect(0, 0, 1920, 1080);
var measuredBand = WindowRelay.BottomSubtitleBandHeight(fullSource, fullSource, .85, .04, 123, wideFit, 16);
Check(measuredBand > 16 && measuredBand <= 24, "bottom caption grows enough to cover the detected original line within a limit");
Check(WindowRelay.BottomSubtitleBandHeight(fullSource, fullSource, null, null, 123, wideFit, 16) == 16, "missing detection uses compact caption band");
using (var fullFrame = new System.Drawing.Bitmap(100, 200))
{
    using var draw = System.Drawing.Graphics.FromImage(fullFrame);
    draw.Clear(System.Drawing.Color.Red);
    draw.FillRectangle(System.Drawing.Brushes.Lime, 0, 100, 100, 100);
    var render = typeof(RecentVideoFrames).GetMethod("Render", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var bitmap = (BitmapSource)render.Invoke(null, [fullFrame, new WindowRelay.CropRect(0, 0, 100, 200), 241, 123, true])!;
    var bytes = new byte[241 * 123 * 4]; bitmap.CopyPixels(bytes, 241 * 4, 0);
    int At(int x, int y, int component) => bytes[(y * 241 + x) * 4 + component];
    Check(At(0, 60, 2) == 0 && At(120, 10, 2) > 200 && At(120, 110, 1) > 200, "loop frame retains both edges and black side margins");
}
var source = new WindowRelay.CropRect(500, 98, 930, 860);
var displayed = WindowRelay.FitCrop(source, 155, 98);
var subtitleSource = new WindowRelay.CropRect(displayed.Left, source.Top, displayed.Right, source.Bottom);
var subtitle = WindowRelay.SubtitleBand(subtitleSource, 155, 12, 1.38);
var featherSource = new WindowRelay.CropRect(0, 0, 2410, 160);
var featherTop = WindowRelay.SubtitleSlice(featherSource, 0, 1, 16, 241, 241);
var featherNext = WindowRelay.SubtitleSlice(featherSource, 1, 1, 16, 241, 216);
var featherBody = WindowRelay.SubtitleSlice(featherSource, 2, 14, 16, 241, 241);
Check(featherTop.Bottom == featherNext.Top && featherNext.Bottom == featherBody.Top && featherBody.Bottom == featherSource.Bottom, "feathered slices preserve image alignment");
Check(featherNext.Right == 2160, "feather avoids the ad badge");
using (var testImage = new System.Drawing.Bitmap(400, 400))
{
    using var draw = System.Drawing.Graphics.FromImage(testImage);
    draw.Clear(System.Drawing.Color.Black);
    var region = new WindowRelay.CropRect(0, 0, 400, 400);
    Check(SubtitleRegionDetector.Detect(testImage, region) is null, "empty picture has no subtitle");
    for (var x = 120; x < 280; x += 12) draw.FillRectangle(System.Drawing.Brushes.White, x, 312, 5, 10);
    var detected = SubtitleRegionDetector.Detect(testImage, region);
    Check(detected is > .76 and < .82, "detect subtitle line above the bottom");
    var measured = SubtitleRegionDetector.DetectDetails(testImage, region);
    Check(measured is { WidthFraction: > .3, HeightFraction: > .01 }, "measure subtitle width and height");
    var adjusted = WindowRelay.SubtitleBand(region, 155, 12, 1.38, detected);
    Check(adjusted.Top < 317 && adjusted.Bottom > 317, "adjusted subtitle band includes detected line");
    var top = WindowRelay.SubtitleTop(region, region, detected, 98, 12);
    Check(top <= 317 * 98 / 400 && top + 12 >= 317 * 98 / 400, "subtitle overlay covers original line");
    Check(WindowRelay.SubtitleTop(region, region, null, 98, 12) == 86, "unrecognized subtitles retain bottom fallback");
    Check(!WindowRelay.SubtitleCenterAllowed(region, region, .55), "reject false text in the middle of the video");
    Check(WindowRelay.SubtitleTop(region, region, .55, 98, 12) == 86, "middle false positive cannot move subtitle overlay");
    Check(!WindowRelay.SubtitleCenterAllowed(new(0, 200, 400, 400), region, .65), "reject false positive after video crop");
}
var adaptiveRegion = new WindowRelay.CropRect(0, 0, 1000, 500);
var smallTextZoom = WindowRelay.AdaptiveSubtitleZoom(adaptiveRegion, 241, 16, 1.38, .35, .02);
var longTextZoom = WindowRelay.AdaptiveSubtitleZoom(adaptiveRegion, 241, 16, 1.38, .85, .02);
var tallTextZoom = WindowRelay.AdaptiveSubtitleZoom(adaptiveRegion, 241, 16, 1.38, .35, .10);
Check(Math.Abs(smallTextZoom - 1.38) < .001, "small subtitle keeps the requested zoom");
Check(longTextZoom < smallTextZoom && tallTextZoom < smallTextZoom, "large subtitles reduce zoom to reduce clipping");
Check(WindowRelay.AdaptiveSubtitleZoom(adaptiveRegion, 241, 16, 1.38, null, null) == 1.38, "missing detection retains the selected zoom");
var previousHeight = WindowRelay.SubtitleBand(subtitleSource, 155, 16, 1.38).Bottom - WindowRelay.SubtitleBand(subtitleSource, 155, 16, 1.38).Top;
Check(Math.Abs((displayed.Right - displayed.Left) / (double)(subtitle.Right - subtitle.Left) - 1.38) < .02 && subtitle.Bottom - subtitle.Top < previousHeight, "38 percent subtitle band is shorter and keeps its width");
Check(12d / (subtitle.Bottom - subtitle.Top) > 155d / (subtitle.Right - subtitle.Left), "subtitle is enlarged vertically more than horizontally");
var work = new System.Drawing.Rectangle(0, 0, 1920, 1080);
Check(SogouPanelTracker.CalculatePosition(new(1600, 900, 1750, 1000), work, 261, 191) == (1600, 741), "skin placement");
Check(SogouPanelTracker.CalculatePosition(new(1880, 10, 1910, 100), work, 261, 191) == (1659, 104), "screen edge placement");
Check(SogouPanelTracker.CalculatePosition(new(-20, 900, 20, 1020), new System.Drawing.Rectangle(-1920, 0, 1920, 1080), 261, 191) == (-261, 741), "secondary monitor placement");

var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4); image.Freeze();
var subtitle38 = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 255 }, 4); subtitle38.Freeze();
var captured = new RecentVideoFrames.CapturedFrame(image, image, subtitle38, image);
Check(captured.Subtitle(0) is null && captured.Subtitle(1) == image && captured.Subtitle(3) == subtitle38 && captured.Subtitle(2) == image, "loop uses selected subtitle zoom");
var frames = new RecentVideoFrames();
for (int i = 0; i <= 40; i++) frames.Add(i * 125, captured);
Check(frames.Snapshot().Length >= 20 && frames.Snapshot()[^1].Milliseconds - frames.Snapshot()[0].Milliseconds == 5000, "five-second loop");
for (int i = 41; i <= 80; i++) frames.Add(i * 125, captured);
Check(frames.Snapshot()[0].Milliseconds == 5000, "expired frames removed");
Console.WriteLine("Player layout, placement and loop subtitle tests passed.");
