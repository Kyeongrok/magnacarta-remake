namespace MagnaCarta.Data;

/// <summary>
/// 전투 연출 EffScr\%04d.efs (Att +0x20 Affect 번호) — 분석-전투연출. 자원 표 6개 {u32 n, i32, n × 8바이트}, 묶음 {u32 n, i32, n × 28바이트},
/// 사건 묶음(필드 사건과 같은 60바이트 명령). 패치판은 사건 1 의 명령을 시간 줄로 편다:
/// 2 WAIT 틱 · 200 [묶음(0 시전자 · 1 대상), 시퀀스, …] 모션 · 800 [묶음, 방식] 때림. 끝 = WAIT 합.
/// </summary>
internal sealed class Affect
{
    public readonly record struct Step(float Tick, int Op, int Group, int Value, int Arg3 = 0, int Arg4 = 0);
    /// <summary>표1 = 3D 효과 물체(3DOBS 갈래 1) 차례 → 번호.</summary>
    public readonly Dictionary<int, int> Effects = [];
    /// <summary>표5 = Camera\%04d.cam 번호.</summary>
    public readonly Dictionary<int, int> Cameras = [];
    /// <summary>자원 표 2: 차례 → 동영상 번호(MOV\%04d.BMC) — 명령 508 이 튼다(분석-전투연출).</summary>
    public readonly Dictionary<int, int> Movies = [];
    /// <summary>자리 표 7칸: 차례, 기준(0 시전자 · 1 대상 · 15 월드), 위치(0 지금 · 1 처음), 높이(0 · ½ · 1 × 키), 오프셋(배우 지역, 앞 = −Y).</summary>
    public readonly Dictionary<int, (int Role, int PosMode, int Height, System.Numerics.Vector3 Offset)> Spots = [];
    public readonly List<Step> Steps = [];
    public float End;
    public int Hits => Steps.Count(s => s.Op == 800);

    private static readonly Dictionary<int, Affect?> Cache = [];
    private static readonly Dictionary<int, CameraCut?> CamCache = [];

    /// <summary>Camera\%04d.cam: u32 틀 수, u32 키 사이 틀, u32 n, 키 0x4c × (n 또는 n+1) — 분석-스테이지 §4.</summary>
    public static CameraCut? LoadCamera(int number)
    {
        if (CamCache.TryGetValue(number, out var c)) return c;
        c = null;
        if (McFiles.Exists("Camera", number, "cam"))
        {
            var d = McFiles.Read("Camera", number, "cam");
            var r = new Reader(d);
            int frames = r.I32(), step = r.I32(); r.I32();
            int cnt = (d.Length - 12) / 0x4c;
            var keys = new CameraKey[cnt];
            for (int k = 0; k < cnt; k++) { var m = r.Mat(); keys[k] = new CameraKey(m, r.F32(), r.F32(), r.F32()); }
            if (cnt > 0) c = new CameraCut(number, frames, Math.Max(step, 1), keys);
        }
        CamCache[number] = c;
        return c;
    }

    public static Affect? Load(int number)
    {
        if (Cache.TryGetValue(number, out var a)) return a;
        Cache[number] = a = TryLoad(number);
        return a;
    }

    private static Affect? TryLoad(int number)
    {
        if (number < 0 || !McFiles.Exists("EffScr", number, "efs")) return null;
        try
        {
            var r = new Reader(McFiles.Read("EffScr", number, "efs"));
            var af = new Affect();
            for (int t = 0; t < 6; t++)
            {
                int n = r.Count(8); r.I32();
                for (int i = 0; i < n; i++) { int idx = r.I32(), num = r.I32(); if (t == 1) af.Effects[idx] = num; if (t == 2) af.Movies[idx] = num; if (t == 5) af.Cameras[idx] = num; }
            }
            {
                int n = r.Count(28); r.I32();
                for (int i = 0; i < n; i++)
                {
                    int idx = r.I32(), role = r.I32(), pm = r.I32(), hm = r.I32();
                    var off = new System.Numerics.Vector3(BitConverter.Int32BitsToSingle(r.I32()), BitConverter.Int32BitsToSingle(r.I32()), BitConverter.Int32BitsToSingle(r.I32()));
                    af.Spots[idx] = (role, pm, hm, off);
                }
            }
            int ne = r.Count();
            float tick = 0;
            for (int e = 0; e < ne; e++)
            {
                r.U32();
                int ni = r.Count(60); r.Skip(60 * ni);
                int nt = r.Count(60);
                for (int k = 0; k < nt; k++)
                {
                    var c = new int[15];
                    for (int q = 0; q < 15; q++) c[q] = r.I32();
                    switch (c[0])
                    {
                        case 2: tick += Math.Max(0, c[1]); break;
                        case 200 or 201: af.Steps.Add(new Step(tick, 200, c[1], c[2])); break;
                        case 800: af.Steps.Add(new Step(tick, 800, c[1], c[2])); break;
                        case 508: af.Steps.Add(new Step(tick, 508, c[1], c[2])); break;   // [표2 차례, ?] 동영상
                        case 310: af.Steps.Add(new Step(tick, 310, c[1], c[2], c[3])); break;  // [표1 차례, 크기 %, 틀 수−1]
                        case 306: af.Steps.Add(new Step(tick, 306, c[1], c[2], c[3])); break;  // [표1 차례, 자리, 틀]
                        // 301 = 꺼짐(모델 +0x118 — 그리기·갱신 둘 다 멈춤) · 303 = 숨기기(+0x114) → 패치판은 둘 다 효과를 없앤다. 302 = 보이기(+0x114 = 0) — 이미 보이므로 할 일 없음
                        case 301 or 303: af.Steps.Add(new Step(tick, 303, c[1], 0)); break;
                        case 500: af.Steps.Add(new Step(tick, 500, c[1], c[2], c[3])); break;  // [표5 차례, 깃발, 자리]
                        case 300: af.Steps.Add(new Step(tick, 300, c[1], c[2], c[3], c[4])); break;   // [표1 차례, 시퀀스, 되풀이(−1 끝없이), 자리]
                    }
                }
            }
            af.End = tick;
            return af;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return null; }
    }
}
