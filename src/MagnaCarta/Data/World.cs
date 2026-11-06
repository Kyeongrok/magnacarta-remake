using System.Numerics;

namespace MagnaCarta.Data;

/// <summary>맵 삼각형 하나 — 월드 좌표(Z 위), 텍스처 uv, 라이트맵 uv, TXRS 번호, 라이트맵 장 차례.</summary>
internal struct MapTriangle
{
    public Vector3 A, B, C;
    public Vector2 UvA, UvB, UvC;
    public Vector2 LmA, LmB, LmC;
    public int Texture;
    public int LightmapPage;   // −1 없음
    public int Flag;
}

/// <summary>
/// MAP\%04d.wrd. 그룹 → 부분 → 면(재질 + 텍스처 + 삼각형), 꼭짓점 FVF 0x202(XYZ + uv 두 벌).
/// 배치: 옵시디안 분석-스테이지 §2. 로더 MCAlpha 0x5202a723, 라이트맵 0x520190ff.
/// </summary>
internal sealed class WorldMap
{
    public int Number;
    public readonly List<(int Id, int Size, byte[] Rgb)> Lightmaps = [];
    public readonly List<MapTriangle> Triangles = [];

    public static WorldMap Load(int number)
    {
        var d = McFiles.Read("MAP", number, "wrd");
        var r = new Reader(d, 0x100);
        var w = new WorldMap { Number = number };
        r.U32();                                          // 삼각형 수 합
        int nlm = r.Count();
        for (int i = 0; i < nlm; i++)
        {
            int id = r.I32(), size = r.I32(), cnt = r.Count(28);
            r.Skip(28 * cnt);                             // 삼각형마다 라이트맵 조각(꼭짓점 uv1 과 같은 값)
            var rgb = d.AsSpan(r.Pos, size * size * 3).ToArray();
            r.Skip(size * size * 3);
            w.Lightmaps.Add((id, size, rgb));
        }
        r.Skip(0x100);                                    // 둘째 머리(쓰레기)
        int ngroup = r.Count();
        for (int g = 0; g < ngroup; g++)
        {
            int ns = r.Count(12);
            r.Skip(12 * ns);                              // 부분 기준점
            var subs = new List<(Vector3[] P, Vector2[] Uv0, Vector2[] Uv1, int LmBase, List<(int Tex, int Flag, int[] Pages, int[] Counts, ushort[] Idx)> Faces)>();
            for (int s = 0; s < ns; s++)
            {
                int nv = r.I32(); uint fvf = r.U32(); int stride = r.I32(); r.U32(); int lmBase = r.I32(); int nf = r.I32();
                var p = new Vector3[nv]; var uv0 = new Vector2[nv]; var uv1 = new Vector2[nv];
                int ntex = (int)(fvf >> 8 & 0xF);
                int uvOff = 12 + ((fvf & 0x10) != 0 ? 12 : 0) + ((fvf & 0x40) != 0 ? 4 : 0) + ((fvf & 0x80) != 0 ? 4 : 0);
                for (int i = 0; i < nv; i++)
                {
                    var vr = new Reader(d, r.Pos + i * stride);
                    p[i] = vr.Vec3();
                    vr.Pos = r.Pos + i * stride + uvOff;
                    if (ntex >= 1) uv0[i] = new Vector2(vr.F32(), vr.F32());
                    if (ntex >= 2) uv1[i] = new Vector2(vr.F32(), vr.F32());
                }
                r.Skip(nv * stride);
                var faces = new List<(int, int, int[], int[], ushort[])>();
                for (int k = 0; k < nf; k++)
                {
                    r.Skip(17 * 4);                       // D3DMATERIAL8
                    int tex = r.I32(), flag = r.I32(), ntri = r.I32();
                    int[] pages = [], counts = [];
                    ushort[] idx = [];
                    if (ntri > 0)
                    {
                        r.U32();
                        pages = Enumerable.Range(0, w.Lightmaps.Count).Where(i => w.Lightmaps[i].Id == lmBase + k).ToArray();
                        counts = new int[pages.Length];
                        for (int i = 0; i < pages.Length; i++) counts[i] = r.I32();
                        idx = new ushort[ntri * 3];
                        for (int i = 0; i < idx.Length; i++) idx[i] = r.U16();
                    }
                    faces.Add((tex, flag, pages, counts, idx));
                }
                subs.Add((p, uv0, uv1, lmBase, faces));
            }
            // 변환 키프레임 — 첫 트랙의 첫 행렬로 꼭짓점을 월드로 옮긴다(움직이는 물체는 지금은 멈춘 채로)
            r.U32(); r.U32();
            int keys = r.I32(), ntrack = r.I32();
            Matrix4x4? xf = null;
            for (int t = 0; t < ntrack; t++)
            {
                r.U32();
                int c = r.Count(4); r.Skip(4 * c);
                for (int k = 0; k <= keys; k++)
                {
                    var m = r.Mat();
                    if (t == 0 && k == 0) xf = m;
                }
            }
            foreach (var (P, Uv0, Uv1, _, Faces) in subs)
                foreach (var (tex, flag, pages, counts, idx) in Faces)
                {
                    int ntri = idx.Length / 3;
                    for (int t = 0, pi = 0, acc = counts.Length > 0 ? counts[0] : int.MaxValue; t < ntri; t++)
                    {
                        while (t >= acc && pi + 1 < counts.Length) acc += counts[++pi];
                        int a = idx[3 * t], b = idx[3 * t + 1], c = idx[3 * t + 2];
                        if (a >= P.Length || b >= P.Length || c >= P.Length) continue;
                        Vector3 X(Vector3 v) => xf is { } m ? Vector3.Transform(v, m) : v;
                        w.Triangles.Add(new MapTriangle
                        {
                            A = X(P[a]), B = X(P[b]), C = X(P[c]),
                            UvA = Uv0[a], UvB = Uv0[b], UvC = Uv0[c],
                            LmA = Uv1[a], LmB = Uv1[b], LmC = Uv1[c],
                            Texture = tex, Flag = flag,
                            LightmapPage = pages.Length > 0 && t < acc ? pages[pi] : -1,
                        });
                    }
                }
        }
        return w;
    }
}

/// <summary>Field\%04d.fld 의 앞부분 — 맵 번호, 주변광, 인물 배치(B). 배치: 옵시디안 분석-스테이지 §3.</summary>
internal sealed class Field
{
    public record Actor(int Character, int Id, Vector3 Position, Vector3 Rotation);

    public int Number, MapNumber, Music, FpbNumber = -1;
    public string PlaceName = "";
    /// <summary>fld 0xf8(객체 +0x10c, 장면 +0x11d8) — 0 이 아니면 들어올 때 AUTO 칸에 저장하지 않는다(0x5206a954). 1 은 651·655~666 등 사건용 필드 15개.</summary>
    public bool NoAutoSave;
    /// <summary>fld 0xf0 안개 단계(−1 없음, 0~39) · 0xec 안개 색 WinDiData 번호 — 필드 들기 0x5206a6ee: 밀도 0.00025 × 단계, 색 = 그 wdi 의 첫 색.</summary>
    public int FogLevel = -1, FogColorWdi;
    /// <summary>fld 0xe8 — 음악 1번 채널 SMD(필드 들기 0x52069306: [0x520f2318] 0x52050f03(1, 번호), −1 이면 1번 채널 끔). 290 필드쯤(82·72·70 …) — 배경음 위에 겹치는 두 번째 곡.</summary>
    public int Music2 = -1;
    /// <summary>fld 0x40c f32×4 — 화면 바탕(지우기) 색 RGBA, 다음 두 값은 f32 10 · 10000(가까운·먼 자름면으로 보임), 그다음 i32 = 빛 수(분석-던전). 0x5201d66f 로 그리개에.</summary>
    public System.Numerics.Vector4 ClearColor = new(0.05f, 0.06f, 0.09f, 1);
    /// <summary>점 빛 — fld 0x424 수, 0x428 부터 0x4c 바이트씩: [0..3] 색 RGBA · [4..7] (0,0,0,1) · [8] 안쪽 반경 · [9] 바깥 반경 · [10] 켜짐(1 이면 들 때 켬, 0x52026e13) · [11] id(사건 513/514 가 씀) · [15..17] 자리. 549 는 벽 횃불 11개(z 42).</summary>
    public readonly List<FieldLight> Lights = [];
    /// <summary>조우 단계(0~4, 5 없음), 전투 무대 MAP, Mon 무리 5칸, 전투 필드 5칸 — 분석-던전 §1.</summary>
    public int EncounterStep = 5, Stage = -1;
    /// <summary>발소리 바닥(0~8, −1 은 0) — 파일 0x90 → 객체 +0xa8 → dealer +0x16e4 (분석-모션명령).</summary>
    public int Floor;
    public readonly int[] Groups = new int[5], BattleFields = new int[5];
    public Vector4 Ambient;
    public float Near, Far;
    public readonly List<Actor> Actors = [];
    public readonly Dictionary<int, int> ActorSides = [];

    public static Field Load(int number)
    {
        var r = new Reader(McFiles.Read("Field", number, "fld"));
        var f = new Field { Number = number };
        f.MapNumber = r.I32();
        f.Music = r.I32();
        f.FpbNumber = r.I32();
        r.Pos = 0x10; f.EncounterStep = r.I32(); f.Stage = r.I32();
        r.Pos = 0x90; f.Floor = Math.Clamp(r.I32(), 0, 8);
        r.Pos = 0xF8; f.NoAutoSave = r.I32() != 0;
        r.Pos = 0xE8; f.Music2 = r.I32();
        if (r.Data.Length >= 0x428)
        {
            int ln = BitConverter.ToInt32(r.Data, 0x424);
            for (int k = 0; k < ln && k < 64 && 0x428 + 0x4c * (k + 1) <= r.Data.Length; k++)
            {
                int o = 0x428 + 0x4c * k; var b = r.Data;
                f.Lights.Add(new FieldLight(BitConverter.ToInt32(b, o + 44), new System.Numerics.Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8)),
                    BitConverter.ToSingle(b, o + 32), BitConverter.ToSingle(b, o + 36), new System.Numerics.Vector3(BitConverter.ToSingle(b, o + 60), BitConverter.ToSingle(b, o + 64), BitConverter.ToSingle(b, o + 68)), BitConverter.ToInt32(b, o + 40) == 1));
            }
        }
        if (r.Data.Length >= 0x41C) { var b = r.Data; f.ClearColor = new System.Numerics.Vector4(BitConverter.ToSingle(b, 0x40C), BitConverter.ToSingle(b, 0x410), BitConverter.ToSingle(b, 0x414), 1); }
        r.Pos = 0xEC; f.FogColorWdi = r.I32(); f.FogLevel = r.I32(); if (f.FogLevel is < 0 or >= 40) f.FogLevel = -1;
        r.Pos = 0xC0; for (int k = 0; k < 5; k++) f.Groups[k] = r.I32();
        for (int k = 0; k < 5; k++) f.BattleFields[k] = r.I32();
        var name = r.Data.AsSpan(0xFC, 0x12);
        int nul = name.IndexOf((byte)0);
        f.PlaceName = McFiles.Cp949.GetString(name[..(nul < 0 ? 0x12 : nul)]).Trim();   // 장소 이름(분석-도시)
        r.Pos = 0x40C;
        f.Ambient = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
        f.Near = r.F32(); f.Far = r.F32();
        int na = r.Count(0x4C); r.Skip(na * 0x4C);       // A 조명
        int nb = r.Count(0x34);
        for (int i = 0; i < nb; i++)
        {
            int ch = r.I32(), id = r.I32();
            var p = r.Vec3(); var q = r.Vec3();
            r.Skip(0x14);
            f.Actors.Add(new Actor(ch, id, p, q));
            f.ActorSides[id] = BitConverter.ToInt16(r.Data, r.Pos - 0x14);   // 꼬리 첫 word: 사건 전투에서 2 = 아군, 0 = 적
        }
        // C 물체: 파일 첫 값 = 모델, 둘째 = id, 셋째 = 갈래(0 자리 표지 · 3 마을 사람 · 6/7 상자) — 분석-도시
        int nc = r.Count(0x38);
        for (int i = 0; i < nc; i++)
        {
            int model = r.I32(), id = r.I32(), kind = r.I32();
            var p = r.Vec3(); var q = r.Vec3();
            var extra = r.Data.AsSpan(r.Pos, 0x14).ToArray();
            r.Skip(0x14);
            f.Objects.Add(new FieldObject(id, model, kind, p, q, extra));
        }
        // D 카메라(CCTV, MCObserver::AddCCTV 0x5205197f): {i32 id, f32[3] 처음 자리, 8×f32[3] 움직일 상자, i32 a(×10 = 지킬 거리), b(느림 갈래), c} — 분석-필드카메라
        int nd = r.Count(0x7C);
        for (int i = 0; i < nd; i++)
        {
            int id = r.I32();
            var p0 = r.Vec3();
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            var corners = new Vector3[8];
            for (int k = 0; k < 8; k++) { var v = r.Vec3(); corners[k] = v; lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            int a = r.I32(), b = r.I32(), c = r.I32();
            // 민 상자: 꼭짓점마다 c + normalize(c − 처음 자리) × 줄(분석-필드카메라) — 고르기 판정에 쓴다(면 6장 대신 감싸는 상자, 근사)
            Vector3 plo = new(float.MaxValue), phi = new(float.MinValue);
            foreach (var cn in corners)
            {
                var dir = cn - p0;
                var pushed = dir.LengthSquared() > 1e-6f ? cn + Vector3.Normalize(dir) * (a * 10f) : cn;
                plo = Vector3.Min(plo, pushed); phi = Vector3.Max(phi, pushed);
            }
            f.Cameras.Add(new Cctv(id, p0, lo, hi, a * 10f, b switch { 0 => 10, 1 => 20, 2 => 40, 3 => 60, 4 => 80, _ => 8 }, c, plo, phi));
        }
        int ne = r.Count(0x70);
        for (int i = 0; i < ne; i++)
        {
            r.Vec3();
            int id = r.I32();
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            var quad = new Vector2[4];
            for (int k = 0; k < 8; k++) { var v = r.Vec3(); lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); if (k < 4) quad[k] = new Vector2(v.X, v.Y); }
            f.Regions.Add(new Region(id, lo, hi, quad));
        }
        // F 카메라 컷: {i32 id, i32 −1, i32 틀 수, i32 키 사이 틀, i32 키 수 m, (m+1) × 키 0x4c}
        // 키 = f32[16] 행렬(자리 = 행3, 앞 = −행2, 위 = 행1) + 시야각(도) + near + far — 분석-시작장면 §4
        try
        {
            int nf = r.Count(0x18);
            for (int i = 0; i < nf; i++)
            {
                int id = r.I32(); r.I32(); int frames = r.I32(), step = r.I32(), m = r.I32();
                if (m is < 0 or > 10000) break;
                var keys = new CameraKey[m + 1];
                for (int k = 0; k <= m; k++) { var mat = r.Mat(); keys[k] = new CameraKey(mat, r.F32(), r.F32(), r.F32()); }
                f.Cuts[id] = new CameraCut(id, frames, Math.Max(step, 1), keys);
            }
        }
        catch (InvalidDataException) { }
        f.Events = FieldEvent.FindAndParse(r.Data, r.Pos);
        return f;
    }

    public record FieldObject(int Id, int Model, int Kind, Vector3 Position, Vector3 Rotation, byte[] Extra);
    /// <summary>
    /// E 상자 = 사건 구역(조건 304 가 id 로 부른다). 8 꼭짓점 = 위 넷 · 아래 넷 — 바닥이 **기울어진 사각형**인 기둥이다(532 출구 0 은 마름모).
    /// 감싸는 상자로 보면 들어간 자리가 되돌아가는 출구 안에 잡혀 531 ↔ 532 를 끝없이 오갔다 → 사각형 안쪽(볼록 사각형 변 부호)으로 본다.
    /// </summary>
    public record Region(int Id, Vector3 Min, Vector3 Max, Vector2[]? Quad = null)
    {
        public bool Contains(Vector3 p, float margin = 0)
        {
            if (p.X < Min.X - margin || p.X > Max.X + margin || p.Y < Min.Y - margin || p.Y > Max.Y + margin || p.Z < Min.Z - 40 || p.Z > Max.Z + 40) return false;
            if (Quad is not { Length: 4 }) return true;
            int pos = 0, neg = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = Quad[i]; var b = Quad[(i + 1) % 4];
                var e = b - a;
                float len = e.Length();
                if (len < 1e-3f) continue;
                float cross = (e.X * (p.Y - a.Y) - e.Y * (p.X - a.X)) / len;   // 변까지 거리(부호)
                if (cross > -margin) pos++;
                if (cross < margin) neg++;
            }
            return pos == 4 || neg == 4;
        }
    }

    public readonly Dictionary<int, CameraCut> Cuts = [];
    /// <summary>감시 카메라 — 처음 자리, 움직일 상자, 지킬 거리(줄), 나눗수(0x520532f0 표), c(부드럽게 넘김으로 추정).</summary>
    public record Cctv(int Id, Vector3 Start, Vector3 Min, Vector3 Max, float Rope, int Divisor, int Smooth, Vector3 ZoneMin = default, Vector3 ZoneMax = default)
    {
        /// <summary>목표가 민 상자 안인가(0x52052d7e — 원본은 민 상자 면 6장 +0x8c~+0xf0 안쪽 판정).</summary>
        public bool Covers(Vector3 p) => p.X >= ZoneMin.X && p.X <= ZoneMax.X && p.Y >= ZoneMin.Y && p.Y <= ZoneMax.Y && p.Z >= ZoneMin.Z && p.Z <= ZoneMax.Z;
    }
    public readonly List<Cctv> Cameras = [];
    public readonly List<FieldObject> Objects = [];
    public readonly List<Region> Regions = [];
    public List<FieldEvent> Events = [];
}

internal record CameraKey(System.Numerics.Matrix4x4 M, float Fov, float Near, float Far)
{
    public System.Numerics.Vector3 Eye => M.Translation;
    public System.Numerics.Vector3 Forward => -new System.Numerics.Vector3(M.M31, M.M32, M.M33);
    public System.Numerics.Vector3 Up => new(M.M21, M.M22, M.M23);
}

/// <summary>카메라 컷 — 키 사이 Step 틀씩, 모두 Frames 틀.</summary>
internal record CameraCut(int Id, int Frames, int Step, CameraKey[] Keys)
{
    public int Length => Math.Max(Frames, Step * (Keys.Length - 1));

    public (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Fwd, System.Numerics.Vector3 Up, float Fov) At(float tick)
    {
        float p = Math.Clamp(tick / Step, 0, Keys.Length - 1);
        int a = (int)p, b = Math.Min(a + 1, Keys.Length - 1);
        float t = p - a;
        var ka = Keys[a]; var kb = Keys[b];
        return (System.Numerics.Vector3.Lerp(ka.Eye, kb.Eye, t),
                System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Lerp(ka.Forward, kb.Forward, t)),
                System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Lerp(ka.Up, kb.Up, t)),
                ka.Fov + (kb.Fov - ka.Fov) * t);
    }
}

/// <summary>
/// fld 끝의 사건 자료: u32 n, n × {u32 깃발, i32 조건 수, 조건 60바이트×, i32 명령 수, 명령 60바이트×}.
/// 명령·조건 = u32 번호 + i32×14. 분석-스토리 · 분석-시작장면.
/// </summary>
internal sealed class FieldEvent
{
    public uint Trigger;
    public readonly List<int[]> Conditions = [], Actions = [];

    private static List<FieldEvent>? Parse(byte[] d, int off)
    {
        var r = new Reader(d, off);
        uint n = r.U32();
        if (n is 0 or >= 2000) return null;
        var list = new List<FieldEvent>();
        for (int e = 0; e < n; e++)
        {
            if (r.Remaining < 8) return null;
            var ev = new FieldEvent { Trigger = r.U32() };
            int ni = r.I32();
            if (ni is < 0 or >= 200 || r.Remaining < ni * 60 + 4) return null;
            for (int i = 0; i < ni; i++) ev.Conditions.Add(Cmd(r));
            int nt = r.I32();
            if (nt is < 0 or >= 2000 || r.Remaining < nt * 60) return null;
            for (int i = 0; i < nt; i++) ev.Actions.Add(Cmd(r));
            list.Add(ev);
        }
        return r.Pos == d.Length ? list : null;
    }

    private static int[] Cmd(Reader r)
    {
        var c = new int[15];
        for (int k = 0; k < 15; k++) c[k] = r.I32();
        return c;
    }

    /// <summary>파일 끝에서 정확히 끝나는 사건 묶음의 시작을 뒤에서부터 찾는다(story.find_events 와 같다).</summary>
    /// <remarks>
    /// 뒤에서 처음 맞는 자리를 쓰면 29 개 필드(161·655·771 …)에서 묶음 뒤쪽의 가짜 짝을 잡아 사건 대부분을 잃었다
    /// (655 = 장 넘김 필드는 사건이 비어 이야기가 133 → 655 에서 막힘). → F 묶음을 다 읽은 자리를 먼저 시험하고,
    /// 안 되면 파일 끝에서 정확히 끝나는 후보 중 **가장 앞의 것**(가장 큰 묶음)을 쓴다.
    /// </remarks>
    public static List<FieldEvent> FindAndParse(byte[] d, int hint = -1)
    {
        if (hint > 0 && hint < d.Length - 4 && Parse(d, hint) is { } atHint && Plausible(atHint)) return atHint;
        List<FieldEvent>? best = null, any = null;
        for (int off = d.Length - 8; off > 0; off -= 4)
            if (Parse(d, off) is { } list) { any = list; if (Plausible(list)) best = list; }
        return best ?? any ?? [];
    }

    /// <summary>조건 갈래는 0·100·101·302·304(0x52071af6 + 전투 302), 명령은 0~999 — 468 의 앞 후보처럼 실수 값이 조건 자리에 오는 가짜를 거른다.</summary>
    private static bool Plausible(List<FieldEvent> list) =>
        list.All(e => e.Conditions.All(c => c[0] is 0 or 100 or 101 or 302 or 304) && e.Actions.All(a => a[0] is >= 0 and < 1000));
}

/// <summary>필드 점 빛 하나(fld 0x428~, 0x4c 바이트).</summary>
internal sealed record FieldLight(int Id, System.Numerics.Vector3 Color, float Inner, float Outer, System.Numerics.Vector3 Pos, bool On);
