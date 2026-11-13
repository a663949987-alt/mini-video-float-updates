using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace MiniVideoFloat;

internal sealed class CropSelectionForm : Forms.Form
{
    private readonly IntPtr _sourceWindow;
    private readonly int _sourceWidth, _sourceHeight;
    private readonly PictureBox _preview;
    private readonly Bitmap _image;
    private Rectangle _selection;
    private Point _anchor;
    private bool _dragging;

    internal WindowRelay.CropRect SelectedCrop => new(_selection.Left, _selection.Top, _selection.Right, _selection.Bottom);

    internal CropSelectionForm(IntPtr sourceWindow, int sourceWidth, int sourceHeight, WindowRelay.CropRect initial)
    {
        _sourceWindow = sourceWindow; _sourceWidth = sourceWidth; _sourceHeight = sourceHeight;
        _image = CaptureClient(sourceWindow, sourceWidth, sourceHeight)
            ?? throw new InvalidOperationException("无法截取红果窗口。请先恢复红果窗口，并确保它没有最小化，再重新选择视频区域。");
        _selection = Clamp(Rectangle.FromLTRB(initial.Left, initial.Top, initial.Right, initial.Bottom));

        Text = "在截图上框选视频画面";
        Width = 920; Height = 640; MinimumSize = new Size(640, 440);
        StartPosition = Forms.FormStartPosition.CenterScreen;
        FormBorderStyle = Forms.FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;

        _preview = new PictureBox { Dock = Forms.DockStyle.Fill, BackColor = Color.FromArgb(28, 32, 40), Image = _image, SizeMode = Forms.PictureBoxSizeMode.Zoom, Cursor = Forms.Cursors.Cross };
        _preview.Paint += Preview_Paint;
        _preview.MouseDown += Preview_MouseDown;
        _preview.MouseMove += Preview_MouseMove;
        _preview.MouseUp += Preview_MouseUp;

        var help = new Forms.Label { Dock = Forms.DockStyle.Top, Height = 30, Text = "拖动鼠标框住视频画面；截图坐标会映射回红果窗口。", TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(8, 0, 0, 0) };
        var buttons = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 44, FlowDirection = Forms.FlowDirection.RightToLeft, Padding = new Forms.Padding(6) };
        var cancel = new Forms.Button { Text = "取消", DialogResult = Forms.DialogResult.Cancel, Width = 86 };
        var apply = new Forms.Button { Text = "应用裁切", Width = 96 };
        apply.Click += (_, _) => { if (_selection.Width >= 2 && _selection.Height >= 2) { DialogResult = Forms.DialogResult.OK; Close(); } };
        var reset = new Forms.Button { Text = "重新框选", Width = 92 };
        reset.Click += (_, _) => { _selection = Rectangle.Empty; _preview.Invalidate(); };
        buttons.Controls.AddRange([cancel, apply, reset]);
        Controls.Add(_preview); Controls.Add(help); Controls.Add(buttons);
        AcceptButton = apply; CancelButton = cancel;
    }

    private void Preview_Paint(object? sender, Forms.PaintEventArgs e)
    {
        var display = ImageDisplayRectangle();
        if (_selection.Width <= 0 || _selection.Height <= 0) return;
        var sx = display.Width / (float)_sourceWidth; var sy = display.Height / (float)_sourceHeight;
        var rect = new RectangleF(display.Left + _selection.Left * sx, display.Top + _selection.Top * sy, _selection.Width * sx, _selection.Height * sy);
        using var dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
        using var pen = new Pen(Color.FromArgb(255, 255, 196, 0), 2) { DashStyle = DashStyle.Solid };
        var saved = e.Graphics.Save();
        e.Graphics.SetClip(display);
        e.Graphics.FillRectangle(dim, display.Left, display.Top, display.Width, Math.Max(0, rect.Top - display.Top));
        e.Graphics.FillRectangle(dim, display.Left, rect.Bottom, display.Width, Math.Max(0, display.Bottom - rect.Bottom));
        e.Graphics.FillRectangle(dim, display.Left, rect.Top, Math.Max(0, rect.Left - display.Left), rect.Height);
        e.Graphics.FillRectangle(dim, rect.Right, rect.Top, Math.Max(0, display.Right - rect.Right), rect.Height);
        e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        e.Graphics.Restore(saved);
    }

    private void Preview_MouseDown(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        var p = PreviewToSource(e.Location);
        if (p is null) return;
        _anchor = p.Value; _selection = new Rectangle(_anchor, Size.Empty); _dragging = true; _preview.Invalidate();
    }

    private void Preview_MouseMove(object? sender, Forms.MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = PreviewToSource(e.Location);
        if (p is null) return;
        _selection = Rectangle.FromLTRB(Math.Min(_anchor.X, p.Value.X), Math.Min(_anchor.Y, p.Value.Y), Math.Max(_anchor.X, p.Value.X), Math.Max(_anchor.Y, p.Value.Y));
        _preview.Invalidate();
    }

    private void Preview_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var p = PreviewToSource(e.Location);
        if (p is not null)
            _selection = Clamp(Rectangle.FromLTRB(Math.Min(_anchor.X, p.Value.X), Math.Min(_anchor.Y, p.Value.Y), Math.Max(_anchor.X, p.Value.X), Math.Max(_anchor.Y, p.Value.Y)));
        _preview.Invalidate();
    }

    private Point? PreviewToSource(Point point)
    {
        var display = ImageDisplayRectangle();
        if (!display.Contains(point)) return null;
        var x = Math.Clamp((point.X - display.Left) / (float)display.Width, 0f, 1f);
        var y = Math.Clamp((point.Y - display.Top) / (float)display.Height, 0f, 1f);
        return new Point((int)Math.Round(x * _sourceWidth), (int)Math.Round(y * _sourceHeight));
    }

    private Rectangle ImageDisplayRectangle()
    {
        var area = _preview.ClientRectangle;
        var scale = Math.Min(area.Width / (float)_image.Width, area.Height / (float)_image.Height);
        var width = (int)Math.Round(_image.Width * scale); var height = (int)Math.Round(_image.Height * scale);
        return new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    private Rectangle Clamp(Rectangle rect)
    {
        var left = Math.Clamp(rect.Left, 0, _sourceWidth - 1);
        var top = Math.Clamp(rect.Top, 0, _sourceHeight - 1);
        var right = Math.Clamp(rect.Right, left + 1, _sourceWidth);
        var bottom = Math.Clamp(rect.Bottom, top + 1, _sourceHeight);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal static Bitmap? CaptureClient(IntPtr hwnd, int width, int height)
    {
        if (IsIconic(hwnd) || width <= 0 || height <= 0) return null;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(hwnd, hdc, 0x00000003); }
        finally { graphics.ReleaseHdc(hdc); }
        if (captured && HasVisiblePixels(bitmap)) return bitmap;
        bitmap.Dispose();
        return null;
    }

    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        var points = new[] { new Point(bitmap.Width / 4, bitmap.Height / 4), new Point(bitmap.Width / 2, bitmap.Height / 2), new Point(bitmap.Width * 3 / 4, bitmap.Height * 3 / 4) };
        var first = bitmap.GetPixel(points[0].X, points[0].Y).ToArgb();
        return points.Skip(1).Any(p => bitmap.GetPixel(p.X, p.Y).ToArgb() != first);
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
