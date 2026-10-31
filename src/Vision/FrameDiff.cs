using GuIO.Contracts;

namespace GuIO.Vision;

/// <summary>帧差/变化检测：判断画面是否变化、哪块区域变了。用于「等待变化」「防振荡」判定。</summary>
public static class FrameDiff
{
    /// <summary>整体平均差异 0..255。</summary>
    public static double MeanDifference(Frame a, Frame b, PixelRect? region = null)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var r = region ?? new PixelRect(0, 0, w, h);
        int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top);
        int x1 = Math.Min(w, r.Right), y1 = Math.Min(h, r.Bottom);
        if (x1 <= x0 || y1 <= y0) return 0;

        double sum = 0; long n = 0;
        for (int y = y0; y < y1; y++)
        {
            int ra = y * a.Stride, rb = y * b.Stride;
            for (int x = x0; x < x1; x++)
            {
                int i = ra + x * 4, j = rb + x * 4;
                sum += Math.Abs(a.Pixels[i] - b.Pixels[j]);
                sum += Math.Abs(a.Pixels[i + 1] - b.Pixels[j + 1]);
                sum += Math.Abs(a.Pixels[i + 2] - b.Pixels[j + 2]);
                n += 3;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>变化像素占比 0..1（按阈值判定单像素是否算变化）。</summary>
    public static double ChangedRatio(Frame a, Frame b, int threshold = 20, PixelRect? region = null)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var r = region ?? new PixelRect(0, 0, w, h);
        int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top);
        int x1 = Math.Min(w, r.Right), y1 = Math.Min(h, r.Bottom);
        if (x1 <= x0 || y1 <= y0) return 0;

        long changed = 0, total = 0;
        for (int y = y0; y < y1; y++)
        {
            int ra = y * a.Stride, rb = y * b.Stride;
            for (int x = x0; x < x1; x++)
            {
                int i = ra + x * 4, j = rb + x * 4;
                int d = Math.Abs(a.Pixels[i] - b.Pixels[j])
                      + Math.Abs(a.Pixels[i + 1] - b.Pixels[j + 1])
                      + Math.Abs(a.Pixels[i + 2] - b.Pixels[j + 2]);
                if (d >= threshold * 3) changed++;
                total++;
            }
        }
        return total == 0 ? 0 : (double)changed / total;
    }
}

/// <summary>颜色/像素匹配：在某区域内查找近似色块（用于工具栏图标、血条等）。</summary>
public static class ColorMatcher
{
    public static IReadOnlyList<MatchResult> FindColor(Frame f, byte r, byte g, byte b, int tolerance, PixelRect? region, int minRun = 4)
    {
        int x0 = Math.Max(0, (region ?? new PixelRect(0, 0, f.Width, f.Height)).Left);
        int y0 = Math.Max(0, (region ?? new PixelRect(0, 0, f.Width, f.Height)).Top);
        int x1 = Math.Min(f.Width, (region ?? new PixelRect(0, 0, f.Width, f.Height)).Right);
        int y1 = Math.Min(f.Height, (region ?? new PixelRect(0, 0, f.Width, f.Height)).Bottom);

        var hits = new List<MatchResult>();
        var visited = new bool[(x1 - x0) * (y1 - y0)];
        int w = x1 - x0;

        bool Close(int x, int y)
        {
            int i = y * f.Stride + x * 4;
            return Math.Abs(f.Pixels[i] - b) <= tolerance
                && Math.Abs(f.Pixels[i + 1] - g) <= tolerance
                && Math.Abs(f.Pixels[i + 2] - r) <= tolerance;
        }

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (visited[(y - y0) * w + (x - x0)]) continue;
                if (!Close(x, y)) continue;

                // 简单洪水扩张求连通块包围盒
                int minX = x, maxX = x, minY = y, maxY = y;
                var stack = new Stack<(int, int)>();
                stack.Push((x, y));
                int area = 0;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    if (cx < x0 || cy < y0 || cx >= x1 || cy >= y1) continue;
                    int vi = (cy - y0) * w + (cx - x0);
                    if (visited[vi]) continue;
                    if (!Close(cx, cy)) continue;
                    visited[vi] = true;
                    area++;
                    minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                    minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                    stack.Push((cx + 1, cy)); stack.Push((cx - 1, cy));
                    stack.Push((cx, cy + 1)); stack.Push((cx, cy - 1));
                }
                if (area >= minRun * minRun)
                    hits.Add(new MatchResult(new PixelRect(minX, minY, maxX + 1, maxY + 1), area));
            }
        }
        hits.Sort((a, b2) => b2.Score.CompareTo(a.Score));
        return hits;
    }
}
