using System.Buffers.Binary;
using System.Numerics;
using MagnaCarta.Data;

namespace MagnaCarta.Scenes;

/// <summary>
/// 원본 세이브 MCxx.sav(23514바이트) 읽기 — 분석-세이브(쓰기 0x5206fdac). 끝 4바이트 = CRC32(앞 전부).
/// +0 판 4 · +4 장소 이름 0x12 · +0x16 필드 · +0x1a 놀이 시간(ms, 추정) · +0x1e 파티 수 · +0x22 파티 7칸 · … · +0x72 소지품 1024 ·
/// +0x1072 지역 스위치 2000 · +0x2FB2 전역 스위치 2000 · +0x4EF2 자리·바라보는 쪽 · +0x4F0A 캐릭터 8 × 281바이트(절대값).
/// </summary>
internal static class OriginalSave
{
    public sealed record Summary(int Slot, string Place, int Field, TimeSpan Play, bool CrcOk);

    public static string PathOf(int slot) => Path.Combine(McFiles.Dir, $"MC{slot:D2}.sav");

    public static Summary? Peek(int slot)
    {
        if (!File.Exists(PathOf(slot))) return null;
        var d = File.ReadAllBytes(PathOf(slot));
        if (d.Length < 0x4F0A || BitConverter.ToInt32(d, 0) != 4) return null;
        return new Summary(slot, Str(d, 4, 0x12), BitConverter.ToInt32(d, 0x16), TimeSpan.FromMilliseconds(BitConverter.ToUInt32(d, 0x1a)), CrcOk(d));
    }

    private static bool CrcOk(byte[] d)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < d.Length - 4; i++)
        {
            crc ^= d[i];
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc == BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(d.Length - 4));
    }

    private static string Str(byte[] d, int at, int len)
    {
        var s = d.AsSpan(at, len);
        int z = s.IndexOf((byte)0);
        return McFiles.Cp949.GetString(s[..(z < 0 ? len : z)]);
    }

    /// <summary>GameState 를 세이브 값으로 채우고 (필드, 자리, 방향) 을 돌려준다.</summary>
    public static (int Field, Vector3 Pos, float Yaw)? Load(int slot)
    {
        if (!File.Exists(PathOf(slot))) return null;
        var d = File.ReadAllBytes(PathOf(slot));
        if (d.Length < 0x4F0A + 4 || BitConverter.ToInt32(d, 0) != 4) return null;
        var r = new Reader(d, 0x16);
        int field = r.I32();
        GameState.PlayMs = r.U32();                    // 놀이 시간 ms(장면 +0x40 에 timeGetTime 차를 더함, 0x5206fd5b) — 이어서 센다
        GameState.EncounterTicks = 0; GameState.EncounterCountdown = -1;   // 원본 불러오기(0x5207021e)도 조우 셈을 0 으로
        int partyCount = r.I32();
        var party = new int[7];
        for (int i = 0; i < 7; i++) party[i] = r.I32();
        // +0x42 전투 4명(CharData id, 빈칸 −1) — 원본 Party 쪽 「BATTLE MEMBER ALIGNMENT」 고른 것. 파티에 있으나 여기 없는 사람은 출전 빼기로
        r.Pos = 0x42;
        var fighters = new HashSet<int>();
        for (int i = 0; i < 4; i++) { int v = r.I32(); if (v >= 0) fighters.Add(v); }
        GameState.Benched.Clear();
        // MC02·03 처럼 파티에 없는 사람(떠난 아도라 3)이 남아 있으면 낡은 목록으로 보고 쓰지 않는다(그때 원본이 누구를 내보내는지는 못 봄)
        if (fighters.Count > 0 && fighters.All(id => party.Take(Math.Clamp(partyCount, 0, 7)).Contains(id)))
            foreach (int id in party.Take(Math.Clamp(partyCount, 0, 7))) if (id >= 0 && !fighters.Contains(id)) GameState.Benched.Add(id);
        r.Pos = 0x56;
        for (int i = 0; i < 7; i++) GameState.CartaPoints[i] = r.I32();   // 카르타 점수 7칸(분석-전투결과: 세이브 +0x56)
        r.Pos = 0x72;
        GameState.Items.Clear();
        for (int i = 0; i < 1024; i++) { int v = r.I32(); if (v >= 0) GameState.Items.Add(v); }
        r.Skip(2000 * 4);                              // 지역 스위치 — 패치판은 필드마다 새로 시작(네 세이브 모두 비어 있음)
        GameState.GlobalSwitches.Clear();
        for (int i = 0; i < 2000; i++) { int v = r.I32(); if (v != 0) GameState.GlobalSwitches[i] = v; }
        var pos = r.Vec3();
        var dir = r.Vec3();
        int n = r.I32();
        GameState.Overrides.Clear();
        GameState.Equipment.Clear();
        GameState.Evolutions.Clear();
        GameState.AdoraShare.Clear();
        GameState.OpenedChests.Clear();
        for (int k = 0; k < n && r.Remaining >= 281; k++)
        {
            var c = ReadChar(r, out int[] equip);
            // 원본 Equip 은 기본값에 더해 둔다 — 패치판은 장비를 따로 더하므로 빼 둔다
            foreach (int e in equip)
                if (e >= 0 && ItemNames.Get(e) is { } it)
                {
                    c.Hp -= it.Hp; c.Str -= it.Str; c.Def -= it.Def; c.Int -= it.Int; c.Dex -= it.Dex;
                    if (it.Element is >= 1 and <= 7) c.Carta[it.Element - 1] -= it.Affinity;
                }
            c.RecomputeAffinity();
            GameState.Overrides[c.Id] = c;
            GameState.Equipment[c.Id] = equip;
        }
        GameState.Party.Clear();
        for (int i = 0; i < Math.Clamp(partyCount, 0, 7); i++) if (party[i] >= 0) GameState.Party.Add(party[i]);
        if (GameState.Party.Count == 0) GameState.Party.Add(0);
        float yaw = dir.LengthSquared() > 1e-6f ? MathF.Atan2(dir.X, -dir.Y) : 0;
        return (field, pos, yaw);
    }

    /// <summary>ChrParam 281바이트 — CharData 레코드(267)에 런타임 친화 14·장비 16 이 붙은 꼴.</summary>
    private static CharParam ReadChar(Reader r, out int[] equip)
    {
        var c = new CharParam { Id = r.I32() };
        c.Name = Str(r.Data, r.Pos, 100); r.Skip(100);
        c.Kind = r.Data[r.Pos]; r.Skip(1 + 2);
        c.Tp = r.I32(); c.Wtp = r.I32(); c.Hp = r.I32(); r.I32();
        c.Str = r.I32(); c.Def = r.I32(); c.Int = r.I32(); c.Dex = r.I32();
        r.Skip(12);
        c.Amd = (short)r.U16(); c.Face = (short)r.U16();
        c.TurnFace = (short)r.U16();                                    // +0xa0 전투 차례 얼굴 FrameData (분석-전투효과)
            c.SizeClass = (short)r.U16();                                   // +0xa2 크기 갈래(분석-전투효과)
            r.Skip(2 * 5 + 6 + 2 * 5);
        c.Singi = (short)r.U16();
        c.Head = (short)r.U16(); c.Breast = (short)r.U16(); c.Abdomen = (short)r.U16(); c.Arm = (short)r.U16(); c.Leg = (short)r.U16();
        for (int k = 0; k < 7; k++) c.Carta[k] = (short)r.U16();
        for (int k = 0; k < 10; k++) { int a = r.I32(); if (a >= 0) c.Abilities.Add(a); }
        r.Skip(14);                                                     // 런타임 친화 — 기본 친화로 다시 짓는다
        equip = [r.I32(), r.I32(), r.I32(), r.I32()];
        return c;
    }
}
