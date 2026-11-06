using System.IO;

// 창세기전3 리메이크(the-war-of-genesis-clicker/WarOfGenesis.Assets/ObsSprite.cs)에서 가져옴 — 마그나카르타 OBS 는 같은 꼴, 첫 u16 만 0x1993(분석-렌더링-2D §1-3).
namespace MagnaCarta.Data;

/// <summary>몸짓(Obs) 한 장 — 자리(X·Y)까지 포함해 BGRA 로 다 풀어 둔 것.</summary>
/// <param name="SlotId">
/// 파일에 적힌 <b>장 번호</b>. 모션표(<see cref="ObsMotionTable"/>)의 키가 가리키는 것이 이 번호다.
/// 인물 그림은 0부터 빈틈없이 이어져 <see cref="ObsMotion.Frames"/> 안의 순번과 같지만,
/// <c>Obs/0471.obs</c> 같은 UI 그림은 번호에 구멍이 있어 순번과 다르다 — 모션표로 컷을 고를 때는
/// 순번이 아니라 이 값으로 찾아야 한다.
/// </param>
internal sealed record ObsFrame(int SlotId, int Width, int Height, int X, int Y, byte[] Bgra);

/// <summary>몸짓 하나 — 여러 장(<see cref="ObsFrame"/>)이 한 벌이다. 이 벌 하나가 「한 동작」이다.</summary>
internal sealed record ObsMotion(int Id, IReadOnlyList<ObsFrame> Frames);

/// <summary>
/// <c>Obs/*.obs</c> — 창세기전3 파트2의 도트 그림(전투 모션·초상)을 담은 파일.
/// </summary>
/// <remarks>
/// <c>tools/extract_character.py</c> 의 <c>parse_obs_structure</c>/<c>parse_subentry</c>/
/// <c>decode_obs_payload</c>/<c>decode_obs_slot</c>/<c>indexed_to_rgba</c> 를 옮긴 것이다.
/// 그림 푸는 셈(<see cref="DecodePayload"/>)은 값 하나 안 고쳤고, 머리 읽는 쪽만 게임 로더
/// <c>LoadG4ObsFile</c>(<c>G3PartII.dll</c> <c>0x1002f050</c>, ImageBase <c>0x10000000</c>)에
/// 맞춰 고쳤다 — 노트 「Obs 파일 갈래(0471 같은 UI 그림)」. 자세한 것은
/// <see cref="ParseStructure"/>·<see cref="ParseSubEntry"/> 의 주석.
/// 확인용 파이썬 도구: <c>tools/re/obs_layout.py</c>.
///
/// 한 파일 안에 <b>몸짓(subentry) 여럿</b>이 있고, 몸짓 하나는 <b>장(slot) 여럿</b>으로
/// 된 한 벌이다 — 이 장 벌을 한 칸씩 넘겨 보는 것이 "전투 모션을 한 컷씩 본다"는 것이다.
/// </remarks>
internal static class ObsSprite
{
    /// <summary>장을 풀 때 쓰는 가로 보폭. 원본 캔버스 폭이다(<c>0x280</c> = 640).</summary>
    private const int Pitch = 0x280;

    private readonly record struct SubRef(ushort Id, uint Offset);

    private sealed record SubEntry(ushort Id, byte TransparentIndex, byte[] Palette, List<SlotRef> Slots);

    private readonly record struct SlotRef(ushort Id, uint Offset);

    private sealed record DecodedSlot(ushort SlotId, int Width, int Height, short X, short Y, byte[] Indexed);

    /// <summary>
    /// <b>첫 몸짓벌의 첫 장 하나만</b> 푼다 — 인물이 서 있는 자세 한 장만 필요할 때 쓴다.
    /// </summary>
    /// <remarks>
    /// <see cref="Decode"/> 는 파일 안의 몸짓벌·장을 <b>죄다</b> 풀어서(살라딘 sprite
    /// 하나가 백 장이 넘는다) 서 있는 그림 한 장만 필요할 때도 몇 초씩 걸린다. 이 쪽은
    /// 첫 벌의 첫 장 딱 하나만 풀어서 순식간에 끝난다.
    /// </remarks>
    public static ObsFrame? DecodeFirstFrame(string path) => DecodeFirstFrame(File.ReadAllBytes(path));

    /// <summary>메모리에 읽어 둔 Obs(예: pak 안 파일)의 첫 벌 첫 장.</summary>
    public static ObsFrame? DecodeFirstFrame(byte[] b)
    {
        var subrefs = ParseStructure(b);
        if (subrefs.Count == 0) return null;

        var entry = TryParseSubEntry(b, subrefs[0].Offset, subrefs[0].Id);
        if (entry == null || entry.Slots.Count == 0) return null;

        var slot = DecodeSlot(b, entry.Slots[0].Offset, entry.TransparentIndex);
        byte[] bgra = ToBgra(slot.Indexed, slot.Width, slot.Height, entry.Palette, entry.TransparentIndex);
        return new ObsFrame(slot.SlotId, slot.Width, slot.Height, slot.X, slot.Y, bgra);
    }

    public static List<ObsMotion> Decode(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>파일 대신 이미 읽은 바이트로 — pak 에서 꺼낸 그림을 임시 파일 없이 푼다.</summary>
    public static List<ObsMotion> Decode(byte[] b)
    {
        var subrefs = ParseStructure(b);

        var motions = new List<ObsMotion>();

        // 몸짓벌이 둘 이상일 때, 색인표에 적힌 둘째 벌부터의 자리값은 실제 머리 자리에서
        // 최대 이백 바이트 가까이 어긋나 있을 수 있다(갈무리로 확인) — 97바이트 안쪽만
        // 훑는 parse_subentry 로는 못 찾는다. 그런데 <b>바로 앞 벌의 마지막 장이 끝나는
        // 자리에는 정확히</b> 다음 벌의 머리가 있다 — 벌들이 파일 안에 잇달아 있기
        // 때문이다. 그래서 첫 벌만 색인표 자리를 믿고, 그다음부터는 앞 벌이 끝난 자리를
        // 우선 짚어 보고, 그래도 안 되면 색인표 자리로 물러난다.
        long nextSearchFrom = subrefs.Count > 0 ? subrefs[0].Offset : 0;

        foreach (var subref in subrefs)
        {
            SubEntry? entry = TryParseSubEntry(b, nextSearchFrom, subref.Id)
                            ?? TryParseSubEntry(b, subref.Offset, subref.Id);
            if (entry == null) { nextSearchFrom = subref.Offset; continue; }

            var frames = new List<ObsFrame>();
            foreach (var slotRef in entry.Slots)
            {
                try
                {
                    var slot = DecodeSlot(b, slotRef.Offset, entry.TransparentIndex);
                    byte[] bgra = ToBgra(slot.Indexed, slot.Width, slot.Height, entry.Palette, entry.TransparentIndex);
                    frames.Add(new ObsFrame(slot.SlotId, slot.Width, slot.Height, slot.X, slot.Y, bgra));
                }
                catch { /* 이 장만 건너뛴다 — 원본 도구도 그랬다. */ }
            }
            if (frames.Count > 0) motions.Add(new ObsMotion(entry.Id, frames));

            nextSearchFrom = EndOfLastSlot(b, entry);
        }
        return motions;
    }

    /// <summary>
    /// 고른 장만 푼다 — (몸짓벌 번호, <b>장 번호</b>) 가 <paramref name="wanted"/> 에 든 것. 모션표 키(<see cref="MotionKey"/>)가
    /// 가리키는 컷 몇 장만 필요할 때(전투 화면의 서기 모션) 파일 전체를 푸는 <see cref="Decode"/> 보다 훨씬 빠르다.
    /// 벌 머리 찾기는 <see cref="Decode"/> 와 같다(앞 벌 끝 자리 우선).
    /// </summary>
    public static Dictionary<(int Sub, int Slot), ObsFrame> DecodeFrames(byte[] b, IReadOnlySet<(int Sub, int Slot)> wanted)
    {
        var result = new Dictionary<(int, int), ObsFrame>();
        var subrefs = ParseStructure(b);
        var wantedSubs = wanted.Select(w => w.Sub).ToHashSet();
        long nextSearchFrom = subrefs.Count > 0 ? subrefs[0].Offset : 0;

        foreach (var subref in subrefs)
        {
            if (result.Count == wanted.Count) break;
            SubEntry? entry = TryParseSubEntry(b, nextSearchFrom, subref.Id)
                            ?? TryParseSubEntry(b, subref.Offset, subref.Id);
            if (entry == null) { nextSearchFrom = subref.Offset; continue; }

            // 모션표가 가리키는 것은 <b>장 번호</b>지 벌 안 순번이 아니다 — 번호에 구멍이 있는 Obs(0471·0138)에서 둘이 어긋난다.
            if (wantedSubs.Contains(entry.Id))
                foreach (var slotRef in entry.Slots)
                {
                    if (!wanted.Contains((entry.Id, slotRef.Id))) continue;
                    try
                    {
                        var slot = DecodeSlot(b, slotRef.Offset, entry.TransparentIndex);
                        byte[] bgra = ToBgra(slot.Indexed, slot.Width, slot.Height, entry.Palette, entry.TransparentIndex);
                        result[(entry.Id, slot.SlotId)] = new ObsFrame(slot.SlotId, slot.Width, slot.Height, slot.X, slot.Y, bgra);
                    }
                    catch { /* 이 장만 건너뛴다 — Decode 와 같다. */ }
                }
            nextSearchFrom = EndOfLastSlot(b, entry);
        }
        return result;
    }

    private static SubEntry? TryParseSubEntry(byte[] b, long listedOffset, ushort expectedId)
    {
        if (listedOffset < 0) return null;
        try { return ParseSubEntry(b, listedOffset, expectedId); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>그 몸짓벌의 마지막 장 자료가 끝나는 자리 — 다음 벌의 머리가 있을 자리다.</summary>
    private static long EndOfLastSlot(byte[] b, SubEntry entry)
    {
        if (entry.Slots.Count == 0) return 0;
        var last = entry.Slots[^1];
        uint payloadSize = U32(b, last.Offset + 2);
        return last.Offset + 14 + payloadSize;
    }

    /// <summary>
    /// 파일 머리 — <c>u16 ?(늘 0) · u16 몸짓벌 수 · u16 가장 큰 몸짓벌 번호</c> 다음에
    /// <c>(u16 벌 번호, u32 자리)</c> 가 벌 수만큼 잇달아 온다(6바이트씩, <c>0x06</c> 부터).
    /// 그 뒤가 모션표다(<see cref="ObsMotionTable"/>).
    /// </summary>
    /// <remarks>
    /// 노트 「Obs 파일 갈래(0471 같은 UI 그림)」 — 게임 로더 <c>LoadG4ObsFile</c>
    /// <c>0x1002f050</c>. 세 번째 값은 <b>벌 수 - 1</b> 이 아니라 <b>가장 큰 벌 번호</b>이고,
    /// 로더는 여기에 1을 더해(<c>0x1002f103 inc</c>) 번호로 찾아 쓰는 배열 칸 수로 삼는다.
    /// </remarks>
    private static List<SubRef> ParseStructure(byte[] b)
    {
        if (b.Length < 12) throw new InvalidDataException("OBS 머리가 너무 짧습니다.");

        int subentryCount = U16(b, 2);
        if (subentryCount is < 1 or > 512)
            throw new InvalidDataException($"몸짓 수가 이상합니다: {subentryCount}");

        var subrefs = new List<SubRef>();
        int cursor = 0x06;
        for (int i = 0; i < subentryCount; i++)
        {
            if (cursor + 6 > b.Length) throw new InvalidDataException("몸짓 자리표가 잘렸습니다.");
            subrefs.Add(new SubRef((ushort)U16(b, cursor), U32(b, cursor + 2)));
            cursor += 6;
        }
        return subrefs;
    }

    /// <summary>
    /// 몸짓 머리를 찾는다. 알려 준 자리에서 최대 96바이트 어긋나 있을 수 있어 하나씩 밀어 본다.
    /// </summary>
    /// <remarks>
    /// 벌 머리 배치(로더 <c>0x1002f22c</c>~<c>0x1002f38f</c>):
    /// <c>u16 벌 번호 · u16 장 수 · u16 가장 큰 장 번호 · u8 투명 색인 · u8[768] 팔레트(RGB)</c>
    /// 다음에 <c>(u16 장 번호, u32 자리)</c> 가 장 수만큼.
    ///
    /// 노트 「Obs 파일 갈래(0471 같은 UI 그림)」 — 셋째 값은 <b>장 수 - 1</b> 이 아니라
    /// <b>가장 큰 장 번호</b>다(<c>0x1002f26a inc WORD PTR [edi]</c> 로 1을 더해 배열 칸
    /// 수로 쓴다). 인물 그림은 장 번호가 0부터 빈틈없이 이어져 둘이 우연히 같지만,
    /// <c>Obs/0471.obs</c> 같은 UI 그림은 <b>장 번호에 구멍이 있다</b>(벌 0 = 장 16개인데
    /// 번호는 0,1,3,4,6,7,9…18). 예전처럼 <c>셋째 + 1 == 장 수</c> 를 따지면 이런 파일은
    /// 벌을 하나도 못 찾는다. 그림 자료 꼴은 다른 Obs 와 똑같다(8비트 색인 + 768바이트 팔레트).
    /// </remarks>
    private static SubEntry ParseSubEntry(byte[] b, long listedOffset, ushort expectedId)
    {
        for (int headerSkip = 0; headerSkip < 97; headerSkip++)
        {
            long header = listedOffset + headerSkip;
            if (header + 7 + 768 > b.Length) continue;

            int id = U16(b, header);
            int slotCount = U16(b, header + 2);
            int maxSlotId = U16(b, header + 4);
            if (id != expectedId || slotCount is < 1 or > 256) continue;
            if (maxSlotId > 255 || maxSlotId + 1 < slotCount) continue;

            byte transparentIndex = b[header + 6];
            var palette = new byte[768];
            Array.Copy(b, header + 7, palette, 0, 768);

            long cursor = header + 7 + 768;
            var slots = new List<SlotRef>();
            bool valid = true;

            for (int i = 0; i < slotCount; i++)
            {
                if (cursor + 6 > b.Length) { valid = false; break; }
                ushort slotId = (ushort)U16(b, cursor);
                uint slotOffset = U32(b, cursor + 2);
                if (slotOffset + 14 > b.Length) { valid = false; break; }

                uint payloadSize = U32(b, slotOffset + 2);
                int width = U16(b, slotOffset + 6);
                int height = U16(b, slotOffset + 8);
                if (width <= 0 || height <= 0 || width > 2048 || height > 2048) { valid = false; break; }
                if (payloadSize > b.Length - slotOffset - 14) { valid = false; break; }

                slots.Add(new SlotRef(slotId, slotOffset));
                cursor += 6;
            }

            if (valid) return new SubEntry((ushort)id, transparentIndex, palette, slots);
        }
        throw new InvalidDataException($"몸짓 {expectedId} 을(를) {listedOffset} 부근에서 못 찾았습니다.");
    }

    private static DecodedSlot DecodeSlot(byte[] b, uint slotOffset, byte fill)
    {
        ushort slotId = (ushort)U16(b, slotOffset);
        uint payloadSize = U32(b, slotOffset + 2);
        int width = U16(b, slotOffset + 6);
        int height = U16(b, slotOffset + 8);
        short x = S16(b, slotOffset + 10);
        short y = S16(b, slotOffset + 12);

        var payload = new byte[payloadSize];
        Array.Copy(b, slotOffset + 14, payload, 0, payloadSize);

        byte[] indexed = DecodePayload(payload, width, height, Pitch, fill);
        return new DecodedSlot(slotId, width, height, x, y, indexed);
    }

    /// <summary>
    /// 장 하나의 색인 그림을 편다 — 줄마다 건너뛰는 조각을 이어 붙이는 자체 RLE다.
    /// </summary>
    /// <remarks>
    /// <b>원본 도구의 셈을 한 걸음도 안 바꾸고 그대로 옮겼다.</b> <c>span</c> 이 도는 중에
    /// 값이 바뀌면 <c>while</c> 조건(<c>consumed &lt; (span &amp; 0xffff)</c>)도 매번 다시
    /// 셈해진다 — 파이썬과 똑같이 C# 의 <c>while</c> 도 그렇게 돈다.
    /// </remarks>
    private static byte[] DecodePayload(byte[] payload, int width, int height, int pitch, byte fill)
    {
        if (payload.Length < 2) throw new InvalidDataException("장 그림이 너무 짧습니다.");

        int recordCount = U16(payload, 0);
        var output = new byte[width * height];
        Array.Fill(output, fill);

        int cursor = 2;
        int outputIndex = 0;
        bool wrapped = false;

        for (int r = 0; r < recordCount; r++)
        {
            if (cursor + 4 > payload.Length) throw new InvalidDataException("장 레코드 머리가 잘렸습니다.");
            int skip = U16(payload, cursor); cursor += 2;
            int quadCount = payload[cursor]; cursor += 1;
            int tailCount = payload[cursor]; cursor += 1;

            long span = skip;
            if (skip != 0)
            {
                int consumed = 0;
                while (consumed < (span & 0xffff))
                {
                    if (wrapped)
                    {
                        wrapped = false;
                        span += width - pitch;
                        if (consumed != (span & 0xffff)) outputIndex++;
                    }
                    else
                    {
                        outputIndex++;
                        if (outputIndex % width == 0) span += width - pitch;
                    }
                    consumed++;
                }
            }

            int literalCount = tailCount + quadCount * 4;
            if (cursor + literalCount > payload.Length) throw new InvalidDataException("장 그림 자료가 잘렸습니다.");
            // 칸을 넘치는 그림(나침반 화살 OBS 0216 등) — 들어가는 만큼만 베끼고 멈춘다(분석 도구 render2d 와 같은 처리)
            if (outputIndex + literalCount > output.Length)
            {
                if (outputIndex < output.Length) Array.Copy(payload, cursor, output, outputIndex, output.Length - outputIndex);
                return output;
            }

            Array.Copy(payload, cursor, output, outputIndex, literalCount);
            outputIndex += literalCount;
            cursor += literalCount;

            if (tailCount + quadCount != 0 && outputIndex % width == 0) wrapped = true;
        }
        return output;
    }

    /// <summary>색인 그림 + 팔레트 → BGRA(WPF <c>Bgra32</c> 순서: B,G,R,A).</summary>
    private static byte[] ToBgra(byte[] indexed, int width, int height, byte[] palette, byte transparentIndex)
    {
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < indexed.Length; i++)
        {
            byte idx = indexed[i];
            int po = idx * 3;
            byte r = po < palette.Length ? palette[po] : (byte)0;
            byte g = po + 1 < palette.Length ? palette[po + 1] : (byte)0;
            byte bl = po + 2 < palette.Length ? palette[po + 2] : (byte)0;
            byte a = idx == transparentIndex ? (byte)0 : (byte)255;

            int o = i * 4;
            bgra[o] = bl;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = a;
        }
        return bgra;
    }

    private static int U16(byte[] b, long o) => b[o] | (b[o + 1] << 8);
    private static uint U32(byte[] b, long o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static short S16(byte[] b, long o) => (short)U16(b, o);
}
