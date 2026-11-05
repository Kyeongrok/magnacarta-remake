using System.Numerics;

namespace MagnaCarta.Data;

/// <summary>.3do 재질 하나. 원본 구조체 0x58 바이트 중 쓰는 것만.</summary>
internal sealed class Material
{
    public Vector3 Color;
    public uint Flag;           // 0 보통 · 1 · 2(32비트 TGA 머리칼 등 — 알파)
    public Vector4 Diffuse;
    public int Texture;         // TXRS 번호, −1 없음
}

internal sealed class FaceGroup
{
    public required Material Material;
    public required ushort[] Indices;
    /// <summary>강체 모델의 부품(뼈) 차례 — 모션 명령 갈래 1·2 의 b0 가 가리킨다. 뼈 몸은 0.</summary>
    public int Part;
}

/// <summary>동작(시퀀스). 열쇠틀 = (ani 번호, 그 ani 의 틀), 열쇠틀마다 Step 틱 머문다.</summary>
internal sealed class Sequence
{
    public int Id, Length, Step, Hold;
    public required (int Ani, int Frame)[] Keys;
    /// <summary>
    /// 모션 명령(60바이트, 분석-모션명령): 열쇠틀(−1 = 시퀀스 내내), 갈래(10 효과음 SED b0 · 11 발소리 · 9 전투 때림 · 4 검광 · 3 텍스처 · 0 뼈에 물체 …), b0.
    /// </summary>
    public (int Key, int Type, int B0, int B2, int B1)[] Commands = [];
}

/// <summary>
/// 3DOBS\%04d.3do 갈래 0(캐릭터 몸). 정점은 자기 뼈의 지역 좌표 — 그리는 값 = v · 뼈의 모델공간 행렬.
/// 배치: 옵시디안 분석-렌더링-3D §2. 로더 MCAlpha 0x5202d35e.
/// </summary>
internal sealed class CharacterMesh
{
    /// <summary>검광(갈래 4 첫 것): 두 점(모델 공간)과 두 색 0xRRGGBB — 칼린츠 검 0023 은 (44, −0.49, 0.6)·(5, −3.49, 0.5), 주황 FF8000 → 짙은 빨강 800000.</summary>
    public (Vector3 P1, Vector3 P2, uint Col1, uint Col2)? SwordLight;
    public int Number;
    public Vector3[] Positions = [], Normals = [];
    public Vector2[] Uvs = [];
    public ushort[] Bone0 = [], Bone1 = [];
    public float[] Weight0 = [], Weight1 = [];
    public List<FaceGroup> Groups = [];
    public Vector3[] BonePivots = [];
    public List<Sequence> Sequences = [];

    public Sequence? FindSequence(int id) => Sequences.Find(s => s.Id == id && s.Keys.Length > 0);

    private static Material ReadMaterial(Reader r)
    {
        var m = new Material { Color = r.Vec3(), Flag = r.U32() };
        r.Skip(16);                                   // ambient
        m.Diffuse = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
        r.Skip(16 + 4);                               // specular, power
        m.Texture = r.I32();
        r.Skip(8);
        return m;
    }

    /// <summary>
    /// 갈래 1(뼈마다 강체 조각 — 상자·사물·무기): 조각 정점을 그 뼈 하나에 가중치 1 로 붙여 갈래 0 과 같은 꼴로 편다.
    /// u32 뼈 수, 기준점×, 뼈마다 {u32 재질 수(0 이면 끝), u32 정점 수, fvf, 보폭, 정점, 재질마다 {재질, 면}, u32}.
    /// </summary>
    private static void LoadRigid(Reader r, CharacterMesh m)
    {
        int nb = r.Count(12);
        m.BonePivots = new Vector3[nb];
        for (int i = 0; i < nb; i++) m.BonePivots[i] = r.Vec3();
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var bone = new List<ushort>();
        for (int b = 0; b < nb; b++)
        {
            int nmat = r.I32();
            if (nmat <= 0) continue;
            int nv = r.I32(); r.U32(); int stride = r.I32();
            int start = pos.Count, at = r.Pos;
            for (int i = 0; i < nv; i++)
            {
                r.Pos = at + i * stride;
                pos.Add(r.Vec3()); nrm.Add(r.Vec3());
                uv.Add(stride >= 32 ? new Vector2(r.F32(), r.F32()) : Vector2.Zero);
                bone.Add((ushort)b);
            }
            r.Pos = at + nv * stride;
            for (int g = 0; g < nmat; g++)
            {
                var mat = ReadMaterial(r);
                int nf = r.Count(6);
                var idx = new ushort[nf * 3];
                for (int i = 0; i < idx.Length; i++) idx[i] = (ushort)(r.U16() + start);
                if (idx.Length > 0) m.Groups.Add(new FaceGroup { Material = mat, Indices = idx, Part = b });
            }
            r.U32();
        }
        m.Positions = [.. pos]; m.Normals = [.. nrm]; m.Uvs = [.. uv]; m.Bone0 = [.. bone];
        m.Bone1 = Enumerable.Repeat((ushort)0xFFFF, pos.Count).ToArray();
        m.Weight0 = Enumerable.Repeat(1f, pos.Count).ToArray();
        m.Weight1 = new float[pos.Count];
    }

    public static CharacterMesh Load(int number)
    {
        var r = new Reader(McFiles.Read("3DOBS", number, "3do"));
        var m = new CharacterMesh { Number = number };
        uint kind = r.U32();
        if (kind == 1) LoadRigid(r, m);
        else if (kind != 0) throw new NotSupportedException($"3do {number} 갈래 {kind}");
        else
        {
        int nv = r.Count(32);
        r.Skip(8);                                    // fvf, x10
        m.Positions = new Vector3[nv]; m.Normals = new Vector3[nv]; m.Uvs = new Vector2[nv];
        for (int i = 0; i < nv; i++)
        {
            m.Positions[i] = r.Vec3();
            m.Normals[i] = r.Vec3();
            m.Uvs[i] = new Vector2(r.F32(), r.F32());
        }
        m.Bone0 = new ushort[nv]; m.Bone1 = new ushort[nv]; m.Weight0 = new float[nv]; m.Weight1 = new float[nv];
        for (int i = 0; i < nv; i++) m.Bone0[i] = r.U16();
        for (int i = 0; i < nv; i++) m.Bone1[i] = r.U16();
        for (int i = 0; i < nv; i++) m.Weight0[i] = r.F32();
        for (int i = 0; i < nv; i++) m.Weight1[i] = r.F32();
        int nmat = r.Count();
        for (int g = 0; g < nmat; g++)
        {
            var mat = ReadMaterial(r);
            int nf = r.Count(6);
            var idx = new ushort[nf * 3];
            for (int i = 0; i < idx.Length; i++) idx[i] = r.U16();
            m.Groups.Add(new FaceGroup { Material = mat, Indices = idx });
        }
        r.U32();                                      // x30
        int nb = r.Count(12);
        m.BonePivots = new Vector3[nb];
        for (int i = 0; i < nb; i++) m.BonePivots[i] = r.Vec3();
        }
        if (r.End) return m;

        // 옷자락·머리칼 흔들림 — 지금은 건너뛴다
        int nd = kind == 0 ? r.I32() : 0;          // 갈래 1 은 이 칸이 없다
        for (int k = 0; k < Math.Max(nd, 0); k++)
        {
            uint dkind = r.U32();
            int n = r.Count(36); r.Skip(n * 36);
            int c = r.Count(4); r.Skip(c * 4);
            r.Skip(12 + (dkind == 1 ? 16 : 32));
        }
        // 동작 목록
        r.Skip(16);                                   // "VERANIM" 꼬리표(이 판엔 안 씀)
        int nseq = r.I32();
        for (int s = 0; s < Math.Max(nseq, 0); s++)
        {
            int id = r.I32(), length = r.I32(), step = r.I32();
            int nk = r.U16(), hold = r.U16();
            var keys = new (int, int)[nk];
            for (int k = 0; k < nk; k++) keys[k] = (r.I32(), r.I32());
            int ncmd = r.Count(60);
            var cmds = new (int, int, int, int, int)[ncmd];
            for (int k = 0; k < ncmd; k++)
            {
                int at = r.Pos;
                int key = r.I32(); r.U32(); int type = r.I32(); int b0 = r.I32(); int b1 = r.I32(); int b2 = r.I32();
                cmds[k] = (key, type, b0, b2, b1);
                if (type == 4 && m.SwordLight == null)
                {
                    // 갈래 4 = 검광(분석-모션명령): b1·b2 = 0xRRGGBB 두 색, +36 부터 i32×6 = 두 점(천분의 일 단위, 모델 공간)
                    r.Pos = at + 36;
                    var p1 = new Vector3(r.I32(), r.I32(), r.I32()) / 1000f;
                    var p2 = new Vector3(r.I32(), r.I32(), r.I32()) / 1000f;
                    m.SwordLight = (p1, p2, (uint)b1, (uint)b2);
                }
                r.Pos = at + 60;
            }
            m.Sequences.Add(new Sequence { Id = id, Length = length, Step = Math.Max(step, 1), Hold = hold, Keys = keys, Commands = cmds });
        }
        return m;
    }
}

/// <summary>
/// AINIM\%04d.ani — 뼈마다 (틀 수 + 1)개의 부모 기준 지역 4×4 행렬 + 뼈 나무.
/// 마지막 틀 = 고리 닫는 틀(첫 틀과 같다). 로더 MCAlpha 0x5202c88a.
/// </summary>
internal sealed class BoneAnim
{
    public int Number, Length, Step, Frames, Bones;
    public Matrix4x4[,] Local = new Matrix4x4[0, 0];   // [뼈, 틀]
    public int[] Parent = [];
    /// <summary>부모가 자식보다 먼저 오는 차례.</summary>
    public int[] Order = [];

    public static BoneAnim Load(int number)
    {
        var r = new Reader(McFiles.Read("AINIM", number, "ani"));
        var a = new BoneAnim { Number = number };
        r.U32();
        a.Length = r.I32(); a.Step = r.I32(); a.Frames = r.I32(); a.Bones = r.I32();
        a.Local = new Matrix4x4[a.Bones, a.Frames + 1];
        for (int b = 0; b < a.Bones; b++)
            for (int f = 0; f <= a.Frames; f++) a.Local[b, f] = r.Mat();
        a.Parent = Enumerable.Repeat(-1, a.Bones).ToArray();
        for (int b = 0; b < a.Bones; b++)
        {
            r.U32();
            int n = r.Count(4);
            for (int k = 0; k < n; k++)
            {
                int c = r.I32();
                if (c >= 0 && c < a.Bones && c != b) a.Parent[c] = b;
            }
        }
        var order = new List<int>();
        var done = new bool[a.Bones];
        void Visit(int b)
        {
            if (done[b]) return;
            done[b] = true;
            if (a.Parent[b] >= 0) Visit(a.Parent[b]);
            order.Add(b);
        }
        for (int b = 0; b < a.Bones; b++) Visit(b);
        a.Order = [.. order];
        return a;
    }
}

/// <summary>AmdData\%04d.amd — 캐릭터 하나가 쓰는 3do 목록: u32 n, u32 1, (u32 칸, u32 3do)×n.</summary>
internal static class Amd
{
    public static Dictionary<int, int> Load(int number)
    {
        var r = new Reader(McFiles.Read("AmdData", number, "amd"));
        int n = r.I32();
        r.U32();
        var map = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) map[r.I32()] = r.I32();
        return map;
    }
}
