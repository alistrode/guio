using GuIO.Contracts;

namespace GuIO.Vision;

/// <summary>候选元素编号叠加（Set-of-Mark 风格）：把可点候选与文字块画上编号，
/// 供学习型控制律（LLM）按编号决策。纯像素绘制，不依赖绘图库，跨平台一致。</summary>
public static class MarkupOverlay
{
    public sealed record Mark(int Index, PixelRect Bounds, string Label);

    /// <summary>从语义树节点 + OCR 文本块生成候选标记。</summary>
    public static IReadOnlyList<Mark> BuildMarks(Observation obs, int max = 200)
    {
        var marks = new List<Mark>();
        int i = 1;
        foreach (var n in obs.Nodes.Where(n => n.Visible && !n.Bounds.IsEmpty))
        {
            if (marks.Count >= max) break;
            marks.Add(new Mark(i++, n.Bounds, n.Name ?? n.Text ?? n.Kind));
        }
        foreach (var t in obs.Text)
        {
            if (marks.Count >= max) break;
            if (t.Bounds.IsEmpty) continue;
            if (marks.Any(m => m.Bounds.Intersect(t.Bounds).Width > t.Bounds.Width / 2)) continue;
            marks.Add(new Mark(i++, t.Bounds, t.Text));
        }
        return marks;
    }

    /// <summary>把编号叠加到帧上，返回新帧（BGRA32）。</summary>
    public static Frame Annotate(Frame frame, IReadOnlyList<Mark> marks)
    {
        var buf = (byte[])frame.Pixels.Clone();
        foreach (var m in marks)
        {
            DrawRect(buf, frame.Stride, frame.Width, frame.Height, m.Bounds, 0, 0, 255, 2);
            DrawTag(buf, frame.Stride, frame.Width, frame.Height, m.Index.ToString(),
                Math.Max(0, m.Bounds.Left), Math.Max(0, m.Bounds.Top - 13));
        }
        return frame with { Pixels = buf };
    }

    private static void SetPx(byte[] buf, int stride, int w, int h, int x, int y, byte b, byte g, byte r)
    {
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        int i = y * stride + x * 4;
        buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
    }

    private static void DrawRect(byte[] buf, int stride, int w, int h, PixelRect rc, byte b, byte g, byte r, int t)
    {
        for (int k = 0; k < t; k++)
        {
            for (int x = rc.Left; x < rc.Right; x++)
            {
                SetPx(buf, stride, w, h, x, rc.Top + k, b, g, r);
                SetPx(buf, stride, w, h, x, rc.Bottom - 1 - k, b, g, r);
            }
            for (int y = rc.Top; y < rc.Bottom; y++)
            {
                SetPx(buf, stride, w, h, rc.Left + k, y, b, g, r);
                SetPx(buf, stride, w, h, rc.Right - 1 - k, y, b, g, r);
            }
        }
    }

    // 3x5 点阵数字，用于编号标签
    private static readonly string[] DigitGlyphs =
    {
        "111101101101111","010110010010111","111001111100111","111001111001111",
        "101101111001001","111100111001111","111100111101111","111001001001001",
        "111101111101111","111101111001111",
    };

    private static void DrawTag(byte[] buf, int stride, int w, int h, string text, int x, int y)
    {
        int cx = x;
        foreach (char ch in text)
        {
            if (ch is >= '0' and <= '9')
            {
                var glyph = DigitGlyphs[ch - '0'];
                for (int row = 0; row < 5; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        int bit = row * 3 + col;
                        if (glyph[bit] == '1')
                        {
                            // 背景黑描边 + 黄色前景
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                    SetPx(buf, stride, w, h, cx + col * 2 + dx, y + row * 2 + dy, 0, 0, 0);
                            SetPx(buf, stride, w, h, cx + col * 2, y + row * 2, 0, 220, 255);
                        }
                    }
                cx += 3 * 2 + 2;
            }
        }
    }
}
