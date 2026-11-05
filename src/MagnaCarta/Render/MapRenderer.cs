using System.Numerics;
using System.Runtime.InteropServices;
using MagnaCarta.Data;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MagnaCarta.Render;

[StructLayout(LayoutKind.Sequential)]
internal struct MapVertex(Vector3 pos, Vector2 uv, Vector2 lm)
{
    public Vector3 Pos = pos;
    public Vector2 Uv = uv;
    public Vector2 Lm = lm;
}

/// <summary>wrd 맵 하나를 (텍스처, 라이트맵 장) 묶음별로 그린다. 바닥 높이 묻기도 여기서.</summary>
internal sealed class MapRenderer : IDisposable
{
    private readonly Gfx _gfx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vb;
    private readonly List<(int Texture, int Page, int Start, int Count)> _batches = [];
    private readonly Dictionary<int, ID3D11ShaderResourceView> _textures = [];
    private readonly List<ID3D11ShaderResourceView> _lightmaps = [];
    private readonly ID3D11ShaderResourceView _white;

    public readonly Ground Ground;

    public MapRenderer(Gfx gfx, WorldMap map)
    {
        _gfx = gfx;
        (_vs, _ps, var code) = gfx.Compile(Shaders.Source, "MapVS", "MapPS");
        _layout = gfx.Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 20, 0),
        ], code.Span);

        var white = new Image32(2, 2);
        white.Fill(0, 0, 2, 2, 255, 255, 255);
        _white = gfx.Texture(white, false);

        foreach (var (_, size, rgb) in map.Lightmaps)
        {
            var img = new Image32(size, size);
            for (int i = 0; i < size * size; i++)
            {
                img.Bgra[i * 4] = rgb[i * 3 + 2]; img.Bgra[i * 4 + 1] = rgb[i * 3 + 1]; img.Bgra[i * 4 + 2] = rgb[i * 3]; img.Bgra[i * 4 + 3] = 255;
            }
            _lightmaps.Add(gfx.Texture(img, false));
        }

        var verts = new List<MapVertex>(map.Triangles.Count * 3);
        foreach (var g in map.Triangles.GroupBy(t => (t.Texture, t.LightmapPage)).OrderBy(g => g.Key.Texture))
        {
            int start = verts.Count;
            foreach (var t in g)
            {
                verts.Add(new MapVertex(t.A, t.UvA, t.LmA));
                verts.Add(new MapVertex(t.B, t.UvB, t.LmB));
                verts.Add(new MapVertex(t.C, t.UvC, t.LmC));
            }
            _batches.Add((g.Key.Texture, g.Key.LightmapPage, start, verts.Count - start));
            if (g.Key.Texture >= 0 && !_textures.ContainsKey(g.Key.Texture))
            {
                try { _textures[g.Key.Texture] = gfx.Texture(Images.Txr(g.Key.Texture)); }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { _textures[g.Key.Texture] = _white; }
            }
        }
        if (verts.Count == 0) verts.Add(default);   // 빈 맵 — 버퍼는 0 바이트로 못 만든다
        _vb = gfx.VertexBuffer(verts.ToArray());
        Ground = new Ground(map.Triangles);
        Ground.AddSolid(map.Triangles);
    }

    public void Draw(Constants c)
    {
        var ctx = _gfx.Context;
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<MapVertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        _gfx.Opaque();
        _gfx.MapCull();
        foreach (var (tex, page, start, count) in _batches)
        {
            c.Fog.Z = page >= 0 ? 1 : 0;
            _gfx.Set(c);
            ctx.PSSetShaderResource(0, tex >= 0 ? _textures[tex] : _white);
            ctx.PSSetShaderResource(1, page >= 0 ? _lightmaps[page] : _white);
            ctx.Draw((uint)count, (uint)start);
        }
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values.Distinct()) if (t != _white) t.Dispose();
        foreach (var l in _lightmaps) l.Dispose();
        _white.Dispose(); _vb.Dispose(); _layout.Dispose(); _ps.Dispose(); _vs.Dispose();
    }
}

/// <summary>
/// 바닥 높이 묻기. 원본은 맵 다각형(BSP, MCBSPTree.cpp)으로 판단하는 것으로 보이나 확인 못 했으므로,
/// 위를 보는 삼각형을 2D 격자에 넣어 두고 (x, y) 아래의 가장 높은 면을 찾는다.
/// </summary>
internal sealed class Ground
{
    private const float Cell = 64f;
    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _tris = [];
    private readonly Dictionary<(int, int), List<int>> _grid = [];

    public Ground(IEnumerable<MapTriangle> triangles)
    {
        foreach (var t in triangles)
        {
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            float len = n.Length();
            if (len < 1e-6f || n.Z / len < 0.45f) continue;   // 위를 보는 면만 바닥(감김으로 판단 — 천장·벽은 뺀다)
            // 큰 삼각형도 바닥일 수 있다(성지 용암 무대 등) — 하늘은 아래를 봐서 위의 법선 검사로 빠진다
            int idx = _tris.Count;
            _tris.Add((t.A, t.B, t.C));
            int x0 = (int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) / Cell);
            int y0 = (int)MathF.Floor(MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)) / Cell), y1 = (int)MathF.Floor(MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) / Cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!_grid.TryGetValue((x, y), out var list)) _grid[(x, y)] = list = [];
                    list.Add(idx);
                }
        }
    }

    /// <summary>모든 면(벽 포함) — 카메라가 나무·벽 뒤로 들어가지 않게 막을 때 쓴다.</summary>
    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _solid = [];
    private readonly Dictionary<(int, int), List<int>> _solidGrid = [];

    public void AddSolid(IEnumerable<MapTriangle> triangles)
    {
        foreach (var t in triangles)
        {
            if (Vector3.Distance(t.A, t.B) > 3000) continue;   // 하늘 · 먼 산은 막지 않는다
            int idx = _solid.Count;
            _solid.Add((t.A, t.B, t.C));
            int x0 = (int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X)) / Cell);
            int y0 = (int)MathF.Floor(MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)) / Cell), y1 = (int)MathF.Floor(MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y)) / Cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!_solidGrid.TryGetValue((x, y), out var list)) _solidGrid[(x, y)] = list = [];
                    list.Add(idx);
                }
        }
    }

    /// <summary><paramref name="from"/> 에서 <paramref name="to"/> 로 가는 선분이 처음 면에 닿는 비율(0~1). 안 닿으면 1.</summary>
    public float Raycast(Vector3 from, Vector3 to)
    {
        var d = to - from;
        float best = 1;
        var seen = new HashSet<int>();
        int steps = (int)MathF.Ceiling(new Vector2(d.X, d.Y).Length() / (Cell * 0.5f)) + 1;
        for (int s = 0; s <= steps; s++)
        {
            var p = from + d * (s / (float)steps);
            int cx = (int)MathF.Floor(p.X / Cell), cy = (int)MathF.Floor(p.Y / Cell);
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                {
                    if (!_solidGrid.TryGetValue((cx + ox, cy + oy), out var list)) continue;
                    foreach (int i in list)
                    {
                        if (!seen.Add(i)) continue;
                        var (a, b, c) = _solid[i];
                        // Möller–Trumbore
                        var e1 = b - a; var e2 = c - a;
                        var pv = Vector3.Cross(d, e2);
                        float det = Vector3.Dot(e1, pv);
                        if (MathF.Abs(det) < 1e-8f) continue;
                        float inv = 1 / det;
                        var tv = from - a;
                        float u = Vector3.Dot(tv, pv) * inv;
                        if (u < 0 || u > 1) continue;
                        var qv = Vector3.Cross(tv, e1);
                        float v = Vector3.Dot(d, qv) * inv;
                        if (v < 0 || u + v > 1) continue;
                        float t = Vector3.Dot(e2, qv) * inv;
                        if (t > 0 && t < best) best = t;
                    }
                }
        }
        return best;
    }

    /// <summary>감김과 상관없이(아래를 보는 면도) 평평한 면에서 찾는다 — 감김이 거꾸로인 무대 바닥(성지 용암 등)용.</summary>
    public float? HeightAtAny(float x, float y, float below)
    {
        if (!_solidGrid.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell)), out var list)) return null;
        float? best = null;
        foreach (int i in list)
        {
            var (a, b, c) = _solid[i];
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            if (len < 1e-6f || MathF.Abs(n.Z) / len < 0.45f) continue;
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f) continue;
            float l0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float l1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float l2 = 1 - l0 - l1;
            if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
            float z = l0 * a.Z + l1 * b.Z + l2 * c.Z;
            if (z <= below && (best is null || z > best)) best = z;
        }
        return best;
    }

    /// <summary>(x, y) 에서 <paramref name="below"/> 보다 낮거나 같은 가장 높은 바닥. 없으면 null.</summary>
    public float? HeightAt(float x, float y, float below)
    {
        if (!_grid.TryGetValue(((int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell)), out var list)) return null;
        float? best = null;
        foreach (int i in list)
        {
            var (a, b, c) = _tris[i];
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f) continue;
            float l0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float l1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float l2 = 1 - l0 - l1;
            if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
            float z = l0 * a.Z + l1 * b.Z + l2 * c.Z;
            if (z <= below && (best is null || z > best)) best = z;
        }
        return best;
    }
}
