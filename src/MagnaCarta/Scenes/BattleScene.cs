using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Render;

namespace MagnaCarta.Scenes;

/// <summary>
/// 지하수로 던전의 전투 — 로프마·칼린츠·첼시 대 Mon 0032(자보라 ×2 + 캐노펀), 무대 MAP 0277(분석-던전).
/// 원본에서 옮긴 것(분석-전투): 파생 능력치, 피해 식(0 이하면 10), 턴(틱 % WTP == 0), TP 30 · 행동마다 UseTP×10,
/// 연결공격 타이밍(첫 입력 뒤 10~15 · 20~25 프레임), 필살 단계(콤보 성공마다 +1, 3 이면 오버크래쉬), 적 HP ±10%.
/// 지은 것: 진형·이동 속도·카메라·적 AI(무작위 대상, 쓸 수 있는 기술 중 무작위)·전투 시퀀스 번호 고르기·화면 배치.
/// </summary>
internal sealed partial class BattleScene : IScene
{
    public const int StageMap = 277, MonsterGroup = 32;
    public static readonly int[] PartyIds = [2, 0, 1];          // 로프마 · 칼린츠 · 첼시
    /// <summary>
    /// 지하수로에 닿을 즈음의 신기 진화 단계 — 지은 값. 처음 능력치로는 물리 공격이 캐노펀 AC 400 에 막혀 10 만 들어가므로
    /// 신기를 첫째 갈래로 두 번 진화시킨 몸으로 싸운다(성장 식은 원본 그대로).
    /// </summary>
    public const int EvolveSteps = 2;

    // 전투 몸(.amd 칸 2) 시퀀스 — 번호 뜻은 그림으로 짐작(분석-렌더링-3D §5). 지은 값
    private const int SeqIdle = 0, SeqMove = 2, SeqHit = 4, SeqAttack = 5, SeqDown = 7, SeqSkill = 8, SeqCombo2 = 9, SeqCombo3 = 10, SeqOverCrash = 12;
    private const float MoveSpeed = 3.2f;                       // 단위/틱 — 지은 값
    private const int ComboStep = 10, ComboWindow = 6;           // ValueData 35 · 36

    private sealed class Fighter
    {
        public required CharParam P;
        public required CharacterRenderer R;
        public bool Enemy;
        public Vector3 Home, Pos;
        public float Yaw, HomeYaw;
        public int Hp, MaxHp, Tp, Apocalypse;
        public int Motion;
        public float MotionTick;
        public bool Loop = true;
        public bool Alive => Hp > 0;
        public float Flash;

        public void Play(int seq, bool loop)
        {
            if (!R.HasSequence(seq)) seq = R.HasSequence(SeqAttack) && seq is SeqCombo2 or SeqCombo3 or SeqOverCrash or SeqSkill ? SeqAttack : SeqIdle;
            Motion = seq; Loop = loop; MotionTick = 0;
        }
    }

    private sealed class Action
    {
        public required Fighter Actor, Target;
        public required Ability Ability;
        public required AttackData Attack;
        public int Seq;
        public int Stage;           // 0 다가감 · 1 휘두름 · 2 끝
        public bool Applied;
        public Affect? Script;      // 원본 연출(EffScr) — 있으면 그 시간 줄대로 모션·때림
        public float StrikeTick;
        public int StepIndex;
    }

    private record Popup(Vector3 At, string Text, (byte, byte, byte) Color) { public float Age; }

    private enum Phase { Intro, NextTurn, Command, Acting, Returning, Won, Lost }

    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly Overlay _ui;
    private readonly MapRenderer _map;
    private readonly Dictionary<int, AttackData> _attacks;
    private readonly Dictionary<int, Ability> _abilities;
    private readonly List<Fighter> _fighters = [];
    private readonly List<Popup> _popups = [];
    private readonly Random _rng = new();

    private Phase _phase = Phase.Intro;
    private float _phaseTime;
    private int _tick, _turnIndex = -1;
    private Fighter? _actor;
    private int _target;
    private Action? _action;
    private int _enemyActions;
    private string _message = "";
    private float _messageTime;

    // 연결공격 (분석-전투 §2-4)
    private bool _comboOn;
    private float _comboFrames;
    private int _comboN;
    private bool _comboQueued;
    private bool _endAfter;
    private bool _auto;              // F9 자동 전투(패치판 덧붙임): 아군도 스스로 고른다          // 행동 중에 Space — 이 행동이 끝나면 턴 끝

    private Vector3 _camTarget, _camEye;
    private int _pendingMovie = -1;
    private MovieScene? _overlayMovie;
    private readonly List<Field.Cctv> _cctv = [];
    private Vector3[] _cctvPos = [];
    private int _cctvActive = -1;
    private float _lastTicks;

    /// <summary>필드 StepCctv 와 같은 식: 가장 가까운 카메라가 줄 길이를 지키며 상자 안에서 따라간다.</summary>
    private Vector3 StepBattleCctv(Vector3 target)
    {
        int best = 0;
        for (int i = 1; i < _cctvPos.Length; i++)
            if (Vector3.Distance(_cctvPos[i], target) < Vector3.Distance(_cctvPos[best], target)) best = i;
        if (best != _cctvActive)
        {
            if (_cctvActive >= 0 && _cctv[best].Smooth == 1) _cctvPos[best] = Vector3.Clamp(_cctvPos[_cctvActive], _cctv[best].Min, _cctv[best].Max);
            _cctvActive = best;
        }
        var cam = _cctv[best];
        var d = target - _cctvPos[best];
        float l = d.Length();
        if (l > 1e-3f && MathF.Abs(l - cam.Rope) >= 1)
        {
            float k = 1 - MathF.Pow(1 - 1f / cam.Divisor, Math.Max(_lastTicks, 0.01f));
            _cctvPos[best] = Vector3.Clamp(_cctvPos[best] + d / l * (l - cam.Rope) * k, cam.Min, cam.Max);
        }
        return _cctvPos[best];
    }

    /// <summary>전투를 짜는 값 — 무대 MAP, Mon 무리, 아군 CharData id, 신기 진화 단계, 전투 음악, 이기면 돌아갈 장면.</summary>
    public sealed record Setup(int Stage, int Group, int[] Party, int Evolve, int Music, IScene? ReturnTo, int ReturnMusic = -1)
    {
        /// <summary>사건 전투(fld 776~825): 아군·적 자리를 B 묶음에서 바로 — (CharData id, 자리). 있으면 Group 대신 쓴다.</summary>
        public List<(int Id, Vector3 Pos)>? Allies, Foes;
        /// <summary>끝나면 부르는 것(이겼나) — 있으면 ReturnTo 대신.</summary>
        public Func<bool, IScene>? After;
        /// <summary>전투 필드(597~601 · 776~825) — 그 fld D 감시 카메라로 따라간다(분석-전투카메라).</summary>
        public int BattleField = -1;
    }

    /// <summary>시험 전투: 지하수로 캐노펀(w-7).</summary>
    // 확인용: MC_TEST_PARTY=5,0 처럼 쉼표로 동료 id 를 주면 --battle 파티를 바꾼다
    public static Setup Test => new(StageMap, MonsterGroup,
        Environment.GetEnvironmentVariable("MC_TEST_PARTY") is { Length: > 0 } tp ? tp.Split(',').Select(int.Parse).ToArray() : PartyIds, EvolveSteps, 76, null) { BattleField = 779 };   // 무대 277 을 쓰는 원본 전투 필드(Mon 0124) — 그 감시 카메라로 아군이 화면에 들어온다

    private readonly Setup _setup;
    private bool _returned;
    public IScene? ReturnTo => _setup.ReturnTo;

    public BattleScene(Gfx gfx, ScreenImage screen) : this(gfx, screen, Test) { }

    public BattleScene(Gfx gfx, ScreenImage screen, Setup setup)
    {
        _setup = setup;
        _gfx = gfx;
        _screen = screen;
        _ui = new Overlay(gfx, screen);
        _map = new MapRenderer(gfx, WorldMap.Load(setup.Stage));
        if (setup.BattleField >= 0 && McFiles.Exists("Field", setup.BattleField, "fld"))
            try { _cctv.AddRange(Field.Load(setup.BattleField).Cameras); } catch (InvalidDataException) { }
        _cctvPos = _cctv.Select(c => c.Start).ToArray();
        Audio.Music.Play(setup.Music);
        _attacks = BattleTables.LoadAttacks();
        _abilities = BattleTables.LoadAbilities();
        var chars = CharParam.LoadAll().ToDictionary(c => c.Id);
        var singi = BattleTables.LoadSingi();
        foreach (int id in setup.Party)
            for (int k = 0; k < setup.Evolve; k++)
                if (singi.TryGetValue(chars[id].Singi, out var now) && singi.TryGetValue(now.Child1, out var next) && next.Id != now.Id)
                    chars[id].Evolve(next);
        var cache = new Dictionary<int, CharacterMesh>();

        CharacterRenderer Body(CharParam c)
        {
            var slots = Amd.Load(c.Amd);
            int model = slots.TryGetValue(2, out var m2) && m2 > 0 ? m2 : slots[0];  // 칸 2 = 전투 몸 — 0 이면 빈 칸(칼린츠(아이) amd 25), 필드 몸으로
            if (!cache.TryGetValue(model, out var mesh)) cache[model] = mesh = CharacterMesh.Load(model);
            return new CharacterRenderer(gfx, mesh);
        }

        // 진형: 아군은 −Y 쪽, 적은 +Y 쪽에서 마주 본다 (지은 배치 — 무작위 전투의 원본 자리는 못 땄다)
        var party = setup.Allies?.Select(a => a.Id).ToArray() ?? setup.Party;
        bool real = setup.ReturnTo != null || setup.After != null;
        for (int i = 0; i < party.Length; i++)
        {
            if (!chars.ContainsKey(party[i])) continue;
            var c = real ? GameState.Effective(chars[party[i]]) : chars[party[i]];   // 실제 전투는 진화·장비를 더한다
            var home = setup.Allies?[i].Pos ?? new Vector3((i - (party.Length - 1) / 2f) * 70f, -170f - (i == 1 ? 25 : 0), 0);
            var f = new Fighter { P = c, R = Body(c), Home = home };
            f.HomeYaw = MathF.PI;   // 정면 −Y 를 +Y 로
            // 손에 든 신기: 처음 신기 모델(CharData 신기) → 지금 신기 모델(진화·원본 세이브) — Sgi +0x6c
            if (singi.TryGetValue(chars[party[i]].Singi, out var s0) && singi.TryGetValue(c.Singi, out var s1) && s0.Model > 0 && s1.Model > 0 && s0.Model != s1.Model)
                f.R.AttachSwap[s0.Model] = s1.Model;
            _fighters.Add(f);
        }
        var group = setup.Foes?.Select(e => e.Id).ToList()
            ?? (!real && Environment.GetEnvironmentVariable("MC_TEST_FOES") is { Length: > 0 } tf ? tf.Split(',').Select(int.Parse).ToList() : null)   // 확인용: --battle 적 CharData id
            ?? BattleTables.LoadGroup(setup.Group).Take(5).ToList();   // 원본은 앞 5칸만 읽는다(분석-몬스터)
        for (int i = 0; i < group.Count; i++)
        {
            if (!chars.TryGetValue(group[i], out var c)) continue;
            float x = (i - (group.Count - 1) / 2f) * 110f;
            var home = setup.Foes?[i].Pos ?? new Vector3(x, 170f + (c.Kind == 5 ? 40 : 0), 0);
            var f = new Fighter { P = c, R = Body(c), Enemy = true, Home = home };
            f.HomeYaw = 0;
            _fighters.Add(f);
        }
        if (setup.Allies != null || setup.Foes != null)
        {
            var foeMid = _fighters.Where(f => f.Enemy).Select(f => f.Home).DefaultIfEmpty().Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(1, _fighters.Count(f => f.Enemy));
            var allyMid = _fighters.Where(f => !f.Enemy).Select(f => f.Home).DefaultIfEmpty().Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(1, _fighters.Count(f => !f.Enemy));
            foreach (var f in _fighters)
            {
                var to = (f.Enemy ? allyMid : foeMid) - f.Home;
                f.HomeYaw = MathF.Atan2(to.X, -to.Y);
            }
        }
        foreach (var f in _fighters)
        {
            if ((_map.Ground.HeightAt(f.Home.X, f.Home.Y, f.Home.Z + 400) ?? _map.Ground.HeightAtAny(f.Home.X, f.Home.Y, f.Home.Z + 400)) is { } z) f.Home.Z = z;   // 무대마다 바닥 높이가 달라(성지 용암 등) 넉넉히 위에서 찾는다
            f.Pos = f.Home; f.Yaw = f.HomeYaw;
            f.MaxHp = f.P.MaxHp;
            if (f.Enemy)
            {
                // 적 체력 흔들기: v = HP×0.2, HP − rand%v + v/2 (분석-전투 결론표)
                int v = Math.Max(1, (int)(f.MaxHp * 0.2f));
                f.MaxHp = f.MaxHp - _rng.Next(v) + v / 2;
            }
            f.Hp = f.MaxHp;
            f.Tp = f.P.Tp;
            if (!f.Enemy && int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_APO"), out int apo)) f.Apocalypse = apo;   // 확인용
            f.Play(SeqIdle, true);
            f.MotionTick = _rng.Next(40);
        }
        _target = _fighters.FindIndex(f => f.Enemy && f.P.Kind == 5);
        if (_target < 0) _target = _fighters.FindIndex(f => f.Enemy);
        _camTarget = new Vector3(0, 0, 30);
        _camEye = new Vector3(0, -360, 155);
        Say(setup.ReturnTo == null ? "지하수로 - 적이 나타났다!" : "적이 나타났다!");
    }

    private IEnumerable<Fighter> Party => _fighters.Where(f => !f.Enemy);
    private IEnumerable<Fighter> Enemies => _fighters.Where(f => f.Enemy);

    private void Say(string text) { _message = text; _messageTime = 2.2f; }

    // ── 진행 ────────────────────────────────────────────────────────────────

    public IScene? Update(float seconds, Input input)
    {
        // 연출 508 동영상(분석-전투연출) — 전투는 그대로 돌고 그 위에 더하기로 겹쳐 튼다
        if (_pendingMovie >= 0) { _overlayMovie?.Dispose(); _overlayMovie = new MovieScene(_gfx, _screen, _pendingMovie, () => this) { ReturnTo = this }; _pendingMovie = -1; }
        if (_overlayMovie != null) { _overlayMovie.Update(seconds, input); if (_overlayMovie.Finished) { _overlayMovie.Dispose(); _overlayMovie = null; } }

        if (input.Pressed(Input.Escape)) return new TitleScene(_gfx, _screen);
        if (input.Pressed(0x78)) { _auto = !_auto; Say(_auto ? "자동 전투 켬 (F9)" : "자동 전투 끔"); }
        float ticks = seconds * Timing.TicksPerSecond;
        _lastTicks = ticks;
        GameState.PlayMs += seconds * 1000;
        _phaseTime += seconds;
        _messageTime -= seconds;
        foreach (var f in _fighters)
        {
            float before = f.MotionTick;
            f.MotionTick += ticks;
            foreach (var cmd in f.R.CommandsBetween(f.Motion, before, f.MotionTick))
            {
                if (cmd.Type == 10) Audio.SoundEffects.Play(cmd.B0);
                // 갈래 9 = 전투 때림(0x5208eaef → 0x401): 연출에 800 이 없는 기술(칼린츠 일반공격 등)의 때림 시각
                if (cmd.Type == 9 && _action is { } act && act.Actor == f && act.Script is { Hits: 0 } && act.Target.Alive) { Apply(act); act.Applied = true; }
            }
            f.Flash = Math.Max(0, f.Flash - seconds);
            int len = f.R.SequenceTicks(f.Motion);
            if (!f.Loop && len > 0 && f.MotionTick >= len)
            {
                if (!f.Alive) f.MotionTick = len - 0.01f;    // 쓰러진 채로
                else if (f.Motion != SeqMove) f.Play(SeqIdle, true);
            }
        }
        foreach (var p in _popups) p.Age += seconds;
        UpdateEffects(ticks);
        _popups.RemoveAll(p => p.Age > 1.3f);

        if (Environment.GetEnvironmentVariable("MC_BATTLE_LOG") is { Length: > 0 } log && (int)(_phaseTime * 2) != (int)((_phaseTime - seconds) * 2))
            File.AppendAllText(log, $"{_phase} t={_phaseTime:F1} actor={_actor?.P.Name} tp={_actor?.Tp} act={_action?.Ability.Name} stage={_action?.Stage} motion={_actor?.Motion} mt={_actor?.MotionTick:F0} combo={_comboOn}/{_comboN}/{_comboQueued} fx={_action?.Attack.Affect} st={_action?.StrikeTick:F0}/{_action?.Script?.End} step={_action?.StepIndex}/{_action?.Script?.Steps.Count} pos={_actor?.Pos:F0} enemy={_actor?.Enemy} {_lastMark}" + Environment.NewLine);
        switch (_phase)
        {
            case Phase.Intro:
                if (_phaseTime > 1.6f || input.Pressed(Input.Enter)) Set(Phase.NextTurn);
                break;
            case Phase.NextTurn:
                NextActor();
                break;
            case Phase.Command:
                Command(input);
                break;
            case Phase.Acting:
                Act(ticks, input);
                break;
            case Phase.Returning:
                if (MoveToward(_actor!, _actor!.Home, ticks)) { _actor.Yaw = _actor.HomeYaw; _actor.Play(SeqIdle, true); Set(Phase.NextTurn); }
                break;
            case Phase.Won or Phase.Lost:
                if (_phaseTime > 1.5f && (input.Pressed(Input.Enter) || input.Pressed(Input.Space) || _auto && _phaseTime > 3f && Environment.GetEnvironmentVariable("MC_HOLD_RESULT") is null))
                {
                    // 이기면 원래 필드로(전투 필드 597~601 의 사건 302 a0=1), 지면 시작 화면으로(a0=0 → 13 추정). 시험 전투는 다시
                    if (_setup.After is { } after) { Audio.Music.Stop(); return after(_phase == Phase.Won); }
                    if (_setup.ReturnTo is { } back)
                    {
                        if (_phase == Phase.Won) { _returned = true; Audio.Music.Play(_setup.ReturnMusic); return back; }
                        return new TitleScene(_gfx, _screen);
                    }
                    return new BattleScene(_gfx, _screen, _setup);
                }
                break;
        }
        return this;
    }

    private void Set(Phase p) { _phase = p; _phaseTime = 0; }

    /// <summary>이번 전투에서 얻은 카르타 점수(속성 7칸) — 결과 창(FrameData 245)에 보인다.</summary>
    private readonly int[] _cartaGain = new int[7];

    /// <summary>
    /// 적이 쓰러질 때마다 무작위 속성(rand%7)에 점수(0x5208ce9f): 갈래 0 일반 = 50% 로 1~5 · 1/2/3 = 1 · 4 = 10% 로 1 · 5 보스 = 10.
    /// 원본은 쓰러질 때 바로 더하지만 패치판은 이긴 뒤 한꺼번에. 시험 전투(필드 밖)는 점수를 남기지 않는다.
    /// </summary>
    private void GrantCarta()
    {
        foreach (var e in Enemies)
        {
            int v = e.P.Kind switch
            {
                0 => _rng.Next(2) == 0 ? _rng.Next(1, 6) : 0,
                1 or 2 or 3 => 1,
                4 => _rng.Next(10) == 0 ? 1 : 0,
                5 => 10,
                _ => 0,
            };
            if (v > 0) _cartaGain[_rng.Next(7)] += v;
        }
        if (_setup.ReturnTo != null || _setup.After != null)
            for (int i = 0; i < 7; i++) GameState.CartaPoints[i] += _cartaGain[i];
    }

    /// <summary>MCBtlDealer 0x520a86f5: 지금 배우 다음부터 살아 있고 틱 % WTP == 0 인 첫 배우. 없으면 틱 +1.</summary>
    private void NextActor()
    {
        if (!Enemies.Any(f => f.Alive)) { Set(Phase.Won); Say("승리!"); foreach (var f in Party.Where(f => f.Alive)) f.Play(SeqIdle, true); GrantCarta(); return; }
        if (!Party.Any(f => f.Alive)) { Set(Phase.Lost); Say("전멸했다..."); return; }
        for (int guard = 0; guard < 1000; guard++)
        {
            _turnIndex++;
            if (_turnIndex >= _fighters.Count) { _turnIndex = 0; _tick++; }
            var f = _fighters[_turnIndex];
            if (!f.Alive || _tick % Math.Max(f.P.Wtp, 1) != 0) continue;
            _actor = f;
            f.Tp = f.P.Tp;                 // 자기 턴 시작에 TP = 최대(30)
            if (f.Enemy) { _enemyActions = 0; EnemyThink(); }
            else
            {
                if (_target < 0 || !_fighters[_target].Alive) _target = _fighters.FindIndex(e => e.Enemy && e.Alive);
                Set(Phase.Command);
            }
            return;
        }
    }

    // 능력이 없는 인물(칼린츠(아이) 22 등)은 능력 0 으로 — 원본 동작은 모름(지은 값)
    private Ability Normal(Fighter f) => _abilities[f.P.Abilities.Count > 0 ? f.P.Abilities[0] : 0];

    private Ability? Skill(Fighter f) =>
        f.P.Abilities.Skip(1).Select(a => _abilities.GetValueOrDefault(a)).FirstOrDefault(a => a != null && _attacks.ContainsKey(a.Attack));

    private void Command(Input input)
    {
        var a = _actor!;
        if (_auto && _phaseTime > 0.3f) { AutoCommand(a); return; }
        if (input.Pressed(Input.Left) || input.Pressed('A')) CycleTarget(-1);
        if (input.Pressed(Input.Right) || input.Pressed('D') || input.Pressed(0x09)) CycleTarget(1);
        if (input.Pressed(Input.Space)) { EndTurn(); return; }
        if (a.Tp < 10) { EndTurn(); return; }

        if (input.Pressed('Z') || input.Pressed(Input.Enter))
        {
            // 첫 입력: T=0, n=1, 연결공격1
            var normal = Normal(a);
            var first = normal.Links.Length > 0 ? _abilities[normal.Links[0]] : normal;
            if (Start(a, _fighters[_target], first, SeqAttack))
            {
                _comboOn = normal.Links.Length > 0; _comboFrames = 0; _comboN = 1; _comboQueued = false;
            }
        }
        else if (input.Pressed('X') && Skill(a) is { } skill)
        {
            var att = _attacks[skill.Attack];
            var target = att.Kind == 3 ? Party.Where(f => f.Alive).MinBy(f => f.Hp / (float)f.MaxHp)! : _fighters[_target];
            Start(a, target, skill, SeqSkill);
        }
        else if (input.Pressed('C') && a.Apocalypse >= 3 && Normal(a).Apocalypse >= 0)
        {
            if (Start(a, _fighters[_target], _abilities[Normal(a).Apocalypse], SeqOverCrash)) { a.Apocalypse = 0; Audio.SoundEffects.Play(0x245); }   // 필살 소리 0x245(등급 2 특수 결과 소리 — 쓰임 추정)
        }
    }

    /// <summary>자동: 다친 동료가 있으면 회복, 필살이 차면 오버크래쉬, TP 가 되면 기술, 아니면 공격(연결은 창 가운데에 누른다).</summary>
    private void AutoCommand(Fighter a)
    {
        var skill = Skill(a);
        var att = skill != null ? _attacks[skill.Attack] : null;
        var hurt = Party.Where(f => f.Alive).MinBy(f => f.Hp / (float)f.MaxHp);
        _target = _fighters.Select((f, i) => (f, i)).Where(x => x.f.Enemy && x.f.Alive).OrderBy(x => x.f.Hp).Select(x => x.i).DefaultIfEmpty(-1).First();
        if (_target < 0) { EndTurn(); return; }
        if (skill != null && att!.Kind == 3 && hurt != null && hurt.Hp < hurt.MaxHp * 0.5f && a.Tp >= att.UseTp * 10) { Start(a, hurt, skill, SeqSkill); return; }
        if (a.Apocalypse >= 3 && Normal(a).Apocalypse >= 0 && Start(a, _fighters[_target], _abilities[Normal(a).Apocalypse], SeqOverCrash)) { a.Apocalypse = 0; return; }
        if (skill != null && att!.Kind != 3 && a.Tp >= att.UseTp * 10) { Start(a, _fighters[_target], skill, SeqSkill); return; }
        var normal = Normal(a);
        var first = normal.Links.Length > 0 ? _abilities[normal.Links[0]] : normal;
        if (Start(a, _fighters[_target], first, SeqAttack)) { _comboOn = normal.Links.Length > 0; _comboFrames = 0; _comboN = 1; _comboQueued = false; }
        else EndTurn();
    }

    private void CycleTarget(int dir)
    {
        var alive = _fighters.Select((f, i) => (f, i)).Where(x => x.f.Enemy && x.f.Alive).Select(x => x.i).ToList();
        if (alive.Count == 0) return;
        int k = alive.IndexOf(_target);
        _target = alive[((k < 0 ? 0 : k + dir) % alive.Count + alive.Count) % alive.Count];
    }

    private bool Start(Fighter actor, Fighter target, Ability ability, int seq)
    {
        if (!_attacks.TryGetValue(ability.Attack, out var att)) return false;
        int cost = att.UseTp * 10;
        if (actor.Tp < cost) { if (!actor.Enemy) Say("TP 가 모자란다"); return false; }
        actor.Tp = Math.Max(0, actor.Tp - cost);          // 0x5208f4e8
        _action = new Action { Actor = actor, Target = target, Ability = ability, Attack = att, Seq = seq };
        Set(Phase.Acting);
        if (!actor.Enemy || ability.Name.Length > 0) Say(Clean(ability.Name));
        return true;
    }

    private static string Clean(string name) => name.Replace("_", " ");

    private void EndTurn()
    {
        _comboOn = false;
        _endAfter = false;
        Set(Phase.Returning);
    }

    private void Act(float ticks, Input input)
    {
        var x = _action!;
        var a = x.Actor;
        if (_comboOn) _comboFrames += ticks;
        if (_comboOn && (_comboFrames >= 2 * ComboStep + ComboWindow || _comboN >= 3)) _comboOn = false;

        // 연결공격 입력 (n<3): n×10 ≤ T < n×10+6 이면 성공
        if (!a.Enemy && input.Pressed(Input.Space)) _endAfter = true;
        bool autoPress = _auto && _comboOn && !a.Enemy && !_comboQueued && _comboFrames >= _comboN * ComboStep + 2 && _comboFrames < _comboN * ComboStep + ComboWindow;
        if (_comboOn && !a.Enemy && (autoPress || input.Pressed('Z') || input.Pressed(Input.Enter)))
        {
            bool ok = _comboFrames >= _comboN * ComboStep && _comboFrames < _comboN * ComboStep + ComboWindow && a.Tp >= 10;
            _comboN++;
            if (ok)
            {
                _comboQueued = true;
                a.Apocalypse = Math.Min(3, a.Apocalypse + 1);            // 최대 Value 56 = 3
                Say(a.Apocalypse >= 3 ? "연결 성공! 오버크래쉬 가능 (C)" : "연결 성공!");
            }
            else { Say("타이밍 실패"); _comboOn = false; Audio.SoundEffects.Play(0x238); }   // 실패음 0x238 (분석-전투 §2-4)
        }

        switch (x.Stage)
        {
            case 0:
            {
                float reach = Math.Max(30f, x.Attack.Range * 10f) + (x.Target.Enemy != a.Enemy ? 18f : 0f);
                if (x.Target == a || x.Attack.Kind == 3 && x.Target != a && Vector2.Distance(Flat(a.Pos), Flat(x.Target.Pos)) <= reach)
                    Face(a, x.Target);
                var to = Flat(x.Target.Pos) - Flat(a.Pos);
                // 닿을 거리(사거리 + 1)에 들면 휘두른다 — 딱 사거리 자리에서는 부동소수 오차로 끝없이 걷던 것을 막는다
                bool arrived = to.Length() <= reach + 1f || x.Target == a;
                if (!arrived)
                {
                    var goal = x.Target.Pos - new Vector3(Vector2.Normalize(to) * (reach - 2f), 0);
                    arrived = MoveToward(a, goal, ticks);
                }
                if (arrived)
                {
                    if (x.Target != a) Face(a, x.Target);
                    x.Script = Affect.Load(int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_AFFECT"), out int taf) && !a.Enemy ? taf : x.Attack.Affect);   // 확인용: 아군 연출을 그 번호로
                    if (x.Script is not { Steps.Count: > 0 }) x.Script = null;
                    a.Play(x.Seq, false);   // 연출이 첫 모션을 바로 바꾼다
                    x.Stage = 1;
                }
                break;
            }
            case 1:
            {
                int len = Math.Max(a.R.SequenceTicks(a.Motion), 12);
                bool done;
                if (x.Script is { } sc)
                {
                    // 원본 연출 시간 줄: 200 모션(묶음 0 시전자 · 1 대상), 800 때림 — 때림마다 피해 식 한 번(분석-전투연출)
                    x.StrikeTick += ticks;
                    while (x.StepIndex < sc.Steps.Count && sc.Steps[x.StepIndex].Tick <= x.StrikeTick)
                    {
                        var st = sc.Steps[x.StepIndex++];
                        if (st.Op == 200) { var who = st.Group == 1 ? x.Target : a; if (who.Alive) who.Play(st.Value, false); }
                        else if (st.Op == 800 && x.Target.Alive) { Apply(x); x.Applied = true; }
                        else if (st.Op == 300) SpawnEffect(sc, st, a, x.Target);
                        else if (st.Op == 500) StartFxCamera(sc, st, a, x.Target);
                        else if (st.Op is 310 or 306 or 303) EffectCommand(sc, st, a, x.Target);
                        else if (st.Op == 508)
                        {
                            bool has = sc.Movies.TryGetValue(st.Group, out int mov);
                            if (Environment.GetEnvironmentVariable("MC_MOVIE_LOG") is { Length: > 0 } ml)
                                File.AppendAllText(ml, $"전투 508: 표2[{st.Group}]={(has ? mov : -1)} avail={MovieScene.Available} file={(has ? MovieScene.FileFor(mov) : null)}" + Environment.NewLine);
                            if (has && MovieScene.Available && MovieScene.FileFor(mov) != null) _pendingMovie = mov;   // 이 틀이 끝나면 동영상으로 갔다가 전투로 돌아온다
                        }
                    }
                    // 때림이 없는 연출(칼린츠 일반공격 13 · 오버크래쉬 255)은 끝에 한 번 때린다(패치판)
                    if (!x.Applied && sc.Hits == 0 && x.StrikeTick >= sc.End) { Apply(x); x.Applied = true; }   // 갈래 9 도 없으면 끝에 한 번
                    done = x.StrikeTick >= Math.Max(sc.End, 12);
                }
                else
                {
                    if (!x.Applied && a.MotionTick >= len * 0.45f) { Apply(x); x.Applied = true; }
                    done = a.MotionTick >= len || a.Motion == SeqIdle;
                }
                // 연결이 들어왔으면 지금 타를 끊고 다음 타로
                if (_comboQueued && x.Applied && a.MotionTick >= len * 0.55f) done = true;
                if (!done) break;
                if (!x.Target.Alive && x.Target.Enemy != a.Enemy) _target = _fighters.FindIndex(e => e.Enemy && e.Alive);
                if (_comboQueued && _comboN - 2 < Normal(a).Links.Length - 1 && _target >= 0)
                {
                    _comboQueued = false;
                    var link = _abilities[Normal(a).Links[_comboN - 1]];
                    Start(a, _fighters[_target], link, _comboN == 2 ? SeqCombo2 : SeqCombo3);
                    break;
                }
                _comboQueued = false;
                x.Stage = 2;
                _action = null;
                EndActionEffects();
                if (!Enemies.Any(f => f.Alive) || !Party.Any(f => f.Alive)) { Set(Phase.Returning); break; }
                if (a.Enemy) EnemyThink();
                else if (a.Tp >= 10 && !_endAfter) { a.Play(SeqIdle, true); Set(Phase.Command); }
                else EndTurn();
                break;
            }
        }
    }

    private void Apply(Action x)
    {
        var (heal, v) = BattleTables.Damage(x.Attack, x.Actor.P, x.Target.P);
        var t = x.Target;
        if (heal)
        {
            t.Hp = Math.Min(t.MaxHp, t.Hp + v);                         // 최대HP 로 자름 0x5208ca6a
            _popups.Add(new Popup(t.Pos + new Vector3(0, 0, 60), $"+{v}", (120, 240, 140)));
            return;
        }
        t.Hp = Math.Max(0, t.Hp - v);
        t.Flash = 0.25f;
        _popups.Add(new Popup(t.Pos + new Vector3(0, 0, 60), v.ToString(), t.Enemy ? ((byte)255, (byte)230, (byte)120) : ((byte)255, (byte)120, (byte)100)));
        if (t.Alive) t.Play(SeqHit, false);
        else { t.Play(SeqDown, false); Say($"{t.P.Name} 쓰러짐"); }
    }

    /// <summary>적 AI(지은 것): 쓸 수 있는 기술 가운데 무작위, 대상은 살아 있는 아군 무작위. 한 턴에 두 번까지.</summary>
    /// <summary>
    /// 적 AI (MCBtlAI, 분석-전투화면): TP 가 남는 동안 — 대상은 가장 가까운 아군(더 먼 후보마다 10% 로 바꿔 잡음),
    /// 기술은 비용(UseTP×10) ≤ TP 인 것 중 위력(rate)도 UseTP 도 더 큰 것이 나올 때만 바꿔 고름(곧 가장 센 것).
    /// 원본은 「지금 자리에서 범위 안」 인 기술만 후보로 두고 안 되면 다가간다 — 패치판은 고른 기술의 사거리까지 다가가 때린다.
    /// </summary>
    private void EnemyThink()
    {
        var a = _actor!;
        var targets = Party.Where(f => f.Alive).OrderBy(f => Vector2.Distance(Flat(f.Pos), Flat(a.Pos))).ToList();
        if (targets.Count == 0 || _enemyActions >= 6) { EndTurn(); return; }
        var target = targets[0];
        for (int i = 1; i < targets.Count; i++) if (_rng.Next(100) < 10) target = targets[i];
        Ability? best = null; AttackData? bestAtt = null;
        foreach (int id in a.P.Abilities)
        {
            if (_abilities.GetValueOrDefault(id) is not { } ab || !_attacks.TryGetValue(ab.Attack, out var at) || at.Kind == 3) continue;
            if (at.UseTp * 10 > a.Tp) continue;
            if (best == null || at.Rate > bestAtt!.Rate && at.UseTp > bestAtt.UseTp) { best = ab; bestAtt = at; }
        }
        if (best == null) { EndTurn(); return; }
        _enemyActions++;
        Start(a, target, best, bestAtt!.Kind == 0 ? SeqAttack : SeqSkill);
    }

    private static Vector2 Flat(Vector3 v) => new(v.X, v.Y);

    private static void Face(Fighter f, Fighter t)
    {
        var d = Flat(t.Pos) - Flat(f.Pos);
        if (d.LengthSquared() > 1e-4f) f.Yaw = MathF.Atan2(d.X, -d.Y);
    }

    private bool MoveToward(Fighter f, Vector3 goal, float ticks)
    {
        var d = Flat(goal) - Flat(f.Pos);
        float dist = d.Length();
        float step = MoveSpeed * ticks;
        if (dist <= step + 0.01f)
        {
            f.Pos = new Vector3(goal.X, goal.Y, f.Pos.Z);
            Snap(f);
            return true;
        }
        var n = d / dist;
        f.Pos += new Vector3(n * step, 0);
        f.Yaw = MathF.Atan2(n.X, -n.Y);
        Snap(f);
        if (f.Motion != SeqMove) f.Play(SeqMove, true);
        return false;
    }

    private void Snap(Fighter f)
    {
        if ((_map.Ground.HeightAt(f.Pos.X, f.Pos.Y, f.Pos.Z + 30) ?? _map.Ground.HeightAtAny(f.Pos.X, f.Pos.Y, f.Pos.Z + 30)) is { } z) f.Pos.Z = z;
    }

    // ── 그리기 ──────────────────────────────────────────────────────────────

    public void Draw()
    {
        _gfx.Begin(new Vortice.Mathematics.Color4(0.02f, 0.02f, 0.03f, 1));
        // 카메라: 아군 뒤 높은 곳에서 싸움터 가운데를, 행동 중에는 둘 사이를 본다(지은 값)
        Vector3 focus = new(0, 0, 30);
        if (_action is { } x) focus = (x.Actor.Pos + x.Target.Pos) / 2 + new Vector3(0, 0, 30);
        else if (_actor != null && _phase == Phase.Command) focus = (_actor.Pos * 0.6f + _fighters[Math.Max(_target, 0)].Pos * 0.4f) + new Vector3(0, 0, 30);
        if (_cctv.Count > 0 && !(_fxCam is { } fcam && fcam.Tick < fcam.Cut.Length))
        {
            // 원본: 전투 필드의 CCTV 를 필드와 같은 식으로 — 지금 차례 배우를 따라감(대상 고르기는 추정), 높이 +60 은 지은 값
            var who = _action?.Actor ?? _actor ?? _fighters[0];
            var tgt = who.Pos + new Vector3(0, 0, 60);
            _camEye = StepBattleCctv(tgt);
            _camTarget = Vector3.Lerp(_camTarget, tgt, 0.15f);
            goto camDone;
        }
        // 천장(z ≈ 221)보다 낮게 — 무대 0277 은 지붕 있는 방이다
        var eyeWant = focus + new Vector3(focus.X * 0.4f, -330, 125);
        // 벽 뒤로 나가면 당긴다
        float hit = _map.Ground.Raycast(focus, eyeWant);
        if (hit < 1) eyeWant = focus + (eyeWant - focus) * Math.Max(0.2f, hit - 0.05f);
        _camTarget = Vector3.Lerp(_camTarget, focus, 0.06f);
        _camEye = Vector3.Lerp(_camEye, eyeWant, 0.04f);
        camDone:
        var view = Matrix4x4.CreateLookAt(_camEye, _camTarget, Vector3.UnitZ);
        float camFov = MathF.PI / 4;
        var camEyeNow = _camEye;
        if (FxCameraView(out var fxView, out var fxFov, out var fxEye)) { view = fxView; camFov = fxFov; camEyeNow = fxEye; }
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(camFov, _gfx.Width / (float)Math.Max(_gfx.Height, 1), 5f, 8000f);
        var vp = view * proj;
        var c = new Constants
        {
            ViewProjection = vp,
            World = Matrix4x4.Identity,
            Tint = Vector4.One,
            LightDir = new Vector4(Vector3.Normalize(new Vector3(0.3f, 0.5f, -1f)), 0),
            LightColor = new Vector4(0.85f, 0.8f, 0.7f, 1),
            Ambient = new Vector4(0.45f, 0.45f, 0.5f, 1),
            Fog = new Vector4(1500, 6000, 0, 0),
            FogColor = new Vector4(0.02f, 0.02f, 0.03f, 1),
            CameraPos = new Vector4(camEyeNow, 1),
        };
        _map.Draw(c);
        foreach (var f in _fighters)
        {
            f.R.Pose(f.Motion, f.MotionTick);
            var fc = c;
            fc.Tint = f.Flash > 0 ? new Vector4(1.8f, 1.4f, 1.4f, 1) : Vector4.One;
            f.R.Draw(fc, Matrix4x4.CreateRotationZ(f.Yaw) * Matrix4x4.CreateTranslation(f.Pos));
        }
        DrawEffects(c);
        _overlayMovie?.DrawOverlay();
        DrawHud(vp);
        DrawUi(vp);
    }

    private string _lastMark = "";

    private Vector2? Project(Matrix4x4 vp, Vector3 p)
    {
        var h = Vector4.Transform(new Vector4(p, 1), vp);
        if (h.W <= 0.1f) return null;
        return new Vector2((h.X / h.W * 0.5f + 0.5f) * _ui.Width, (0.5f - h.Y / h.W * 0.5f) * _ui.Height);
    }

    private void DrawUi(Matrix4x4 vp)
    {
        _ui.Clear();
        int W = _ui.Width, H = _ui.Height;

        // 적 체력 (위)
        int ex = 8;
        foreach (var e in Enemies)
        {
            _ui.Box(ex - 4, 4, 140, 30, 0, 0, 0, 140);
            _ui.Text(ex, 6, e.P.Name, (byte)(e.P.Kind == 5 ? 255 : 230), (byte)(e.P.Kind == 5 ? 170 : 220), (byte)(e.P.Kind == 5 ? 120 : 200));
            _ui.Bar(ex, 23, 128, 5, e.Hp / (float)e.MaxHp, (200, 60, 50));
            ex += 146;
        }

        int py = H - 62;   // 원본 카드 띠 위 — 도움말 자리 기준
        // 행동 중인 배우와 대상 표시
        if (_actor != null && _phase is Phase.Command or Phase.Acting && Project(vp, _actor.Pos + new Vector3(0, 0, _actor.P.BodyHeight + 6)) is { } ap)   // 머리 위 — 키는 CharData 크기 갈래(+0xa2)
        {
            _ui.Text((int)ap.X - 3, (int)ap.Y - 8, "V", 255, 230, 90);
            _lastMark = $"mark={ap.X:F0},{ap.Y:F0} feet={Project(vp, _actor.Pos)?.ToString("F0")} ui={_ui.Width}x{_ui.Height} gfx={_gfx.Width}x{_gfx.Height}";
        }
        if (_phase == Phase.Command && _target >= 0 && Project(vp, _fighters[_target].Pos + new Vector3(0, 0, _fighters[_target].P.BodyHeight * 0.6f)) is { } tp)   // 몸 가운데
            _ui.Text((int)tp.X - 10, (int)tp.Y - 8, "[ ]", 255, 90, 80);

        foreach (var p in _popups.Where(p => !p.Text.TrimStart('+').All(char.IsDigit)))
            if (Project(vp, p.At + new Vector3(0, 0, p.Age * 30)) is { } s)
            {
                var (r, g, b) = p.Color;
                _ui.Text((int)s.X - _ui.TextWidth(p.Text) / 2, (int)s.Y, p.Text, r, g, b);
            }

        if (_phase == Phase.Command && _actor != null)
        {
            var skill = Skill(_actor);
            string help = $"Z 공격(연타=연결)  X {(skill != null ? Clean(skill.Name) : "-")}  {(_actor.Apocalypse >= 3 ? "C 오버크래쉬  " : "")}A/D 대상  Space 턴 끝  F9 자동";
            int w = _ui.TextWidth(help);
            _ui.Box((W - w) / 2 - 6, py - 22, w + 12, 18, 0, 0, 0, 160);
            _ui.Text((W - w) / 2, py - 20, help, 240, 230, 200);
        }
        if (_comboOn && _action != null && !_action.Actor.Enemy)
        {
            // 연결공격 창 표시: 막대 위에 성공 구간
            int bx = W / 2 - 60, by = py - 40;
            _ui.Box(bx, by, 120, 6, 30, 30, 30, 200);
            float total = 2 * ComboStep + ComboWindow;
            for (int n = 1; n <= 2; n++)
                _ui.Box(bx + (int)(n * ComboStep / total * 120), by, (int)(ComboWindow / total * 120), 6, 220, 180, 60);
            _ui.Box(bx + (int)(Math.Min(_comboFrames, total) / total * 120), by - 2, 2, 10, 255, 255, 255);
        }
        if (_messageTime > 0 || _phase is Phase.Won or Phase.Lost)
        {
            string m = _phase is Phase.Won or Phase.Lost ? _message + "   (Enter 다시 / Esc 시작 화면)" : _message;
            int w = _ui.TextWidth(m);
            _ui.Box((W - w) / 2 - 8, 44, w + 16, 20, 0, 0, 0, 170);
            _ui.Text((W - w) / 2, 47, m, 255, 240, 200);
        }
        _ui.Present();
    }

    public void Dispose()
    {
        _overlayMovie?.Dispose();
        if (!_returned) _setup.ReturnTo?.Dispose();
        foreach (var f in _fighters) f.R.Dispose();
        _map.Dispose();
        _ui.Dispose();
        DisposeHud();
    }
}
