namespace MagnaCarta.Data;

/// <summary>
/// Fpb\%04d.fpb — 필드 대사: u32 n, n × {u32 id, u32 자리, u32 길이}, u32 전체 길이, CP949 글.
/// 대사 명령은 차례가 아니라 id 로 줄을 찾는다(분석-스토리). 「$n」 은 줄바꿈.
/// </summary>
internal static class Fpb
{
    public static Dictionary<int, string> Load(int number)
    {
        var map = new Dictionary<int, string>();
        if (number < 0 || !McFiles.Exists("Fpb", number, "fpb")) return map;
        var d = McFiles.Read("Fpb", number, "fpb");
        var r = new Reader(d);
        int n = r.I32();
        int pool = 4 + 12 * n + 4;
        for (int i = 0; i < n; i++)
        {
            int id = r.I32(), at = r.I32(), len = r.I32();
            if (pool + at + len > d.Length || len < 0) continue;
            var s = d.AsSpan(pool + at, len);
            int nul = s.IndexOf((byte)0);
            map[id] = McFiles.Cp949.GetString(s[..(nul < 0 ? len : nul)]).Replace("$n", "\n");
        }
        return map;
    }
}

/// <summary>Itm\0000.itm — 88개 × 264바이트, 머리 8. +0 id, +4 이름[100] (분석-아이템).</summary>
internal sealed record ItemData(int Id, string Name, int Slot, int Armor, int Hp, int Str, int Def, int Int, int Dex, int Element, int Affinity, string Description)
{
    /// <summary>몸 장비 종류 → 입는 CharData id (장비 함수 0x52081615 의 표). 0 = 누구나.</summary>
    public static readonly Dictionary<int, int> ArmorOwner = new() { [1] = 0, [2] = 3, [3] = 2, [4] = 1, [5] = 4, [6] = 6, [7] = 5, [8] = 20 };
    public bool CanWear(int charId) => Slot is >= 0 and <= 3 && (Slot != 0 || Armor == 0 || ArmorOwner.GetValueOrDefault(Armor, -1) == charId);
}

internal static class ItemNames
{
    private static Dictionary<int, ItemData>? _items;

    /// <summary>+0x68 부터 i32×15: 값·칸(0 몸 1 반지 2 목걸이 3 기타 4 열쇠)·몸 종류·?·체력·공격·방어·지력·민첩·…·속성·친화. 설명 +0xa4.</summary>
    public static ItemData? Get(int id)
    {
        if (_items == null)
        {
            _items = [];
            var d = File.ReadAllBytes(Path.Combine(McFiles.Dir, "Itm", "0000.itm"));
            int n = BitConverter.ToInt32(d, 0);
            string Str(int at, int len) { var s = d.AsSpan(at, len); int z = s.IndexOf((byte)0); return McFiles.Cp949.GetString(s[..(z < 0 ? len : z)]); }
            for (int i = 0; i < n; i++)
            {
                int o = 8 + i * 264;
                int F(int k) => BitConverter.ToInt32(d, o + 0x68 + 4 * k);
                _items[BitConverter.ToInt32(d, o)] = new ItemData(BitConverter.ToInt32(d, o), Str(o + 4, 100), F(1), F(2), F(4), F(5), F(6), F(7), F(8), F(12), F(13), Str(o + 0xa4, 100));
            }
        }
        return _items.GetValueOrDefault(id);
    }

    private static Dictionary<int, string>? _names;

    public static string Of(int id)
    {
        if (_names == null)
        {
            _names = [];
            var d = File.ReadAllBytes(Path.Combine(McFiles.Dir, "Itm", "0000.itm"));
            int n = BitConverter.ToInt32(d, 0);
            for (int i = 0; i < n; i++)
            {
                int o = 8 + i * 264;
                var s = d.AsSpan(o + 4, 100);
                int nul = s.IndexOf((byte)0);
                _names[BitConverter.ToInt32(d, o)] = McFiles.Cp949.GetString(s[..(nul < 0 ? 100 : nul)]);
            }
        }
        return _names.TryGetValue(id, out var name) ? name : $"아이템 {id}";
    }
}
