using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;

namespace MagnaCarta.Data;

/// <summary>32비트 그림 한 장 — BGRA(곱하지 않은 알파), 위쪽 줄부터.</summary>
internal sealed class Image32(int width, int height)
{
    public readonly int Width = width, Height = height;
    public readonly byte[] Bgra = new byte[width * height * 4];

    /// <summary>알파 섞어 얹기(SrcOver). 밖으로 나가는 부분은 자른다.</summary>
    public void Blend(Image32 src, int x, int y)
    {
        for (int sy = 0; sy < src.Height; sy++)
        {
            int dy = y + sy;
            if (dy < 0 || dy >= Height) continue;
            for (int sx = 0; sx < src.Width; sx++)
            {
                int dx = x + sx;
                if (dx < 0 || dx >= Width) continue;
                int s = (sy * src.Width + sx) * 4, d = (dy * Width + dx) * 4;
                int a = src.Bgra[s + 3];
                if (a == 0) continue;
                if (a == 255) { Buffer.BlockCopy(src.Bgra, s, Bgra, d, 4); continue; }
                int da = Bgra[d + 3];
                int oa = a + da * (255 - a) / 255;
                for (int c = 0; c < 3; c++)
                    Bgra[d + c] = (byte)((src.Bgra[s + c] * a + Bgra[d + c] * da * (255 - a) / 255) / Math.Max(oa, 1));
                Bgra[d + 3] = (byte)oa;
            }
        }
    }

    public void Fill(int x, int y, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        for (int yy = Math.Max(0, y); yy < Math.Min(Height, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(Width, x + w); xx++)
            {
                int d = (yy * Width + xx) * 4;
                Bgra[d] = b; Bgra[d + 1] = g; Bgra[d + 2] = r; Bgra[d + 3] = a;
            }
    }
}

/// <summary>원본 그림 파일 풀이. 형식: 옵시디안 분석-렌더링-2D.</summary>
internal static class Images
{
    public const uint SoftmaxMagic = 0x25129319;   // 19 93 12 25

    // ── TGA (무압축 2 · RLE 10, 16/24/32비트) ─────────────────────────────────

    public static bool LooksTga(byte[] d, int o)
    {
        if (o + 18 > d.Length || d[o] != 0 || d[o + 1] != 0 || (d[o + 2] != 2 && d[o + 2] != 10)) return false;
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 12)), h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 14));
        int bpp = d[o + 16];
        return w is > 0 and <= 4096 && h is > 0 and <= 4096 && bpp is 16 or 24 or 32;
    }

    public static Image32 DecodeTga(byte[] d, int o)
    {
        int idLen = d[o], cmap = d[o + 1], type = d[o + 2];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 12)), h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 14));
        int bpp = d[o + 16], desc = d[o + 17];
        int p = o + 18 + idLen + (cmap != 0 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 5)) * ((d[o + 7] + 7) / 8) : 0);
        int bp = bpp / 8, n = w * h;
        byte[] raw;
        if (type == 2) raw = d.AsSpan(p, Math.Min(n * bp, d.Length - p)).ToArray();
        else
        {
            raw = new byte[n * bp];
            int q = 0;
            while (q < raw.Length && p < d.Length)
            {
                int c = d[p++];
                int count = (c & 0x7F) + 1;
                if ((c & 0x80) != 0)
                {
                    for (int k = 0; k < count && q < raw.Length; k++) { Array.Copy(d, p, raw, q, bp); q += bp; }
                    p += bp;
                }
                else
                {
                    int len = Math.Min(count * bp, raw.Length - q);
                    Array.Copy(d, p, raw, q, len); q += len; p += count * bp;
                }
            }
        }
        var img = new Image32(w, h);
        bool topDown = (desc & 0x20) != 0;
        for (int y = 0; y < h; y++)
        {
            int srcRow = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                int s = (srcRow * w + x) * bp, t = (y * w + x) * 4;
                if (s + bp > raw.Length) continue;
                if (bp == 4) { img.Bgra[t] = raw[s]; img.Bgra[t + 1] = raw[s + 1]; img.Bgra[t + 2] = raw[s + 2]; img.Bgra[t + 3] = raw[s + 3]; }
                else if (bp == 3) { img.Bgra[t] = raw[s]; img.Bgra[t + 1] = raw[s + 1]; img.Bgra[t + 2] = raw[s + 2]; img.Bgra[t + 3] = 255; }
                else
                {
                    int v = raw[s] | raw[s + 1] << 8;
                    img.Bgra[t] = (byte)((v & 31) * 255 / 31);
                    img.Bgra[t + 1] = (byte)((v >> 5 & 31) * 255 / 31);
                    img.Bgra[t + 2] = (byte)((v >> 10 & 31) * 255 / 31);
                    img.Bgra[t + 3] = (byte)((v & 0x8000) != 0 || (desc & 15) == 0 ? 255 : 0);
                }
            }
        }
        return img;
    }

    /// <summary>BMP · JPEG · GIF — System.Drawing 으로 푼다.</summary>
    public static Image32 DecodeOther(byte[] d, int o)
    {
        using var ms = new MemoryStream(d, o, d.Length - o);
        using var bmp = new Bitmap(ms);
        var img = new Image32(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, img.Bgra, y * bmp.Width * 4, bmp.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        return img;
    }

    // ── TXRS\%04d.txr: u32 갈래(0 BMP 1 JPEG 2 GIF 5 TGA), 쓰레기 28바이트, 0x20 부터 그림 파일 ──

    public static Image32 Txr(int number)
    {
        var d = McFiles.Read("TXRS", number, "txr");
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(d);
        if (kind == 5 || LooksTga(d, 0x20)) return DecodeTga(d, 0x20);
        var img = DecodeOther(d, 0x20);
        // TGA 가 아닌 그림(0 BMP · 1 JPEG · 2 GIF)은 원본이 위아래 뒤집힌 줄 차례로 쓴다(분석-렌더링-2D/3D 「텍스처 위아래」).
        // 증거: 맵 172 하늘 3036(JPEG) 은 v 0 = 아래(z −396) — 안 뒤집으면 필드 199 하늘이 거꾸로. 로프마 0100(JPEG 152/153)·
        // 0051(BMP 여럿) 은 뒤집어야 얼굴·옷이 제자리. TGA 는 머리의 원점 깃발대로 읽은 지금이 맞다. 비교용 MC_TEX_NOFLIP=1
        if (!TexNoFlip) FlipRows(img);
        return img;
    }

    private static readonly bool TexNoFlip = Environment.GetEnvironmentVariable("MC_TEX_NOFLIP") is { Length: > 0 };

    private static void FlipRows(Image32 img)
    {
        int row = img.Width * 4;
        var tmp = new byte[row];
        for (int y = 0; y < img.Height / 2; y++)
        {
            int a = y * row, b = (img.Height - 1 - y) * row;
            Buffer.BlockCopy(img.Bgra, a, tmp, 0, row);
            Buffer.BlockCopy(img.Bgra, b, img.Bgra, a, row);
            Buffer.BlockCopy(tmp, 0, img.Bgra, b, row);
        }
    }

    // ── BGR\%04d.BGR: 머리, u32 n, 사각형 n×16, u32 갈래(5 TGA · 0 BMP), [0 12바이트], 그림 파일 ──

    public static Image32 Bgr(int number)
    {
        var d = McFiles.Read("BGR", number, "BGR");
        if (BinaryPrimitives.ReadUInt32LittleEndian(d) != SoftmaxMagic) throw new InvalidDataException($"BGR {number} 머리가 아님");
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        int p = 8 + 16 * n;
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
        p += 4;
        if (kind == 5)
        {
            if (!LooksTga(d, p)) p += 12;
            return DecodeTga(d, p);
        }
        int bm = d.AsSpan(p, Math.Min(16, d.Length - p)).IndexOf("BM"u8);
        return DecodeOther(d, p + Math.Max(bm, 0));
    }
}

/// <summary>FrameData\%04d.fdi — 2D 화면 한 장의 짜임(원소 15칸). 그림(BGR)·색 채움만 그린다.</summary>
internal sealed class FrameData
{
    public record Element(int Anchor, int X, int Y, int Kind, int Id, int Sub, int Wdi, int W, int H);

    public string Name = "";
    public int X, Y, W, H;
    public readonly List<Element> Elements = [];

    public static FrameData Load(int number)
    {
        var d = McFiles.Read("FrameData", number, "fdi");
        var f = new FrameData();
        int nul = Array.IndexOf(d, (byte)0, 12, 128);
        f.Name = McFiles.Cp949.GetString(d, 12, (nul < 0 ? 140 : nul) - 12);
        var r = new Reader(d, 141);
        f.X = r.I32(); f.Y = r.I32(); f.W = r.I32(); f.H = r.I32();
        for (int i = 0, o = 157; i < 15 && o + 42 <= d.Length; i++, o += 42)
        {
            if (d[o] == 0) continue;
            r.Pos = o + 2;
            int anchor = r.I32(), x = r.I32(), y = r.I32(), kind = r.I32(), id = r.I32(), sub = r.I32(), wdi = r.I32(), w = r.I32(), h = r.I32();
            f.Elements.Add(new Element(anchor, x, y, kind, id, sub, wdi, w, h));
        }
        return f;
    }

    /// <summary>원소 x,y 의 기준점(MCFrame 0x5204e00b 점프표).</summary>
    public static (int, int) AnchorPoint(int anchor, int w, int h) => anchor switch
    {
        2 => (w, 0), 3 => (0, h), 4 => (w, h), 5 => (w / 2, 0), 6 => (w / 2, h),
        7 => (0, h / 2), 8 => (w, h / 2), 9 => (w / 2, h / 2), _ => (0, 0),
    };

    /// <summary>한 장으로 짜 맞춘다. 크기가 0 이면 그림들을 덮는 크기.</summary>
    public Image32 Compose()
    {
        var bgrs = new Dictionary<int, Image32>();
        foreach (var e in Elements) if (e.Kind == 3 && !bgrs.ContainsKey(e.Id)) bgrs[e.Id] = Images.Bgr(e.Id);
        int w = W, h = H;
        if (w <= 0 || h <= 0)
        {
            w = h = 1;
            foreach (var e in Elements)
                if (e.Kind == 3) { w = Math.Max(w, e.X + bgrs[e.Id].Width); h = Math.Max(h, e.Y + bgrs[e.Id].Height); }
        }
        var img = new Image32(w, h);
        foreach (var e in Elements)
        {
            var (ax, ay) = AnchorPoint(e.Anchor, W, H);
            if (e.Kind == 3) img.Blend(bgrs[e.Id], ax + e.X, ay + e.Y);
            else if (e.Kind == 4)
            {
                var (r, g, b) = WinDi.Colors(e.Wdi)[0];
                img.Fill(ax + e.X, ay + e.Y, e.W, e.H, r, g, b);
            }
        }
        return img;
    }
}

/// <summary>WinDiData\%04d.wdi — 색 8개. 8번(검정)이 페이드 색, 3·4번이 글자 색.</summary>
internal static class WinDi
{
    public static (byte R, byte G, byte B)[] Colors(int number)
    {
        var d = McFiles.Read("WinDiData", number, "wdi");
        var c = new (byte, byte, byte)[8];
        for (int i = 0; i < 8; i++) c[i] = (d[13 + 4 * i], d[14 + 4 * i], d[15 + 4 * i]);
        return c;
    }
}

/// <summary>
/// 원본 비트맵 글꼴 Font\{Eng,Kor,Chi}{0,1}.dat. 세트0 = 14줄×u16, 세트1 = 22줄×3바이트, 비트0 = 왼쪽.
/// 번호: 첫 바이트 &lt; 0xA4 → Eng[b−0x20], &lt; 0xB0 → Eng[code−0xA3A0], &lt; 0xCA → Kor[code−0xB0A0], 나머지 Chi[code−0xCAA0].
/// </summary>
internal sealed class BitmapFont
{
    private readonly int _rows, _rowBytes;
    private readonly Dictionary<string, (byte[]? Widths, byte[] Data, int Offset)> _parts = [];

    public int Height => _rows;

    public BitmapFont(int set)
    {
        (_rows, _rowBytes) = set == 0 ? (14, 2) : (22, 3);
        foreach (var k in new[] { "Eng", "Kor", "Chi" })
        {
            var d = File.ReadAllBytes(Path.Combine(McFiles.Dir, "Font", $"{k}{set}.dat"));
            int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x5C));
            bool hasWidths = d[0x2A] != 0;
            _parts[k] = (hasWidths ? d.AsSpan(100, count).ToArray() : null, d, 100 + (hasWidths ? count : 0));
        }
    }

    private IEnumerable<(string Part, int Index)> Glyphs(string text)
    {
        var b = McFiles.Cp949.GetBytes(text);
        for (int i = 0; i < b.Length;)
        {
            int c = b[i];
            if (c < 0xA4) { yield return ("Eng", c - 0x20); i++; continue; }
            int code = c << 8 | (i + 1 < b.Length ? b[i + 1] : 0);
            i += 2;
            if (c < 0xB0) yield return ("Eng", code - 0xA3A0);
            else if (c < 0xCA) yield return ("Kor", code - 0xB0A0);
            else yield return ("Chi", code - 0xCAA0);
        }
    }

    public int Draw(Image32 img, int x, int y, string text, byte r, byte g, byte b)
    {
        int glyphBytes = _rows * _rowBytes;
        foreach (var (part, idx) in Glyphs(text))
        {
            var (widths, data, off) = _parts[part];
            if (idx < 0) continue;
            int w = widths != null && idx < widths.Length ? widths[idx] : _rows;
            int at = off + idx * glyphBytes;
            if (at + glyphBytes > data.Length) { x += w; continue; }
            for (int yy = 0; yy < _rows; yy++)
            {
                int row = 0;
                for (int k = 0; k < _rowBytes; k++) row |= data[at + yy * _rowBytes + k] << (8 * k);
                for (int xx = 0; xx < Math.Min(w, _rowBytes * 8); xx++)
                    if ((row >> xx & 1) != 0) img.Fill(x + xx, y + yy, 1, 1, r, g, b);
            }
            x += w;
        }
        return x;
    }
}
