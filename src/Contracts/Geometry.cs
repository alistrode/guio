namespace GuIO.Contracts;

/// <summary>像素坐标点。以设备屏幕为基准。</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public static PixelPoint operator +(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);
    public override string ToString() => $"{X},{Y}";
}

/// <summary>像素矩形（左上 + 右下），半开区间语义由使用方约定，默认含右/下边界。</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint Center => new(Left + Width / 2, Top + Height / 2);

    public static PixelRect FromSize(int x, int y, int w, int h) => new(x, y, x + w, y + h);

    public PixelRect Inflate(int dx, int dy) => new(Left - dx, Top - dy, Right + dx, Bottom + dy);

    public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    /// <summary>求与另一矩形的交集；无交集返回空矩形。</summary>
    public PixelRect Intersect(PixelRect other)
    {
        int l = Math.Max(Left, other.Left);
        int t = Math.Max(Top, other.Top);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r <= l || b <= t ? new PixelRect(0, 0, 0, 0) : new PixelRect(l, t, r, b);
    }

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

/// <summary>归一化点（0..1），相对目标客户区。换设备/换分辨率不重标。</summary>
public readonly record struct NormPoint(double X, double Y)
{
    public PixelPoint ToPixels(PixelRect area) =>
        new(area.Left + (int)Math.Round(X * area.Width), area.Top + (int)Math.Round(Y * area.Height));

    public override string ToString() => $"{X:0.####},{Y:0.####}";
}

/// <summary>归一化矩形（0..1）。</summary>
public readonly record struct NormRect(double X, double Y, double W, double H)
{
    public PixelRect ToPixels(PixelRect area) => new(
        area.Left + (int)Math.Round(X * area.Width),
        area.Top + (int)Math.Round(Y * area.Height),
        area.Left + (int)Math.Round((X + W) * area.Width),
        area.Top + (int)Math.Round((Y + H) * area.Height));
}
