using System.Numerics;
using System.Text.Json;

namespace MagnaCarta.Scenes;

/// <summary>
/// 패치판 세이브 — JSON 한 칸(%LOCALAPPDATA%\MagnaCartaPatch\save0.json). 원본 MCxx.sav(23514바이트)는
/// 장소 이름(+4)·필드 번호(+0x16)만 풀려 있어 아직 읽지도 쓰지도 않는다(분석-도시·분석-스테이지).
/// </summary>
internal static class SaveGame
{
    public sealed record Data(int Field, float X, float Y, float Z, float Yaw, string Place, DateTime Time,
        Dictionary<int, int> Switches, List<int> Items, List<int[]> Chests, List<int>? Party = null, Dictionary<int, int[]>? Equipment = null, Dictionary<int, List<int>>? Evolutions = null, Dictionary<int, List<int>>? AdoraShare = null, List<CharDto>? Overrides = null, double PlayMs = 0, int[]? CartaPoints = null, Dictionary<int, int[]>? AffinityBonus = null);

    /// <summary>원본 세이브에서 읽은 캐릭터를 패치판 세이브에 남길 꼴.</summary>
    public sealed record CharDto(int Id, string Name, int Kind, int Tp, int Wtp, int Hp, int Str, int Def, int Int, int Dex, int Amd, int Face, int Singi,
        int Head, int Breast, int Abdomen, int Arm, int Leg, int[] Carta, List<int> Abilities)
    {
        public static CharDto From(MagnaCarta.Data.CharParam c) => new(c.Id, c.Name, c.Kind, c.Tp, c.Wtp, c.Hp, c.Str, c.Def, c.Int, c.Dex, c.Amd, c.Face, c.Singi,
            c.Head, c.Breast, c.Abdomen, c.Arm, c.Leg, [.. c.Carta], [.. c.Abilities]);

        public MagnaCarta.Data.CharParam To()
        {
            var c = new MagnaCarta.Data.CharParam { Id = Id, Name = Name, Kind = Kind, Tp = Tp, Wtp = Wtp, Hp = Hp, Str = Str, Def = Def, Int = Int, Dex = Dex, Amd = Amd, Face = Face, Singi = Singi,
                Head = Head, Breast = Breast, Abdomen = Abdomen, Arm = Arm, Leg = Leg, Carta = [.. Carta], Abilities = [.. Abilities] };
            c.RecomputeAffinity();
            return c;
        }
    }

    public static string PathOf(int slot = 0) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch", $"save{slot}.json");

    public static bool Exists(int slot = 0) => File.Exists(PathOf(slot));

    /// <summary>목록에 보일 한 줄(장소 · 놀이 시간).</summary>
    public static string? Describe(int slot = 0)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(PathOf(slot)));
            if (d == null) return null;
            var t = TimeSpan.FromMilliseconds(d.PlayMs);
            return $"{(d.Place.Length > 0 ? d.Place : "필드 " + d.Field)}  {(int)t.TotalHours}시간 {t.Minutes}분";
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public static void Save(int field, Vector3 pos, float yaw, string place, int slot = 0)
    {
        var d = new Data(field, pos.X, pos.Y, pos.Z, yaw, place, DateTime.Now,
            new(GameState.GlobalSwitches), [.. GameState.Items], GameState.OpenedChests.Select(c => new[] { c.Field, c.Id }).ToList(), [.. GameState.Party], new(GameState.Equipment), new(GameState.Evolutions), new(GameState.AdoraShare), GameState.Overrides.Values.Select(CharDto.From).ToList(), GameState.PlayMs, [.. GameState.CartaPoints], new(GameState.AffinityBonus));
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(slot))!);
        File.WriteAllText(PathOf(slot), JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Data? Load(int slot = 0)
    {
        if (!Exists(slot)) return null;
        try
        {
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(PathOf(slot)));
            if (d == null) return null;
            GameState.GlobalSwitches.Clear();
            foreach (var (k, v) in d.Switches) GameState.GlobalSwitches[k] = v;
            GameState.Items.Clear(); GameState.Items.AddRange(d.Items);
            GameState.OpenedChests.Clear();
            foreach (var c in d.Chests) GameState.OpenedChests.Add((c[0], c[1]));
            GameState.Party.Clear(); GameState.Party.AddRange(d.Party is { Count: > 0 } p ? p : [0]);
            GameState.Equipment.Clear();
            foreach (var (k, v) in d.Equipment ?? []) GameState.Equipment[k] = v;
            GameState.Evolutions.Clear(); foreach (var (k, v) in d.Evolutions ?? []) GameState.Evolutions[k] = v;
            GameState.AdoraShare.Clear(); foreach (var (k, v) in d.AdoraShare ?? []) GameState.AdoraShare[k] = v;
            GameState.Overrides.Clear(); foreach (var c in d.Overrides ?? []) GameState.Overrides[c.Id] = c.To();
            GameState.PlayMs = d.PlayMs;
            if (d.CartaPoints is { Length: 7 } cp) cp.CopyTo(GameState.CartaPoints, 0);
            GameState.AffinityBonus.Clear(); foreach (var (k, v) in d.AffinityBonus ?? []) GameState.AffinityBonus[k] = v;
            return d;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>새 게임 — 상태를 비운다.</summary>
    public static void Reset()
    {
        GameState.GlobalSwitches.Clear();
        GameState.Items.Clear();
        GameState.OpenedChests.Clear();
        GameState.Party.Clear(); GameState.Party.Add(0);
        GameState.Equipment.Clear();
        GameState.Evolutions.Clear();
        GameState.AdoraShare.Clear();
        GameState.Overrides.Clear();
        GameState.PlayMs = 0;
        Array.Fill(GameState.CartaPoints, 40);
        GameState.AffinityBonus.Clear();
    }
}
