using System.Numerics;
using MagnaCarta.Data;
using Vortice.Direct3D11;

namespace MagnaCarta.Render;

/// <summary>화면 반 크기의 2D 판 — 틀마다 비우고 글·막대를 그린 뒤 2배로 늘려 얹는다. 글꼴은 원본 비트맵 글꼴.</summary>
internal sealed class Overlay : IDisposable
{
    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private ID3D11Texture2D _tex;
    private ID3D11ShaderResourceView _view;
    public Image32 Image { get; private set; }
    public readonly BitmapFont Font = new(0);
    public const int Scale = 2;

    public Overlay(Gfx gfx, ScreenImage screen)
    {
        _gfx = gfx;
        _screen = screen;
        Image = new Image32(gfx.Width / Scale, gfx.Height / Scale);
        _tex = gfx.DynamicTexture(Image.Width, Image.Height, out _view);
    }

    public int Width => Image.Width;
    public int Height => Image.Height;

    public void Clear()
    {
        if (Image.Width != _gfx.Width / Scale || Image.Height != _gfx.Height / Scale)
        {
            _view.Dispose(); _tex.Dispose();
            Image = new Image32(Math.Max(1, _gfx.Width / Scale), Math.Max(1, _gfx.Height / Scale));
            _tex = _gfx.DynamicTexture(Image.Width, Image.Height, out _view);
        }
        Array.Clear(Image.Bgra);
    }

    public int Text(int x, int y, string s, byte r = 235, byte g = 225, byte b = 200, bool shadow = true)
    {
        if (shadow) Font.Draw(Image, x + 1, y + 1, s, 0, 0, 0);
        return Font.Draw(Image, x, y, s, r, g, b);
    }

    public int TextWidth(string s)
    {
        var tmp = new Image32(1, 1);
        return Font.Draw(tmp, 0, 0, s, 0, 0, 0);
    }

    public void Box(int x, int y, int w, int h, byte r, byte g, byte b, byte a = 255) => Image.Fill(x, y, w, h, r, g, b, a);

    public void Bar(int x, int y, int w, int h, float frac, (byte R, byte G, byte B) color)
    {
        Box(x - 1, y - 1, w + 2, h + 2, 0, 0, 0, 200);
        Box(x, y, w, h, 40, 36, 32, 220);
        Box(x, y, (int)(w * Math.Clamp(frac, 0, 1)), h, color.R, color.G, color.B);
    }

    public void Present()
    {
        _gfx.UploadTexture(_tex, Image);
        _screen.Draw(_view, 0, 0, Image.Width * Scale, Image.Height * Scale, Vector4.One, point: true);
    }

    public void Dispose() { _view.Dispose(); _tex.Dispose(); }
}
