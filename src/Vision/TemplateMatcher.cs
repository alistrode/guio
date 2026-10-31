using GuIO.Contracts;

namespace GuIO.Vision;

/// <summary>模板匹配结果。</summary>
public sealed record MatchResult(PixelRect Bounds, double Score);

/// <summary>OCR 引擎抽象。实现可平台提供（Windows 用系统 OCR），也可为 ONNX 版 PP-OCR。</summary>
public interface IOcrEngine
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<TextBlock>> RecognizeAsync(Frame frame, CancellationToken ct = default);
}

/// <summary>模板匹配抽象。</summary>
public interface ITemplateMatcher
{
    /// <summary>在 haystack 中查找模板，返回全部超过阈值的命中（按分数降序）。</summary>
    IReadOnlyList<MatchResult> FindAll(Frame haystack, Frame template, double threshold, PixelRect? region);
}

/// <summary>
/// 归一化互相关（NCC）模板匹配，纯托管、跨平台。
/// 采用灰度 + 逐位置相关，配合步长加速；足以覆盖 UI 图标/按钮定位。
/// </summary>
public sealed class TemplateMatcher : ITemplateMatcher
{
    public IReadOnlyList<MatchResult> FindAll(Frame haystack, Frame template, double threshold, PixelRect? region)
    {
        var hs = ToGray(haystack);
        var ts = ToGray(template);
        int hw = haystack.Width, hh = haystack.Height;
        int tw = template.Width, th = template.Height;
        if (tw > hw || th > hh) return Array.Empty<MatchResult>();

        var search = region ?? new PixelRect(0, 0, hw, hh);
        int x0 = Math.Max(0, search.Left), y0 = Math.Max(0, search.Top);
        int x1 = Math.Min(hw - tw, search.Right - tw), y1 = Math.Min(hh - th, search.Bottom - th);
        if (x1 < x0 || y1 < y0) return Array.Empty<MatchResult>();

        (double tMean, double tNorm) = Normalize(ts, tw, th, 0, 0);
        if (tNorm <= 1e-6) return Array.Empty<MatchResult>();

        var hits = new List<MatchResult>();
        // 粗步长扫描后再精修，兼顾速度
        int coarse = Math.Max(1, Math.Min(tw, th) / 8);
        for (int y = y0; y <= y1; y += coarse)
        {
            for (int x = x0; x <= x1; x += coarse)
            {
                double s = Correlate(hs, hw, ts, tw, th, x, y, tMean, tNorm);
                if (s >= threshold) hits.Add(new MatchResult(new PixelRect(x, y, x + tw, y + th), s));
            }
        }

        // 精修：对粗命中周边做 1px 扫描，取局部最优
        var refined = new List<MatchResult>();
        foreach (var hit in hits)
        {
            var best = hit;
            for (int dy = -coarse + 1; dy <= coarse - 1; dy++)
                for (int dx = -coarse + 1; dx <= coarse - 1; dx++)
                {
                    int nx = hit.Bounds.Left + dx, ny = hit.Bounds.Top + dy;
                    if (nx < x0 || ny < y0 || nx > x1 || ny > y1) continue;
                    double s = Correlate(hs, hw, ts, tw, th, nx, ny, tMean, tNorm);
                    if (s > best.Score) best = new MatchResult(new PixelRect(nx, ny, nx + tw, ny + th), s);
                }
            refined.Add(best);
        }

        // 去重：抑制重叠框，保留高分
        refined.Sort((a, b) => b.Score.CompareTo(a.Score));
        var kept = new List<MatchResult>();
        foreach (var c in refined)
        {
            bool dup = kept.Any(k => k.Bounds.Intersect(c.Bounds).Width > c.Bounds.Width / 2);
            if (!dup) kept.Add(c);
        }
        return kept;
    }

    private static double Correlate(double[] hs, int hw, double[] ts, int tw, int th, int x, int y, double tMean, double tNorm)
    {
        double s = 0, s2 = 0, st = 0;
        int n = tw * th;
        for (int j = 0; j < th; j++)
        {
            int hrow = (y + j) * hw + x;
            int trow = j * tw;
            for (int i = 0; i < tw; i++)
            {
                double h = hs[hrow + i];
                double t = ts[trow + i];
                s += h; s2 += h * h; st += h * t;
            }
        }
        double hMean = s / n;
        double hVar = s2 - n * hMean * hMean;
        if (hVar <= 1e-6) return 0;
        double cov = st - n * hMean * tMean;
        return cov / (Math.Sqrt(hVar) * tNorm);
    }

    private static (double mean, double norm) Normalize(double[] t, int tw, int th, int ox, int oy)
    {
        int n = tw * th; double s = 0;
        for (int i = 0; i < n; i++) s += t[i];
        double mean = s / n;
        double var = 0;
        for (int i = 0; i < n; i++) { double d = t[i] - mean; var += d * d; }
        return (mean, Math.Sqrt(var));
    }

    public static double[] ToGray(Frame f)
    {
        var g = new double[f.Width * f.Height];
        for (int y = 0; y < f.Height; y++)
        {
            int b = y * f.Stride;
            int o = y * f.Width;
            for (int x = 0; x < f.Width; x++)
            {
                int i = b + x * 4;
                g[o + x] = 0.114 * f.Pixels[i] + 0.587 * f.Pixels[i + 1] + 0.299 * f.Pixels[i + 2];
            }
        }
        return g;
    }
}
