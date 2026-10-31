using GuIO.Contracts;
using GuIO.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GuIO.Driver.Windows;

/// <summary>Windows 系统 OCR（Windows.Media.Ocr）。中文识别开箱即用，作为视觉层 OCR 的本地实现。</summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine;
    private readonly string _lang;

    public WindowsOcrEngine(string lang = "zh-Hans-CN")
    {
        _lang = lang;
        OcrEngine? engine = null;
        try { engine = OcrEngine.TryCreateFromLanguage(new Language(lang)); } catch { }
        if (engine is null)
        {
            try { engine = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
        }
        _engine = engine;
    }

    public bool IsAvailable => _engine is not null;

    public async Task<IReadOnlyList<TextBlock>> RecognizeAsync(Frame frame, CancellationToken ct = default)
    {
        if (_engine is null) return Array.Empty<TextBlock>();

        var png = new ImageSharpFrameCodec().EncodePng(frame);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();

        var result = await _engine.RecognizeAsync(bitmap);
        var blocks = new List<TextBlock>();
        foreach (var line in result.Lines)
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = 0, y2 = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                x1 = Math.Min(x1, (int)r.X);
                y1 = Math.Min(y1, (int)r.Y);
                x2 = Math.Max(x2, (int)Math.Ceiling(r.X + r.Width));
                y2 = Math.Max(y2, (int)Math.Ceiling(r.Y + r.Height));
            }
            if (x1 == int.MaxValue) continue;
            blocks.Add(new TextBlock
            {
                Text = line.Text,
                Bounds = new PixelRect(x1, y1, x2, y2),
                Confidence = 1.0,
            });
        }
        return blocks;
    }
}
