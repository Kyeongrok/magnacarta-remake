namespace MagnaCarta.Scenes;

/// <summary>한 판의 상태 — 전역 스위치, 가진 아이템. 세이브는 아직 없다.</summary>
internal static class GameState
{
    public static readonly Dictionary<int, int> GlobalSwitches = [];
    public static readonly List<int> Items = [];     // 원본 가방은 1024칸, 겹치지 않음(분석-아이템)

    /// <summary>
    /// 연 상자 (필드, 물체 id). 원본은 전역 스위치 하나로만 기억해, 론웰던젼 0546·0548 처럼 스위치(75)를 같이 쓰는 상자는
    /// 하나를 열면 다른 하나도 열린 것이 된다(원본 버그) — 패치판은 상자마다 따로 기억한다.
    /// </summary>
    public static readonly HashSet<(int Field, int Id)> OpenedChests = [];

    /// <summary>파티 — CharData id 차례. 새 게임은 칼린츠 하나(Pat 0000), 사건 804/805 로 넣고 뺀다(원본 전투는 4명 — 분석-세이브 +0x42).</summary>
    public static readonly List<int> Party = [0];

    public static void Join(int id) { if (!Party.Contains(id)) Party.Add(id); }
    /// <summary>조작 캐릭터(CharData id) — 사건 809 가 바꾼다(0x5207f53f: 파티 목록에서 찾아 그 차례를 +0x64 에, 없으면 「%s 는 파티데이터에 없다.」).
    /// 343 에서 3 으로 바뀌어 618~619 는 그 인물만 서 있다.</summary>
    public static int Leader;
    public static void Leave(int id) { if (Party.Count > 1) Party.Remove(id); }

    /// <summary>장비 — 캐릭터마다 4칸(몸·반지·목걸이·기타), −1 빈칸. 원본 ChrParam +0x114~+0x120.</summary>
    public static readonly Dictionary<int, int[]> Equipment = [];

    public static int[] Slots(int charId) => Equipment.TryGetValue(charId, out var s) ? s : Equipment[charId] = [-1, -1, -1, -1];

    /// <summary>끼기: 가방에서 빼서 칸에 넣고, 옛것은 가방으로(원본처럼 맞바꿈).</summary>
    public static bool Equip(int charId, int itemId)
    {
        if (Data.ItemNames.Get(itemId) is not { } it || !it.CanWear(charId) || !Items.Remove(itemId)) return false;
        var slots = Slots(charId);
        if (slots[it.Slot] >= 0) Items.Add(slots[it.Slot]);
        slots[it.Slot] = itemId;
        return true;
    }

    /// <summary>신기 진화로 바뀐 신기 차례(캐릭터마다 진화한 신기 id 목록) — 세이브에 남는다.</summary>
    public static readonly Dictionary<int, List<int>> Evolutions = [];

    private static readonly Lazy<Dictionary<int, Data.Singi>> SingiTable = new(Data.BattleTables.LoadSingi);

    /// <summary>진화 사건 0x5207db56 이 고를 수 있는 다음 신기들. 끝 단계가 뿌리로 돌아가는 원본 버그는 막는다(이미 거친 신기면 빼기).</summary>
    public static List<Data.Singi> NextSingi(Data.CharParam basic)
    {
        var path = Evolutions.GetValueOrDefault(basic.Id) ?? [];
        int cur = path.Count > 0 ? path[^1] : basic.Singi;
        var seen = new HashSet<int>(path) { basic.Singi };
        if (!SingiTable.Value.TryGetValue(cur, out var now)) return [];
        return new[] { now.Child1, now.Child2 }.Where(i => i >= 0 && !seen.Contains(i) && SingiTable.Value.ContainsKey(i)).Select(i => SingiTable.Value[i]).ToList();
    }

    public static void Evolve(int charId, int singiId)
    {
        if (!Evolutions.TryGetValue(charId, out var list)) Evolutions[charId] = list = [];
        list.Add(singiId);
        // 칼린츠가 진화하면 아도라(3)도 능력치·친화를 같이 받는다(기술은 아님) — 원본 그대로
        if (charId == 0)
        {
            if (!AdoraShare.TryGetValue(3, out var a)) AdoraShare[3] = a = [];
            a.Add(singiId);
        }
    }

    /// <summary>사건 810 [캐릭터, 기술] 으로 배운 기술(0x5207f602 → LearnAbility 0x520b1c5d — 기술 10칸 중 빈 칸에). 743·772~774 에서 아도라(3)가 칼린츠 진화 뒤 15~18 을 배움.</summary>
    public static readonly Dictionary<int, List<int>> Learned = [];

    /// <summary>사건 800 [캐릭터, 칸 13, 값] 으로 바꾼 대화 얼굴(CharData +0x9e) — 346 에서 아도라(3) 를 293 으로.</summary>
    public static readonly Dictionary<int, int> FaceOverride = [];

    public static void Learn(int charId, int abilityId)
    {
        if (!Learned.TryGetValue(charId, out var l)) Learned[charId] = l = [];
        if (!l.Contains(abilityId)) l.Add(abilityId);
    }

    /// <summary>아도라가 칼린츠 진화로 받는 몫.</summary>
    public static readonly Dictionary<int, List<int>> AdoraShare = [];

    /// <summary>원본 세이브에서 읽은 캐릭터(진화가 끝난 절대값, 장비는 뺀 기본값) — 있으면 CharData 대신 쓴다.</summary>
    public static readonly Dictionary<int, Data.CharParam> Overrides = [];

    public static Data.CharParam Effective(Data.CharParam c0)
    {
        if (Overrides.TryGetValue(c0.Id, out var ov)) c0 = ov;
        var c = c0.WithEquipment([]);   // 복사
        foreach (int sid in Evolutions.GetValueOrDefault(c.Id) ?? []) if (SingiTable.Value.TryGetValue(sid, out var s)) c.Evolve(s);
        foreach (int sid in AdoraShare.GetValueOrDefault(c.Id) ?? [])
            if (SingiTable.Value.TryGetValue(sid, out var s)) { int keep = c.Singi; c.Evolve(s with { Learn = [] }); c.Singi = keep; }
        foreach (int ab in Learned.GetValueOrDefault(c.Id) ?? []) if (!c.Abilities.Contains(ab) && c.Abilities.Count < 10) c.Abilities.Add(ab);
        return WithItems(c);
    }

    private static Data.CharParam WithItems(Data.CharParam c)
    {
        var w = WithItemsRaw(c);
        if (AffinityBonus.TryGetValue(c.Id, out var b))
            for (int k = 0; k < 7; k++) w.Affinity[k] += b[k];
        return w;
    }

    private static Data.CharParam WithItemsRaw(Data.CharParam c) =>
        c.WithEquipment(Slots(c.Id).Where(i => i >= 0).Select(i => Data.ItemNames.Get(i)).OfType<Data.ItemData>());

    /// <summary>마지막 사건 전투 결과 — 전투 필드의 조건 302(a0 1 = 이김, 0 = 짐, 추정)가 본다.</summary>
    public static bool? LastBattleWon;

    /// <summary>조우 셈 C(틱)와 카운트다운 — 원본은 전역 객체 +0x10a0/+0x10a4 라 필드를 옮겨도 이어지고, 세이브를 불러올 때만 0 으로(0x5207021e).
    /// 카운트다운 동안은 C 가 멈추고, 전투가 걸려도 C 를 되돌리지 않는다(0x5206e07d).</summary>
    public static float EncounterTicks, EncounterCountdown = -1;

    /// <summary>놀이 시간(ms) — 필드·전투에서 센다. 원본 세이브 +0x1a 와 같은 단위(확정, 0x5206fd5b).</summary>
    public static double PlayMs;

    /// <summary>Party 쪽에서 출전을 끈 파티원(CharData id) — 원본 「PARTY - BATTLE MEMBER ALIGNMENT」, 최대 4인 출전(FrameData 233).</summary>
    public static readonly HashSet<int> Benched = [];

    /// <summary>전투에 나갈 넷 — 파티 차례대로, 출전을 끈 사람은 빼고.</summary>
    public static int[] BattleMembers() => Party.Where(id => !Benched.Contains(id)).Take(4).DefaultIfEmpty(Party.FirstOrDefault()).ToArray();

    /// <summary>카르타 점수 7칸(地火風暗水電光) — 전투에서 얻고 상태 화면 Carta 쪽에서 친화에 나눠 준다. 새 게임 40씩(분석-전투결과).</summary>
    public static readonly int[] CartaPoints = [40, 40, 40, 40, 40, 40, 40];

    /// <summary>카르타 점수로 더한 실제 친화(캐릭터 → 속성 7칸).</summary>
    public static readonly Dictionary<int, int[]> AffinityBonus = [];

    /// <summary>◀: 점수 하나로 그 속성 친화 +1(기본 친화까지만), ▶: 되돌려 점수로(0x52081ec3).</summary>
    public static bool ShiftAffinity(Data.CharParam effective, int element, int dir)
    {
        if (element is < 1 or > 7) return false;
        int e = element - 1;
        if (!AffinityBonus.TryGetValue(effective.Id, out var b)) AffinityBonus[effective.Id] = b = new int[7];
        if (dir > 0)
        {
            if (CartaPoints[e] <= 0 || effective.Affinity[e] >= effective.Carta[e]) return false;
            CartaPoints[e]--; b[e]++;
        }
        else
        {
            if (b[e] <= 0) return false;
            b[e]--; CartaPoints[e]++;
        }
        return true;
    }

    public static int Switch(int id) => GlobalSwitches.GetValueOrDefault(id);
    public static void SetSwitch(int id, int v) => GlobalSwitches[id] = v;
}
