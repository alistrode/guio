using System.IO.Compression;
using GuIO.Contracts;

namespace GuIO.Vision;

/// <summary>
/// 纯托管 PNG 编解码（零外部依赖，跨平台一致）。
/// 编码：8 位 RGBA，逐行 filter 0，zlib(deflate) 压缩。
/// 解码：支持 8 位灰/灰度+alpha/RGB/RGBA，filter 0-4，非隔行。
/// </summary>
public sealed class PngFrameCodec : IFrameCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public byte[] EncodePng(Frame frame)
    {
        int w = frame.Width, h = frame.Height;
        // 原始扫描线：每行 1 字节 filter(0) + RGBA
        var raw = new byte[h * (1 + w * 4)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            int rowStart = y * frame.Stride;
            for (int x = 0; x < w; x++)
            {
                int i = rowStart + x * 4;
                raw[o++] = frame.Pixels[i + 2]; // R
                raw[o++] = frame.Pixels[i + 1]; // G
                raw[o++] = frame.Pixels[i];     // B
                raw[o++] = frame.Pixels[i + 3]; // A
            }
        }

        using var ms = new MemoryStream();
        ms.Write(Signature, 0, Signature.Length);

        // IHDR
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w);
        WriteBE(ihdr, 4, h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT
        byte[] compressed;
        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            compressed = comp.ToArray();
        }
        WriteChunk(ms, "IDAT", compressed);

        // IEND
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    public Frame DecodePng(byte[] png, PixelRect sourceRect)
    {
        var f = Decode(png);
        return f with { SourceRect = sourceRect };
    }

    public Frame DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    public void SavePng(Frame frame, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, EncodePng(frame));
    }

    private static Frame Decode(byte[] png)
    {
        if (png.Length < 8) throw new InvalidDataException("不是 PNG（过短）");
        for (int i = 0; i < 8; i++)
            if (png[i] != Signature[i]) throw new InvalidDataException("PNG 签名不匹配");

        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        var palette = Array.Empty<byte>();

        while (pos + 8 <= png.Length)
        {
            int len = ReadBE(png, pos); pos += 4;
            string type = System.Text.Encoding.ASCII.GetString(png, pos, 4); pos += 4;
            int dataStart = pos;
            if (dataStart + len > png.Length) break;

            switch (type)
            {
                case "IHDR":
                    width = ReadBE(png, dataStart);
                    height = ReadBE(png, dataStart + 4);
                    bitDepth = png[dataStart + 8];
                    colorType = png[dataStart + 9];
                    interlace = png[dataStart + 12];
                    break;
                case "PLTE":
                    palette = png[dataStart..(dataStart + len)];
                    break;
                case "IDAT":
                    idat.Write(png, dataStart, len);
                    break;
                case "IEND":
                    pos = png.Length; // 结束
                    goto done;
            }
            pos = dataStart + len + 4; // 跳过数据 + CRC
        }
    done:

        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG 尺寸无效");
        if (bitDepth != 8) throw new NotSupportedException($"仅支持 8 位 PNG（当前 {bitDepth}）");
        if (interlace != 0) throw new NotSupportedException("不支持隔行 PNG");

        int bpp = colorType switch
        {
            0 => 1, // gray
            2 => 3, // RGB
            3 => 1, // palette index
            4 => 2, // gray+alpha
            6 => 4, // RGBA
            _ => throw new NotSupportedException($"不支持的颜色类型 {colorType}"),
        };

        idat.Position = 0;
        byte[] raw;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            z.CopyTo(outMs);
            raw = outMs.ToArray();
        }

        int stride = width * bpp;
        var unfiltered = Unfilter(raw, width, height, bpp, stride);

        int outStride = width * 4;
        var pixels = new byte[outStride * height];
        for (int y = 0; y < height; y++)
        {
            int src = y * stride;
            int dst = y * outStride;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0: r = g = b = unfiltered[src + x]; break;
                    case 4: r = g = b = unfiltered[src + x * 2]; a = unfiltered[src + x * 2 + 1]; break;
                    case 2: r = unfiltered[src + x * 3]; g = unfiltered[src + x * 3 + 1]; b = unfiltered[src + x * 3 + 2]; break;
                    case 6: r = unfiltered[src + x * 4]; g = unfiltered[src + x * 4 + 1]; b = unfiltered[src + x * 4 + 2]; a = unfiltered[src + x * 4 + 3]; break;
                    case 3:
                        int idx = unfiltered[src + x] * 3;
                        r = palette[idx]; g = palette[idx + 1]; b = palette[idx + 2];
                        break;
                    default: r = g = b = 0; break;
                }
                int d = dst + x * 4;
                pixels[d] = b; pixels[d + 1] = g; pixels[d + 2] = r; pixels[d + 3] = a;
            }
        }

        return new Frame
        {
            Width = width, Height = height, Stride = outStride, Pixels = pixels,
            SourceRect = new PixelRect(0, 0, width, height),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>还原 PNG 逐行滤波（filter 0-4）。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int bpp, int stride)
    {
        var outBuf = new byte[stride * height];
        int pos = 0;
        for (int y = 0; y < height; y++)
        {
            if (pos >= raw.Length) break;
            byte filter = raw[pos++];
            int rowStart = y * stride;
            int prevStart = (y - 1) * stride;
            for (int x = 0; x < stride; x++)
            {
                int rawByte = pos < raw.Length ? raw[pos++] : 0;
                int a = x >= bpp ? outBuf[rowStart + x - bpp] : 0;
                int b = y > 0 ? outBuf[prevStart + x] : 0;
                int c = (x >= bpp && y > 0) ? outBuf[prevStart + x - bpp] : 0;
                int value = filter switch
                {
                    0 => rawByte,
                    1 => rawByte + a,
                    2 => rawByte + b,
                    3 => rawByte + ((a + b) >> 1),
                    4 => rawByte + Paeth(a, b, c),
                    _ => rawByte,
                };
                outBuf[rowStart + x] = (byte)(value & 0xFF);
            }
        }
        return outBuf;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len, 0, 4);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, (int)crc);
        s.Write(crcBytes, 0, 4);
    }

    private static void WriteBE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static int ReadBE(byte[] buf, int offset) =>
        (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>兼容旧名（内部实现已切换为纯托管 PNG 编解码）。</summary>
public sealed class ImageSharpFrameCodec : IFrameCodec
{
    private readonly PngFrameCodec _inner = new();
    public byte[] EncodePng(Frame frame) => _inner.EncodePng(frame);
    public Frame DecodePng(byte[] png, PixelRect sourceRect) => _inner.DecodePng(png, sourceRect);
    public Frame DecodeFile(string path) => _inner.DecodeFile(path);
    public void SavePng(Frame frame, string path) => _inner.SavePng(frame, path);
}
