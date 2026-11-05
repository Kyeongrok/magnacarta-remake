using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Render;

namespace MagnaCarta.Scenes;

/// <summary>
/// 필드 사건 돌리개 — 구역(304) 없는 사건만, 명령 일부만 돌린다(분석-스토리 · 분석-시작장면).
/// 사건 고르기: 돌고 있는 것이 없으면 차례대로 조건이 다 맞고 남은 횟수(깃발, 0 = 끝없이)가 있는 첫 사건(0번 「CALL 묶음」은 건너뜀).
/// 돌리는 명령: 1 SYNC · 2 WAIT · 3 END · 4/5 GOTO · 7/8 입력 잠금/풀기 · 100/102 스위치 = · 200 모션 · 202 표지 쪽으로 걷기 ·
/// 502/506/515 들어오기 · 503/507/516 나가기 · 605 음악 · 700/704 대사 · 701 장소 이름. 나머지(카메라 컷 500, 경로 206 …)는 아직 건너뛴다.
/// 배우 번호: 10000 = 조작 캐릭터, 10000+n = fld B 의 id n (그 인물이 칼린츠면 칼린츠).
/// </summary>
internal sealed partial class FieldScene
{
    private static readonly string? EventLog = Environment.GetEnvironmentVariable("MC_EVENT_LOG") is { Length: > 0 } l ? l : null;
    private readonly Dictionary<int, int> _localSwitches = [];
    private readonly Dictionary<FieldEvent, int> _runs = [];
    private FieldEvent? _running;
    private int _pc;
    private float _wait, _syncTime;
    private bool _locked;
    private Vector3? _walkTo;
    private bool _walkRun;
    private (float From, float Delta, float Total, float Done)? _turn;

    private static (float, float, float, float) Turn(float from, float to, float frames, int dir)
    {
        float d = MathF.IEEERemainder(to - from, MathF.Tau);     // −π..π
        if (dir == 0 && d > 0) d -= MathF.Tau;                     // 줄어드는 쪽
        if (dir != 0 && d < 0) d += MathF.Tau;                     // 늘어나는 쪽
        return (from, d, frames, 0);
    }

    private static float StepTurn(ref (float From, float Delta, float Total, float Done)? t, float tk)
    {
        var v = t!.Value;
        v.Done = Math.Min(v.Total, v.Done + tk);
        float yaw = v.From + v.Delta * (v.Done / v.Total);
        t = v.Done >= v.Total ? null : v;
        return yaw;
    }   // 203 = 달리기 4.0 (202 걷기 1.0)

    /// <summary>206 의 목표: 캐릭터(fld B id)를 먼저, 없으면 C 표지.</summary>
    private Vector3? TargetPos(int id)
    {
        if (_field.Actors.Find(x => x.Id == id) is { } act)
            return act.Character == 0 && _field.Actors.FindIndex(x => x.Character == 0) == _field.Actors.IndexOf(act) ? _pos
                 : _cast.Find(c => c.Id == id)?.Pos ?? act.Position;
        return _field.Objects.Find(o => o.Id == id)?.Position;
    }
    private int _scriptMotionNext = -1;
    private CameraCut? _cut;
    private float _cutTick;

    // 사건 대사(700): 얼굴, 목소리, 수명(틀), 틀마다 5px 드러남
    private Image32? _face;
    private string _speaker = "";
    private float _dialogAge, _dialogLife;
    private bool _scriptDialog;

    private static bool CondOk(int[] c, Dictionary<int, int> local) => c[0] switch
    {
        0 => true,
        100 => Compare(local.GetValueOrDefault(c[1]), c[2], c[3]),
        101 => Compare(GameState.Switch(c[1]), c[2], c[3]),
        302 => GameState.LastBattleWon is { } won && (won ? 1 : 0) == c[1],   // 전투 결과(전투 필드 597~601·776~825)
        _ => false,
    };

    private bool IsPlayer(int actor) =>
        actor == 10000 || actor >= 10000 && _field.Actors.Find(a => a.Id == actor - 10000) is { Character: 0 };

    /// <summary>한 틀 돌린다. GOTO 면 다음 장면을 돌려준다.</summary>
    private IScene? RunEvents(float tk, Input input)
    {
        // 사건 대사가 떠 있으면: 다 찍힌 뒤 Space/왼쪽 단추 또는 수명이 다하면 닫는다
        if (_scriptDialog && _dialog != null)
        {
            _dialogAge += tk;
            bool shown = _dialogAge * 5 >= _ui.TextWidth(_dialog.Replace("\n", "")) * 1.0f;
            if (_dialogAge >= _dialogLife || shown && (input.Pressed(Input.Space) || input.LeftClicked || input.Pressed(Input.Enter)))
            { _dialog = null; _scriptDialog = false; _face = null; Audio.Music.StopVoice(); }
        }
        if (_running == null)
        {
            // 조건 0 「늘」은 다른 사건이 하나도 안 맞을 때만(분석-스토리) — 그래서 두 번 훑는다
            for (int pass = 0; pass < 2 && _running == null; pass++)
                foreach (var ev in _field.Events)
                {
                    if (ev.Conditions.Any(c => c[0] == 304)) continue;                 // 구역 사건(출구 등)은 따로
                    if (ev.Actions.All(a => a[0] == 0)) continue;                       // CALL 묶음
                    bool always = ev.Conditions.Count == 0 || ev.Conditions.All(c => c[0] == 0);
                    if (always != (pass == 1)) continue;
                    if (!ev.Conditions.All(c => CondOk(c, _localSwitches))) continue;
                    int runs = _runs.GetValueOrDefault(ev);
                    if (ev.Trigger != 0 && runs >= ev.Trigger) continue;
                    // 끝없이 도는(깃발 0) 「늘」 사건이 같은 틀에 거듭 걸리지 않게 한 번 돈 뒤엔 다른 사건이 맞을 때까지 쉰다
                    if (always && ev.Trigger == 0 && runs > 0) continue;
                    _runs[ev] = runs + 1;
                    _running = ev; _pc = 0; _wait = 0;
                    break;
                }
            if (_running == null) return null;
        }

        if (_walkTo is { } goal)
        {
            var d = new Vector2(goal.X - _pos.X, goal.Y - _pos.Y);
            float step = (_walkRun ? RunSpeed : WalkSpeed) * tk;
            if (d.Length() <= step + 1) { _walkTo = null; _motion = CharacterRenderer.Stand; }
            else
            {
                var n = Vector2.Normalize(d);
                // 사건 걷기는 벽·바닥 판정 없이 표지로 간다(패치판 바닥 판정이 원본과 달라 막히면 장면이 멈춘다)
                _pos += new Vector3(n * step, 0);
                if (_map.Ground.HeightAt(_pos.X, _pos.Y, _pos.Z + 40) is { } gz) _pos.Z = gz;
                _yaw = MathF.Atan2(n.X, -n.Y);
                int mv = _walkRun ? CharacterRenderer.Run : CharacterRenderer.Walk;
                if (_motion != mv) { _motion = mv; _motionTick = 0; }
            }
        }
        if (_turn != null) _yaw = StepTurn(ref _turn, tk);
        if (_scriptMotionNext >= 0 && _calintz.SequenceTicks(_motion) is int len && len > 0 && _motionTick >= len)
        { _motion = _scriptMotionNext; _motionTick = 0; _scriptMotionNext = -1; }

        if (_cut != null) _cutTick += tk;
        if (_choice is { } ch)
        {
            if (input.Pressed(Input.Left) || input.Pressed(Input.Up)) _choiceIndex = 0;
            if (input.Pressed(Input.Right) || input.Pressed(Input.DownKey)) _choiceIndex = 1;
            if (input.Pressed(Input.Enter) || input.Pressed(Input.Space))
            {
                var pick = ch.Options[_choiceIndex];
                GameState.Evolve(ch.Who.Id, pick.Id);
                EvolveMessage(ch.Who, pick);
                _choice = null;
            }
            return null;
        }
        if (_wait > 0) { _wait -= tk; return null; }
        var acts = _running.Actions;
        while (_pc < acts.Count)
        {
            var a = acts[_pc];
            if (EventLog is { } log) File.AppendAllText(log, $"{_field.Number} ev{_field.Events.IndexOf(_running)} pc{_pc} op{a[0]} [{string.Join(',', a[1..6])}] party={string.Join('/', GameState.Party)}" + Environment.NewLine);
            switch (a[0])
            {
                case 1:   // SYNC — 대사·걷기·컷이 끝날 때까지 (패치판: 20초가 넘으면 그냥 넘어간다)
                    _syncTime += tk;
                    if (EventLog != null && _syncTime >= 300 && _syncTime - tk < 300) File.AppendAllText(EventLog, $"SYNC 기다림: dialog={_dialog != null && _scriptDialog} walk={_walkTo != null} turn={_turn != null} castWalk={_cast.Count(c => c.WalkTo != null)} castTurn={_cast.Count(c => c.Turn != null)} cut={_cut != null && _cutTick < _cut.Length}({_cutTick:F0}/{_cut?.Length})" + Environment.NewLine);
                    if (_syncTime < 600 && (_dialog != null && _scriptDialog || _walkTo != null || _turn != null || _cast.Any(c => c.WalkTo != null || c.Turn != null) || _cut != null && _cutTick < _cut.Length)) return null;
                    _syncTime = 0;
                    if (_walkTo != null) { _walkTo = null; _motion = CharacterRenderer.Stand; }
                    foreach (var cw in _cast) cw.WalkTo = null;
                    break;
                case 2: _wait = a[1]; _pc++; return null;
                case 3: _pc = acts.Count; continue;
                case 4: return Goto(a[1], null);
                case 5: return Goto(a[1], (a[2], a[3]));
                case 7: _locked = true; break;
                case 8: _locked = false; break;
                case 100: _localSwitches[a[1]] = a[2]; break;
                case 102: GameState.SetSwitch(a[1], a[2]); break;
                case 200 when IsPlayer(a[1]):
                    // a1 = 지금 틀 모션, a3 = 이어서 되풀이할 모션(추정)
                    if (_calintz.HasSequence(a[2])) { _motion = a[2]; _motionTick = 0; }
                    _scriptMotionNext = a[4] > 0 && _calintz.HasSequence(a[4]) && a[4] != a[2] ? a[4] : -1;
                    break;
                case 201 when IsPlayer(a[1]):
                    if (_calintz.HasSequence(a[2])) { _motion = a[2]; _motionTick = 0; }
                    _scriptMotionNext = -1;
                    break;
                case 201 when CastOf(a[1]) is { } who201:
                    if (who201.R.HasSequence(a[2])) { who201.Motion = a[2]; who201.Tick = 0; }
                    who201.Next = -1;
                    break;
                case 206:
                    // a2+1 틀에 걸쳐 돈다, a3 = 도는 쪽(0 각이 줄어드는 쪽 · 그 밖 늘어나는 쪽) — 분석-경로이동
                    if (TargetPos(a[2]) is { } look)
                    {
                        float frames = Math.Max(1, a[3] + 1);
                        if (IsPlayer(a[1])) _turn = Turn(_yaw, MathF.Atan2(look.X - _pos.X, -(look.Y - _pos.Y)), frames, a[4]);
                        else if (CastOf(a[1]) is { } turner) turner.Turn = Turn(turner.Yaw, MathF.Atan2(look.X - turner.Pos.X, -(look.Y - turner.Pos.Y)), frames, a[4]);
                    }
                    break;
                case 200 when CastOf(a[1]) is { } who:
                    if (who.R.HasSequence(a[2])) { who.Motion = a[2]; who.Tick = 0; }
                    who.Next = a[4] > 0 && who.R.HasSequence(a[4]) && a[4] != a[2] ? a[4] : -1;
                    break;
                case 202 or 203 when CastOf(a[1]) is { } mover:
                    if (_field.Objects.Find(o => o.Id == a[2]) is { } m2) { mover.WalkTo = m2.Position; mover.Running = a[0] == 203; }
                    break;
                case 202 or 203 when IsPlayer(a[1]):
                    if (_field.Objects.Find(o => o.Id == a[2]) is { } mark) { _walkTo = mark.Position; _walkRun = a[0] == 203; }
                    break;
                case 502 or 506 or 515: _enterFade = 20; break;
                case 503 or 507 or 516: _enterFade = 20; break;   // 나가기도 일단 같은 어둠 — 다음 들어오기에서 밝아진다
                case 605: Audio.Music.Play(a[1]); break;
                case 606: Audio.Music.Stop(); break;
                case 807:   // 신기 진화 a0 = 캐릭터 id — 길이 둘이면 고르기 창
                    if (Chars.Value.TryGetValue(a[1], out var who807))
                    {
                        var next = GameState.NextSingi(who807);
                        if (next.Count == 1) { GameState.Evolve(a[1], next[0].Id); EvolveMessage(who807, next[0]); }
                        else if (next.Count > 1) { _choice = (who807, next); _choiceIndex = 0; _pc++; return null; }
                    }
                    break;
                case 211 when CastOf(a[1]) is { } show: show.Hidden = false; break;
                case 213 when CastOf(a[1]) is { } hide: hide.Hidden = true; break;
                case 518:   // FrameData 띄우기 a0 = 640판, a1 = 800판(화면 너비 700 이상이면 둘째, 0x5207d991)
                    ShowFrame(a[2] > 0 ? a[2] : a[1]);
                    break;
                case 519: _frame?.Dispose(); _frame = null; break;
                case 505 when MovieScene.Available && a[1] >= 0 && MovieScene.FileFor(a[1]) != null:   // 동영상 a0(분석-컷씬) — x86 빌드에서만, 끝나면 이 필드의 다음 명령으로
                    _pc++;
                    return new MovieScene(_gfx, _screen, a[1], () => this) { ReturnTo = this };
                case 9:   // 전투 시작 a0 = 전투 필드 — 그 필드의 맵이 무대, B 묶음 꼬리 첫 word 2 = 아군 · 0 = 적(분석-던전)
                    if (StartEventBattle(a[1]) is { } battle) { _pc++; _running = null; return battle; }
                    break;
                case 300 or 301 when _npcs.Find(n => n.Loop && n.O.Id == a[1]) is { } lo:   // 3D 루프 물체 켜기/재생 [물체, 빠르기 %(추정), 되풀이]
                    lo.Playing = true; lo.Speed = a[2] > 0 ? a[2] / 100f : 1; break;
                case 302 or 303 when _npcs.Find(n => n.Loop && n.O.Id == a[1]) is { } ls: ls.Playing = false; break;
                case 804: GameState.Join(a[1]); break;    // 파티 넣기 a0 = CharData id (0x5207f255)
                case 805: GameState.Leave(a[1]); break;   // 빼기 (0x5207f3c7)
                case 600 when _field.Objects.Find(o => o.Id == a[1] && o.Kind == 11) is { } so: Audio.SoundEffects.StartLoop($"amb{so.Id}", so.Model); break;
                case 601: Audio.SoundEffects.StopLoop($"amb{a[1]}"); break;
                case 602 or 603: Audio.SoundEffects.Play(a[1]); break;   // a0 = SED 번호
                case 500:
                    if (_field.Cuts.TryGetValue(a[1], out var cut)) { _cut = cut; _cutTick = 0; }
                    break;
                case 501 or 504: _cut = null; break;     // 따라가기 · 되돌리기 → 패치판 따라가는 카메라
                case 700 or 704: Say(a); break;
                case 701:
                    if (_lines.TryGetValue(a[1], out var place)) ShowTitle(place);
                    break;
            }
            _pc++;
        }
        _running = null;
        if (!_locked) _cut = null;    // 사건이 끝나고 조작이 돌아오면 따라가는 카메라로
        return null;
    }

    private Vortice.Direct3D11.ID3D11ShaderResourceView? _frame;

    private void ShowFrame(int number)
    {
        _frame?.Dispose(); _frame = null;
        if (!McFiles.Exists("FrameData", number, "fdi")) return;
        try
        {
            var img = FrameData.Load(number).Compose();
            var full = new Image32(800, 600);
            full.Fill(0, 0, 800, 600, 0, 0, 0);
            full.Blend(img, (800 - img.Width) / 2, (600 - img.Height) / 2);
            _frame = _gfx.Texture(full, false);
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { }
    }

    private void DrawFrame()
    {
        if (_frame == null) return;
        var (x, y, w, h) = _screen.Fit(800, 600);
        _screen.Draw(_frame, x, y, w, h, System.Numerics.Vector4.One);
    }

    private (CharParam Who, List<Singi> Options)? _choice;
    private int _choiceIndex;

    private static readonly string[] ElementNames = ["", "지", "화", "풍", "암", "수", "전", "광"];

    private void EvolveMessage(CharParam who, Singi s)
    {
        _dialog = $"{who.Name} - {s.Name}" + (char)10 +
                  (s.Element is >= 1 and <= 7 ? $"{ElementNames[s.Element]} 카르타의 친화도가 {s.Affinity} 증가하였습니다. " : "") +
                  $"체력 +{s.Hp} 공격 +{s.Str} 방어 +{s.Def} 지력 +{s.Int} 민첩 +{s.Dex}";
        _scriptDialog = true; _talking = null; _dialogAge = 0; _dialogLife = 300; _face = null; _speaker = "";
    }

    private void DrawChoice()
    {
        if (_choice is not { } ch) return;
        int w = 300, h = 70, x = (_ui.Width - w) / 2, y = _ui.Height / 2 - h / 2;
        _ui.Box(x, y, w, h, 10, 8, 6, 220);
        _ui.Text(x + 10, y + 6, $"{ch.Who.Name} - 카르타를 선택하시오.", 255, 220, 140);
        for (int i = 0; i < ch.Options.Count; i++)
        {
            bool sel = i == _choiceIndex;
            _ui.Text(x + 20, y + 26 + i * 18, (sel ? "> " : "  ") + ch.Options[i].Name, 255, sel ? (byte)230 : (byte)210, sel ? (byte)120 : (byte)200);
        }
    }

    private IScene? StartEventBattle(int battleField)
    {
        if (!McFiles.Exists("Field", battleField, "fld")) return null;
        var bf = Field.Load(battleField);
        if (bf.MapNumber < 0 || !McFiles.Exists("MAP", bf.MapNumber, "wrd")) return null;
        var allies = new List<(int, Vector3)>(); var foes = new List<(int, Vector3)>();
        foreach (var a in bf.Actors)
        {
            bool ally = bf.ActorSides.TryGetValue(a.Id, out int side) && side == 2;
            (ally ? allies : foes).Add((a.Character, a.Position));
        }
        if (foes.Count == 0) return null;
        var gfx = _gfx; var screen = _screen;
        GameState.LastBattleWon = null;
        return new BattleScene(gfx, screen, new BattleScene.Setup(bf.MapNumber, -1, [.. GameState.Party], 0, bf.Music, null)
        {
            Allies = allies.Count > 0 ? allies : null,
            Foes = foes,
            BattleField = battleField,
            // 끝나면 전투 필드로 — 그 필드의 302 사건이 결과를 보고 다음으로 GOTO 한다
            After = won => { GameState.LastBattleWon = won; return new FieldScene(gfx, screen, battleField); },   // 이 필드는 전투로 넘어갈 때 이미 버려졌다
        });
    }

    private IScene? Goto(int field, (int, int)? entry)
    {
        _running = null;
        if (!McFiles.Exists("Field", field, "fld")) return null;
        _leaving = new Exit(-1, field, entry?.Item1 ?? -1, entry?.Item2 ?? -1);
        _leaveTicks = 0;
        return null;
    }

    /// <summary>700: a0 화자, a1~a5 모션, a6 Fpb id, a7 목소리 SVD(−1 없음), a8 수명(0·−1 이면 450틀).</summary>
    private void Say(int[] a)
    {
        if (!_lines.TryGetValue(a[7], out var text)) return;
        _dialog = text;
        _scriptDialog = true;
        _talking = null;
        _dialogAge = 0;
        _dialogLife = a[9] > 0 ? a[9] : 450;
        // 수명이 목소리보다 짧으면 글을 다 읽기 전에 닫혀 버린다 — 패치판은 글 길이만큼은 남긴다
        _dialogLife = Math.Max(_dialogLife, _ui.TextWidth(text) / 5f + 45);
        _face = null; _speaker = "";
        int who = a[1];
        int ch = who == 10000 ? 0 : _field.Actors.Find(x => x.Id == who - 10000)?.Character ?? -1;
        if (ch >= 0 && Chars.Value.TryGetValue(ch, out var cp))
        {
            _speaker = cp.Name;
            if (cp.Face > 0 && McFiles.Exists("BGR", cp.Face, "BGR"))
                try { _face = Images.Bgr(cp.Face); } catch (InvalidDataException) { }
        }
        if (a[8] >= 0) Audio.Music.Voice(a[8]);
    }

    /// <summary>fld B 의 다른 인물 — CharData id → .amd 칸 0 몸, 서기(100). 사건 200·202 가 움직인다.</summary>
    private sealed class Cast
    {
        public required int Id;
        public required CharacterRenderer R;
        public Vector3 Pos;
        public float Yaw, Tick;
        public int Motion = CharacterRenderer.Stand, Next = -1;
        public Vector3? WalkTo;
        public bool Hidden, Running;
        public (float From, float Delta, float Total, float Done)? Turn;
    }
    private readonly List<Cast> _cast = [];

    private void LoadCast()
    {
        var meshes = new Dictionary<int, CharacterMesh>();
        bool playerTaken = false;
        foreach (var a in _field.Actors)
        {
            if (a.Character == 0 && !playerTaken) { playerTaken = true; continue; }   // 첫 칼린츠 = 조작 캐릭터
            if (!Chars.Value.TryGetValue(a.Character, out var cp) || cp.Amd < 0 || !McFiles.Exists("AmdData", cp.Amd, "amd")) continue;
            try
            {
                var slots = Amd.Load(cp.Amd);
                if (!slots.TryGetValue(0, out int model) || !McFiles.Exists("3DOBS", model, "3do")) continue;
                if (!meshes.TryGetValue(model, out var mesh)) meshes[model] = mesh = CharacterMesh.Load(model);
                var c = new Cast { Id = a.Id, R = new CharacterRenderer(_gfx, mesh), Pos = a.Position, Yaw = a.Rotation.Z };
                if (_map.Ground.HeightAt(c.Pos.X, c.Pos.Y, c.Pos.Z + 60) is { } z) c.Pos.Z = z;
                _cast.Add(c);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or KeyNotFoundException) { }
        }
    }

    private Cast? CastOf(int actor) => actor >= 10000 ? _cast.Find(c => c.Id == actor - 10000) : null;

    private void UpdateCast(float tk)
    {
        foreach (var c in _cast)
        {
            c.Tick += tk;
            if (c.Turn != null) c.Yaw = StepTurn(ref c.Turn, tk);
            if (c.WalkTo is { } goal)
            {
                var d = new Vector2(goal.X - c.Pos.X, goal.Y - c.Pos.Y);
                float step = (c.Running ? RunSpeed : WalkSpeed) * tk;
                if (d.Length() <= step + 1) { c.WalkTo = null; c.Motion = CharacterRenderer.Stand; c.Tick = 0; }
                else
                {
                    var n = Vector2.Normalize(d);
                    c.Pos += new Vector3(n * step, 0);
                    if (_map.Ground.HeightAt(c.Pos.X, c.Pos.Y, c.Pos.Z + StepUp) is { } z) c.Pos.Z = z;
                    c.Yaw = MathF.Atan2(n.X, -n.Y);
                    int mv = c.Running ? CharacterRenderer.Run : CharacterRenderer.Walk;
                    if (c.Motion != mv) { c.Motion = mv; c.Tick = 0; }
                }
            }
            if (c.Next >= 0 && c.R.SequenceTicks(c.Motion) is int len && len > 0 && c.Tick >= len) { c.Motion = c.Next; c.Next = -1; c.Tick = 0; }
        }
    }

    private void DrawCast(Constants k)
    {
        foreach (var c in _cast)
        {
            if (c.Hidden) continue;
            c.R.Pose(c.R.HasSequence(c.Motion) ? c.Motion : CharacterRenderer.Stand, c.Tick);
            c.R.Draw(k, Matrix4x4.CreateRotationZ(c.Yaw) * Matrix4x4.CreateTranslation(c.Pos));
        }
    }

    private static readonly Lazy<Dictionary<int, CharParam>> Chars = new(() => CharParam.LoadAll().ToDictionary(c => c.Id));
}
