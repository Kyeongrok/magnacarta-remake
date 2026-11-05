using System.Numerics;
using System.Runtime.InteropServices;
using MagnaCarta.Data;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MagnaCarta.Render;

[StructLayout(LayoutKind.Sequential)]
internal struct CharVertex
{
    public Vector3 Pos, Normal;
    public Vector2 Uv;
}

/// <summary>
/// 캐릭터 하나(.3do 갈래 0 + 시퀀스가 짚는 .ani)를 CPU 에서 스키닝해 그린다.
/// abs[b] = local[b] · abs[parent[b]], pos = Σ w · (v · abs[bone]) — 분석-렌더링-3D §4.
/// 원본은 열쇠틀 사이를 보간하지 않는 것으로 보이나(확인 못 함), 여기서는 행렬을 선형 보간해 부드럽게 한다.
/// </summary>
internal sealed class CharacterRenderer : IDisposable
{
    public const int Stand = 100, Walk = 101, Run = 102;

    private readonly Gfx _gfx;
    private readonly CharacterMesh _mesh;
    private readonly Dictionary<int, BoneAnim> _anims = [];
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vb;
    private readonly List<(ID3D11Buffer Ib, int Count, ID3D11ShaderResourceView Tex, bool Alpha, int Mat)> _groups = [];
    // 모션 명령 갈래 3 = 재질 텍스처 바꾸기 [b0 재질, b1 TXRS] — 바뀐 채로 남는다(칼린츠 0101 seq 163 이 0051/53/54 → 2384~2386 흐린 판, seq 170 이 되돌림)
    private readonly Dictionary<int, ID3D11ShaderResourceView> _texOverride = [];
    private readonly Dictionary<int, ID3D11ShaderResourceView> _swapCache = [];
    private (int Seq, int Key) _lastSwapKey = (-1, -1);
    // 검광(분석-모션명령): 갈래 4 가 있는 모델(무기)은 검광을 갖고, 몸의 갈래 5 가 b0 0 이면 켬 · 1 이면 끔.
    // 원본 그리기(Asura ASRSwordLight)는 못 봐서 패치판은 날 밑·끝 두 점의 지난 12틀을 더하기 띠로 그린다.
    private bool _trailOn;
    // 갈래 2 = 부품 불투명도 % [b0 부품(= 부품 수면 전부), b1 1, b2 0~100] — 그리기 물체 +0xb0/+0xb8 (분석-모션명령).
    // 효과 모델 122개가 쓴다. 패치판은 열쇠틀 사이를 곧게 이어 더하기 그리기의 밝기에 곱한다(이음 방식은 추정).
    private float[] _partOpacity = [];
    private int PartCount => _mesh.BonePivots.Length;
    private readonly Dictionary<int, List<(Vector3 A, Vector3 B)>> _trails = [];
    private ID3D11Buffer? _trailVb;
    private static readonly Dictionary<(uint, uint), ID3D11ShaderResourceView> TrailTex = [];
    private bool? _hasSwordLight;
    private (Vector3 Base, Vector3 Tip)? _blade;
    private const int TrailLen = 12;
    private static readonly string? TrailLog = Environment.GetEnvironmentVariable("MC_TRAIL_LOG") is { Length: > 0 } tl ? tl : null;
    private static readonly bool NoTrail = Environment.GetEnvironmentVariable("MC_NO_TRAIL") is { Length: > 0 };

    /// <summary>이 모델(무기)에 갈래 4 명령이 하나라도 있으면 검광이 있다(읽을 때 0x5203e03c).</summary>
    public bool HasSwordLight => _hasSwordLight ??= _mesh.SwordLight != null;
    public (Vector3 P1, Vector3 P2, uint Col1, uint Col2)? SwordLight => _mesh.SwordLight;

    /// <summary>날의 두 끝(모델 공간) — 지금 자세의 정점 상자에서 가장 긴 축의 양 끝.</summary>
    private (Vector3 Base, Vector3 Tip) Blade()
    {
        if (_blade is { } b) return b;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in _skinned) { mn = Vector3.Min(mn, v.Pos); mx = Vector3.Max(mx, v.Pos); }
        var d = mx - mn; var mid = (mn + mx) / 2;
        Vector3 axis = d.X >= d.Y && d.X >= d.Z ? Vector3.UnitX : d.Y >= d.Z ? Vector3.UnitY : Vector3.UnitZ;
        float half = Vector3.Dot(d, axis) / 2;
        // 손잡이 쪽(원점에 가까운 끝)을 밑으로
        var e1 = mid - axis * half; var e2 = mid + axis * half;
        _blade = e1.LengthSquared() <= e2.LengthSquared() ? (e1, e2) : (e2, e1);
        return _blade.Value;
    }
    private readonly CharVertex[] _skinned;
    private Matrix4x4[] _bones = [];

    // 그림자 원반
    private readonly ID3D11VertexShader _shadowVs;
    private readonly ID3D11PixelShader _shadowPs;
    private readonly ID3D11InputLayout _shadowLayout;
    private readonly ID3D11Buffer _shadowVb;

    public CharacterRenderer(Gfx gfx, CharacterMesh mesh)
    {
        _gfx = gfx;
        _mesh = mesh;
        (_vs, _ps, var code) = gfx.Compile(Shaders.Source, "CharVS", "CharPS");
        _layout = gfx.Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        ], code.Span);
        _skinned = new CharVertex[mesh.Positions.Length];
        for (int i = 0; i < _skinned.Length; i++) _skinned[i].Uv = mesh.Uvs[i];
        _vb = gfx.DynamicVertexBuffer<CharVertex>(_skinned.Length);

        var cache = new Dictionary<int, ID3D11ShaderResourceView>();
        for (int gi = 0; gi < mesh.Groups.Count; gi++)
        {
            var g = mesh.Groups[gi];
            if (g.Indices.Length == 0) continue;
            int t = g.Material.Texture;
            if (!cache.TryGetValue(t, out var view))
            {
                Image32 img;
                try { img = t >= 0 ? Images.Txr(t) : Solid(g.Material.Diffuse); }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { img = Solid(g.Material.Diffuse); }
                cache[t] = view = gfx.Texture(img);
            }
            _groups.Add((gfx.Device.CreateBuffer(g.Indices, BindFlags.IndexBuffer), g.Indices.Length, view, g.Material.Flag != 0, gi));
        }
        // 투명(머리칼 등)은 뒤에 그린다
        _groups.Sort((a, b) => a.Alpha.CompareTo(b.Alpha));

        (_shadowVs, _shadowPs, var scode) = gfx.Compile(Shaders.Source, "ShadowVS", "ShadowPS");
        _shadowLayout = gfx.Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        ], scode.Span);
        static ShadowVertex S(float x, float y) => new() { Pos = new(x, y, 0), Uv = new((x + 1) / 2, (y + 1) / 2) };
        _shadowVb = gfx.VertexBuffer(new[] { S(-1, -1), S(1, -1), S(-1, 1), S(-1, 1), S(1, -1), S(1, 1) });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowVertex { public Vector3 Pos; public Vector2 Uv; }

    private static Image32 Solid(Vector4 c)
    {
        var img = new Image32(2, 2);
        img.Fill(0, 0, 2, 2, (byte)(Math.Clamp(c.X, 0, 1) * 255), (byte)(Math.Clamp(c.Y, 0, 1) * 255), (byte)(Math.Clamp(c.Z, 0, 1) * 255));
        return img;
    }

    private BoneAnim Anim(int number)
    {
        if (!_anims.TryGetValue(number, out var a)) _anims[number] = a = BoneAnim.Load(number);
        return a;
    }

    public bool HasSequence(int id) => _mesh.FindSequence(id) != null;

    /// <summary>시퀀스 <paramref name="id"/> 가 (from, to] 틱 사이에 지나는 열쇠틀의 모션 명령 — 되풀이 시퀀스는 주기를 넘어도 센다.</summary>
    public IEnumerable<(int Key, int Type, int B0, int B2, int B1)> CommandsBetween(int id, float from, float to)
    {
        var seq = _mesh.FindSequence(id);
        if (seq == null || seq.Commands.Length == 0 || to <= from) yield break;
        int nk = seq.Keys.Length;
        int k0 = (int)MathF.Floor(from / seq.Step), k1 = (int)MathF.Floor(to / seq.Step);
        if (from <= 0) k0 = -1;
        for (int k = k0 + 1; k <= k1; k++)
        {
            int key = seq.Hold != 0 ? k : ((k % nk) + nk) % nk;
            if (seq.Hold != 0 && k >= nk) break;
            foreach (var c in seq.Commands) if (c.Key == key) yield return c;
        }
    }

    /// <summary>시퀀스 한 바퀴의 틱 수(없으면 0).</summary>
    public int SequenceTicks(int id) => _mesh.FindSequence(id) is { } s ? s.Keys.Length * s.Step : 0;

    /// <summary>뼈 0(엉덩이) 높이로 잰 대략의 키 — 머리 위 표시 자리용.</summary>
    public float Height => _mesh.Positions.Length == 0 ? 50 : 50;

    /// <summary>시퀀스 <paramref name="id"/> 의 <paramref name="tick"/> 틱 자세로 정점을 편다.</summary>
    // 모션 명령 갈래 0 = 뼈에 3D 물체 붙이기 [b0 3do, b2 뼈] (분석-모션명령) — 칼린츠 검 0023 은 전투 몸 뼈 42(오른손)
    private readonly List<(int Model, int Bone)> _attach = [];
    /// <summary>붙일 물체 바꾸기(처음 신기 모델 → 진화한 신기 모델).</summary>
    public readonly Dictionary<int, int> AttachSwap = [];
    private static readonly Dictionary<int, CharacterRenderer?> AttachCache = [];

    public void Pose(int id, float tick)
    {
        var seq = _mesh.FindSequence(id) ?? _mesh.Sequences.First(s => s.Keys.Length > 0);
        _attach.Clear();
        int curKey = (int)MathF.Floor(tick / Math.Max(seq.Step, 1));
        if (seq.Keys.Length > 0 && seq.Hold == 0) curKey %= seq.Keys.Length;
        foreach (var c in seq.Commands)
            if (c.Type == 0 && (c.Key == -1 || c.Key == curKey) && c.B0 > 0) _attach.Add((AttachSwap.GetValueOrDefault(c.B0, c.B0), c.B2));
        // 갈래 5: 지금 열쇠틀까지 가장 마지막 것이 b0 0 이면 켬(시퀀스가 바뀌면 다시 셈 — 끄는 곳을 못 찾아 패치판 지은 규칙)
        _trailOn = false;
        foreach (var c in seq.Commands)
            if (c.Type == 5 && (c.Key == -1 || c.Key <= curKey)) _trailOn = c.B0 == 0;
        ComputeOpacity(seq, tick / Math.Max(seq.Step, 1));
        if (_lastSwapKey != (id, curKey))
        {
            _lastSwapKey = (id, curKey);
            foreach (var c in seq.Commands)
                if (c.Type == 3 && (c.Key == -1 || c.Key == curKey) && c.B1 > 0 && SwapTexture(c.B1) is { } sv) _texOverride[c.B0] = sv;
        }
        int nk = seq.Keys.Length;
        float pos = tick / seq.Step;
        int k0 = (int)MathF.Floor(pos);
        float frac = pos - k0;
        int k1;
        if (seq.Hold != 0) { k0 = Math.Min(k0, nk - 1); k1 = Math.Min(k0 + 1, nk - 1); }
        else { k0 = ((k0 % nk) + nk) % nk; k1 = (k0 + 1) % nk; }
        var (ani0, f0) = seq.Keys[k0];
        var (ani1, f1) = seq.Keys[k1];
        var a0 = Anim(ani0);
        var a1 = ani1 == ani0 ? a0 : Anim(ani1);
        if (a1.Bones != a0.Bones) frac = 0;
        f0 = Math.Clamp(f0, 0, a0.Frames);
        f1 = Math.Clamp(f1, 0, a1.Frames);

        if (_bones.Length != a0.Bones) _bones = new Matrix4x4[a0.Bones];
        foreach (int b in a0.Order)
        {
            var local = frac > 0 ? Matrix4x4.Lerp(a0.Local[b, f0], a1.Local[b, f1], frac) : a0.Local[b, f0];
            _bones[b] = a0.Parent[b] >= 0 ? local * _bones[a0.Parent[b]] : local;
        }

        var p = _mesh.Positions;
        var n = _mesh.Normals;
        int nb = _bones.Length;
        for (int i = 0; i < p.Length; i++)
        {
            int b0 = Math.Min(_mesh.Bone0[i], nb - 1);
            float w0 = _mesh.Weight0[i];
            Vector3 v = Vector3.Transform(p[i], _bones[b0]), nn = Vector3.TransformNormal(n[i], _bones[b0]);
            int b1 = _mesh.Bone1[i];
            if (b1 != 0xFFFF && b1 < nb && _mesh.Weight1[i] > 0)
            {
                float w1 = _mesh.Weight1[i];
                v = v * w0 + Vector3.Transform(p[i], _bones[b1]) * w1;
                nn = nn * w0 + Vector3.TransformNormal(n[i], _bones[b1]) * w1;
            }
            _skinned[i].Pos = v;
            _skinned[i].Normal = nn;
        }
        _gfx.Upload<CharVertex>(_vb, _skinned);
    }

    /// <summary>효과 그리기: 그림자 없이, 모든 면을 더하기 섞기로.</summary>
    private void ComputeOpacity(Sequence seq, float keyPos)
    {
        int np = Math.Max(PartCount, 1);
        if (_partOpacity.Length != np) _partOpacity = new float[np];
        Array.Fill(_partOpacity, 1f);
        if (!seq.Commands.Any(c => c.Type == 2)) return;
        if (TrailLog != null) File.AppendAllText(TrailLog, $"opacity {_mesh.Number} seq {seq.Id} key {keyPos:F1}" + Environment.NewLine);
        if (seq.Hold == 0 && seq.Keys.Length > 0) keyPos %= seq.Keys.Length;
        for (int part = 0; part < np; part++)
        {
            (float K, float V)? prev = null, next = null;
            foreach (var c in seq.Commands)
            {
                if (c.Type != 2 || !(c.B0 == part || c.B0 >= np)) continue;
                float k = Math.Max(c.Key, 0), v = Math.Clamp(c.B2 / 100f, 0, 1);
                if (k <= keyPos) { if (prev == null || k >= prev.Value.K) prev = (k, v); }
                else if (next == null || k < next.Value.K) next = (k, v);
            }
            if (prev is { } a && next is { } b && b.K > a.K) _partOpacity[part] = a.V + (b.V - a.V) * (keyPos - a.K) / (b.K - a.K);
            else if (prev is { } a2) _partOpacity[part] = a2.V;
            else if (next is { } b2) _partOpacity[part] = b2.V;
        }
    }

    public void DrawAdditive(Constants c, Matrix4x4 world)
    {
        var ctx = _gfx.Context;
        c.World = world;
        c.Ambient = Vector4.One; c.LightColor = Vector4.Zero;
        _gfx.Set(c);
        _gfx.AdditiveBlend();
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<CharVertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        var baseTint = c.Tint;
        foreach (var (ib, count, tex0, _, mat) in _groups)
        {
            int part = mat < _mesh.Groups.Count ? _mesh.Groups[mat].Part : 0;
            float o = NoOpacity || part >= _partOpacity.Length ? 1 : _partOpacity[part];
            if (o <= 0.003f) continue;
            c.Tint = new Vector4(baseTint.X * o, baseTint.Y * o, baseTint.Z * o, baseTint.W);
            _gfx.Set(c);
            ctx.IASetIndexBuffer(ib, Format.R16_UInt, 0);
            ctx.PSSetShaderResource(0, _texOverride.GetValueOrDefault(mat, tex0));
            ctx.DrawIndexed((uint)count, 0, 0);
        }
    }

    private static readonly bool NoOpacity = Environment.GetEnvironmentVariable("MC_NO_OPACITY") is { Length: > 0 };

    private void DrawAttachments(Constants c, Matrix4x4 world)
    {
        foreach (var (model, bone) in _attach)
        {
            if (bone < 0 || bone >= _bones.Length) continue;
            if (!AttachCache.TryGetValue(model, out var r))
            {
                r = null;
                try { if (McFiles.Exists("3DOBS", model, "3do")) { var am = CharacterMesh.Load(model); if (am.Positions.Length > 0) r = new CharacterRenderer(_gfx, am); } }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException) { }
                AttachCache[model] = r;
            }
            if (r == null || r == this) continue;
            r.Pose(0, 0);
            var m = _bones[bone] * world;
            r.DrawBody(c, m);
            if (!r.HasSwordLight || NoTrail) continue;
            if (!_trails.TryGetValue(model, out var tr)) _trails[model] = tr = [];
            if (!_trailOn) { tr.Clear(); continue; }
            var sl = r.SwordLight!.Value;
            // 원본 자료의 두 점을 붙인 모델(무기)의 자세로 옮긴다
            var rb = r._bones.Length > 0 ? r._bones[0] : Matrix4x4.Identity;
            tr.Add((Vector3.Transform(Vector3.Transform(sl.P2, rb), m), Vector3.Transform(Vector3.Transform(sl.P1, rb), m)));
            if (tr.Count > TrailLen) tr.RemoveAt(0);
            DrawTrail(c, tr, sl.Col1, sl.Col2);
        }
    }

    private void DrawTrail(Constants c, List<(Vector3 A, Vector3 B)> tr, uint col1, uint col2)
    {
        if (tr.Count < 2) return;
        if (TrailLog != null) File.AppendAllText(TrailLog, $"trail {_mesh.Number} n={tr.Count} a={tr[^1].A:F0} b={tr[^1].B:F0}" + Environment.NewLine);
        _trailVb ??= _gfx.DynamicVertexBuffer<CharVertex>(6 * TrailLen);
        if (!TrailTex.TryGetValue((col1, col2), out var trailTex))
        {
            // 가로 = 시간(왼쪽 오래됨 → 오른쪽 지금), 세로 = 날 밑(색 2) → 끝(색 1) — 원본 두 색(분석-모션명령 갈래 4)
            static Vector3 Rgb(uint x) => new(((x >> 16) & 255) / 255f, ((x >> 8) & 255) / 255f, (x & 255) / 255f);
            Vector3 cTip = Rgb(col1), cBase = Rgb(col2);
            if (cTip.LengthSquared() < 0.01f && cBase.LengthSquared() < 0.01f) cTip = cBase = Vector3.One;
            var img = new Image32(32, 8);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 32; x++)
                {
                    float t = x / 31f, w = y / 7f;
                    var col = Vector3.Lerp(cBase, cTip, w) * (t * t * (0.35f + 0.65f * w));
                    int i = (y * 32 + x) * 4;
                    img.Bgra[i] = (byte)(255 * Math.Clamp(col.Z, 0, 1)); img.Bgra[i + 1] = (byte)(255 * Math.Clamp(col.Y, 0, 1)); img.Bgra[i + 2] = (byte)(255 * Math.Clamp(col.X, 0, 1)); img.Bgra[i + 3] = 255;
                }
            TrailTex[(col1, col2)] = trailTex = _gfx.Texture(img);
        }
        var v = new CharVertex[6 * (tr.Count - 1)];
        int n = 0;
        for (int i = 0; i + 1 < tr.Count; i++)
        {
            float u0 = i / (float)(tr.Count - 1), u1 = (i + 1) / (float)(tr.Count - 1);
            var a0 = tr[i].A; var b0 = tr[i].B; var a1 = tr[i + 1].A; var b1 = tr[i + 1].B;
            CharVertex V(Vector3 p, float u, float w) => new() { Pos = p, Normal = Vector3.UnitZ, Uv = new Vector2(u, w) };
            v[n++] = V(a0, u0, 0); v[n++] = V(b0, u0, 1); v[n++] = V(b1, u1, 1);
            v[n++] = V(a0, u0, 0); v[n++] = V(b1, u1, 1); v[n++] = V(a1, u1, 0);
        }
        _gfx.Upload<CharVertex>(_trailVb, v);
        var ctx = _gfx.Context;
        c.World = Matrix4x4.Identity;
        c.Tint = new Vector4(2.5f, 2.5f, 2.5f, 1);
        c.LightDir.W = 1;
        _gfx.Set(c);
        _gfx.AdditiveBlend();
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _trailVb, (uint)Marshal.SizeOf<CharVertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetShaderResource(0, trailTex);
        ctx.Draw((uint)n, 0);
    }

    public void Draw(Constants c, Matrix4x4 world)
    {
        DrawShadowAndBody(c, world);
        DrawAttachments(c, world);
    }

    private void DrawShadowAndBody(Constants c, Matrix4x4 world)
    {
        var ctx = _gfx.Context;
        // 발밑 그림자
        c.World = Matrix4x4.CreateScale(14, 14, 1) * Matrix4x4.CreateTranslation(world.Translation + new Vector3(0, 0, 0.6f));
        _gfx.Set(c);
        _gfx.Translucent();
        ctx.IASetInputLayout(_shadowLayout);
        ctx.IASetVertexBuffer(0, _shadowVb, (uint)Marshal.SizeOf<ShadowVertex>());
        ctx.VSSetShader(_shadowVs);
        ctx.PSSetShader(_shadowPs);
        ctx.Draw(6, 0);

        DrawBody(c, world);
    }

    public void DrawBody(Constants c, Matrix4x4 world)
    {
        var ctx = _gfx.Context;
        c.World = world;
        _gfx.Set(c);
        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<CharVertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        foreach (var (ib, count, tex0, alpha, mat) in _groups)
        {
            if (alpha) _gfx.Translucent(); else _gfx.Opaque();
            // 재질 갈래 0 은 텍스처 알파를 안 쓴다 — 로제 0823 은 84% 가 알파 0 이라 몸이 사라졌었다
            c.LightDir.W = alpha ? 0 : 1;
            _gfx.Set(c);
            ctx.IASetIndexBuffer(ib, Format.R16_UInt, 0);
            ctx.PSSetShaderResource(0, _texOverride.GetValueOrDefault(mat, tex0));
            ctx.DrawIndexed((uint)count, 0, 0);
        }
    }

    private ID3D11ShaderResourceView? SwapTexture(int txr)
    {
        if (_swapCache.TryGetValue(txr, out var v)) return v;
        try { v = McFiles.Exists("TXRS", txr, "txr") ? _gfx.Texture(Images.Txr(txr)) : null; }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { v = null; }
        if (v != null) _swapCache[txr] = v;
        return v;
    }

    public void Dispose()
    {
        foreach (var t in _swapCache.Values) t.Dispose();
        _trailVb?.Dispose();
        foreach (var t in _groups.Select(g => g.Tex).Distinct()) t.Dispose();
        foreach (var g in _groups) g.Ib.Dispose();
        _shadowVb.Dispose(); _shadowLayout.Dispose(); _shadowPs.Dispose(); _shadowVs.Dispose();
        _vb.Dispose(); _layout.Dispose(); _ps.Dispose(); _vs.Dispose();
    }
}
