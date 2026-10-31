using System.Drawing;
using System.Drawing.Imaging;
using GuIO.Contracts;

namespace GuIO.Driver.Windows;

/// <summary>
/// 桌面截屏。除 Win32 PrintWindow/BitBlt 外，优先尝试 Windows.Graphics.Capture（WGC），
/// 它能截到 DirectX/Vulkan 游戏画面，克服 PrintWindow 对游戏窗口的黑屏问题。
/// </summary>
internal static class ScreenCapture
{
    /// <summary>截取窗口（含标题栏）。返回 Frame 或 null。</summary>
    public static Frame? CaptureWindow(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        // 1) WGC（现代，支持游戏）；失败则降级
        var wgc = TryWgcCapture(hwnd, w, h, r.Left, r.Top);
        if (wgc is not null) return wgc;

        // 2) PrintWindow（若不全黑）
        var pw = TryPrintWindow(hwnd, w, h, r.Left, r.Top);
        if (pw is not null && !IsMostlyBlack(pw)) return pw;

        // 3) BitBlt 屏幕抓取（最终兜底）
        return TryBitBlt(r.Left, r.Top, w, h);
    }

    /// <summary>截取屏幕区域。</summary>
    public static Frame? CaptureScreen(PixelRect region)
    {
        var wgc = TryWgcCaptureScreen(region);
        if (wgc is not null) return wgc;
        return TryBitBlt(region.Left, region.Top, region.Width, region.Height);
    }

    private static Frame? TryWgcCapture(IntPtr hwnd, int w, int h, int left, int top)
    {
        try
        {
            return WgcCapture.CaptureWindow(hwnd, new PixelRect(left, top, left + w, top + h));
        }
        catch { return null; }
    }

    private static Frame? TryWgcCaptureScreen(PixelRect region)
    {
        try { return WgcCapture.CaptureRegion(region); }
        catch { return null; }
    }

    private static Frame? TryPrintWindow(IntPtr hwnd, int w, int h, int left, int top)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            IntPtr dc = g.GetHdc();
            bool ok = WgcCapture.PrintWindow(hwnd, dc, 2);
            g.ReleaseHdc(dc);
            if (!ok) return null;
            return FromBitmap(bmp, new PixelRect(left, top, left + w, top + h));
        }
        catch { return null; }
    }

    private static Frame? TryBitBlt(int x, int y, int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            return FromBitmap(bmp, new PixelRect(x, y, x + w, y + h));
        }
        catch { return null; }
    }

    private static Frame FromBitmap(Bitmap bmp, PixelRect src)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var buf = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * stride, stride);
            return new Frame
            {
                Width = bmp.Width, Height = bmp.Height, Stride = stride, Pixels = buf,
                SourceRect = src,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
        finally { bmp.UnlockBits(data); }
    }

    private static bool IsMostlyBlack(Frame f)
    {
        int dark = 0, n = 0;
        for (int y = 0; y < f.Height; y += 8)
            for (int x = 0; x < f.Width; x += 8)
            {
                var (b, g, r, _) = f.GetPixel(x, y);
                if (b + g + r < 12) dark++;
                n++;
            }
        return n > 0 && dark > n * 0.98;
    }
}
