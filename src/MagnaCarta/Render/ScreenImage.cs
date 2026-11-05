using System.Numerics;
using Vortice.Direct3D11;

namespace MagnaCarta.Render;

/// <summary>2D 그림 한 장을 화면의 네모에 그린다(정점 버퍼 없이).</summary>
internal sealed class ScreenImage : IDisposable
{
    private readonly Gfx _gfx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;

    public ScreenImage(Gfx gfx)
    {
        _gfx = gfx;
        (_vs, _ps, _) = gfx.Compile(Shaders.Source, "ScreenVS", "ScreenPS");
    }

    /// <summary>화소 좌표 (x, y, w, h) 에 그린다. <paramref name="tint"/> 의 w 가 불투명도.</summary>
    public void Draw(ID3D11ShaderResourceView tex, float x, float y, float w, float h, Vector4 tint, bool point = false, bool additive = false)
    {
        float sw = _gfx.Width, sh = _gfx.Height;
        var c = new Constants
        {
            Rect = new Vector4(x / sw * 2 - 1, 1 - y / sh * 2, (x + w) / sw * 2 - 1, 1 - (y + h) / sh * 2),
            Tint = tint,
            Fog = new Vector4(0, 0, 0, point ? 1 : 0),
        };
        _gfx.Set(c);
        _gfx.Overlay();
        if (additive) _gfx.AdditiveBlend();
        var ctx = _gfx.Context;
        ctx.IASetInputLayout(null);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetShaderResource(0, tex);
        ctx.Draw(6, 0);
    }

    /// <summary>원본 화면(<paramref name="srcW"/>×<paramref name="srcH"/>)을 비율 지켜 창 가운데에 놓을 자리.</summary>
    public (float X, float Y, float W, float H) Fit(int srcW, int srcH)
    {
        float s = MathF.Min(_gfx.Width / (float)srcW, _gfx.Height / (float)srcH);
        float w = srcW * s, h = srcH * s;
        return ((_gfx.Width - w) / 2, (_gfx.Height - h) / 2, w, h);
    }

    public void Dispose() { _ps.Dispose(); _vs.Dispose(); }
}
