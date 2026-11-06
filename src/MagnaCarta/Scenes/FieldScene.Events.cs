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
            return act.Character == _playerChar && _field.Actors.FindIndex(x => x.Character == _playerChar) == _field.Actors.IndexOf(act) ? _pos
                 : _cast.Find(c => c.Id == id)?.Pos ?? act.Position;
        return _field.Objects.Find(o => o.Id == id)?.Position;
    }
    private int _scriptMotionNext = -1;
    private CameraCut? _cut;
    private float _cutTick;
    /// <summary>사건 508 카메라 캐릭터 보기 — 따라가는 카메라의 보는 점을 이 배우로(0x52079f31: a0 배우, a1 0 = 한 번 돌려 봄 · 1 = 계속 봄). 504 · 8 · 필드 떠나기에서 푼다.</summary>
    private int _camLook = -1;
    private Vector3? _camLookPos;

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
        actor == 10000 || actor >= 10000 && _field.Actors.Find(a => a.Id == actor - 10000) is { } pa && pa.Character == _playerChar;

    /// <summary>한 틀 돌린다. GOTO 면 다음 장면을 돌려준다.</summary>
    private IScene? RunEvents(float tk, Input input)
    {
        // 사건 대사가 떠 있으면: 다 찍힌 뒤 Space/왼쪽 단추 또는 수명이 다하면 닫는다
        if (_scriptDialog && _dialog != null)
        {
            _dialogAge += tk;
            float full = _ui.TextWidth(_dialog.Replace("\n", "")) / 5f;   // 다 찍히는 틱
            bool shown = _dialogAge >= full;
            bool press = input.Pressed(Input.Space) || input.LeftClicked || input.Pressed(Input.Enter);
            // 찍히는 중에 누르면 한꺼번에 다 보이고(사용자 요청 — 클릭하면 빨리), 다 보인 뒤 누르면 넘긴다
            if (press && !shown) _dialogAge = full;
            else if (_dialogAge >= _dialogLife || shown && press)
            { _dialog = null; _scriptDialog = false; _face = null; Audio.Music.StopVoice(); }
        }
        if (_running == null)
        {
            // 조건 0 「늘」은 다른 사건이 하나도 안 맞을 때만(분석-스토리) — 그래서 두 번 훑는다
            // 구역(304) 사건 중 GOTO 가 없는 것(전투 9 · 스위치 · 대사 — 725 구역 1 → 0789, 690 → 25 += 1, 727 숲 24)은
            // 플레이어가 그 구역에 들어설 때(밖 → 안) 돌린다. GOTO 가 든 것은 출구로 따로(FieldScene 출구)
            if (_since > 1f)
                foreach (var ev in _field.Events)
                {
                    if (!ev.Conditions.Any(c => c[0] == 304) || ev.Actions.Any(a => a[0] is 4 or 5)) continue;
                    bool inside = ev.Conditions.Where(c => c[0] == 304).All(c => _field.Regions.Find(rg => rg.Id == c[2]) is { } rg && rg.Contains(_pos));
                    bool was = _zoneInside.Contains(ev);
                    if (inside) _zoneInside.Add(ev); else _zoneInside.Remove(ev);
                    if (!inside || was || _running != null) continue;
                    if (!ev.Conditions.Where(c => c[0] != 304).All(c => CondOk(c, _localSwitches))) continue;
                    int zr = _runs.GetValueOrDefault(ev);
                    if (ev.Trigger != 0 && zr >= ev.Trigger) continue;
                    _runs[ev] = zr + 1;
                    _running = ev; _pc = 0; _wait = 0;
                }
            for (int pass = 0; pass < 2 && _running == null; pass++)
                foreach (var ev in _field.Events)
                {
                    if (ev.Conditions.Any(c => c[0] == 304)) continue;                 // 구역 사건은 위(GOTO 없는 것)와 출구로
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
        if (_advanceLeft > 0) { float st = Math.Min(tk, _advanceLeft); _advanceLeft -= st; _pos += new Vector3(MathF.Sin(_yaw), -MathF.Cos(_yaw), 0) * _advanceSpeed * st; }
        if (_scriptMotionNext >= 0 && _calintz.SequenceTicks(_motion) is int len && len > 0 && _motionTick >= len)
        { _motion = _scriptMotionNext; _motionTick = 0; _scriptMotionNext = -1; }

        if (_cut != null) _cutTick += tk * CutTuning.Get(_field.Number, _cut.Id).Speed;   // F7 컷 조정의 속도
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
                    if (_syncTime < 600 && (_dialog != null && _scriptDialog || _walkTo != null || _turn != null || _cast.Any(c => c.WalkTo != null || c.Turn != null || c.Gliding || c.Fading || c.AdvanceLeft > 0) || _npcs.Any(n => n.AlphaT < n.AlphaDur) || _advanceLeft > 0 || _musicFade != null || _cut != null && _cutTick < _cut.Length)) return null;
                    _syncTime = 0;
                    if (_walkTo != null) { _walkTo = null; _motion = CharacterRenderer.Stand; }
                    foreach (var cw in _cast) cw.WalkTo = null;
                    break;
                case 2: _wait = a[1]; _pc++; return null;
                case 3: _pc = acts.Count; continue;
                case 4: return Goto(a[1], null);
                case 5: return Goto(a[1], (a[2], a[3]));
                // 16 — 시작 화면으로(0x5207f67f: 0x5206e36e 뒤 장면 0x33 = 시스템 메뉴 「Back to the Title」 과 같은 길). 348 에서 한 번(끝 장면으로 보임)
                case 16: _pc++; _running = null; return new TitleScene(_gfx, _screen);
                case 7: _locked = true; break;
                case 8: _locked = false; _camLook = -1; break;
                case 100: _localSwitches[a[1]] = a[2]; break;
                case 102: GameState.SetSwitch(a[1], a[2]); break;
                // 103 [전역 스위치, 셈, 값] — 셈 0 더하기 · 1 빼기 · 2 곱하기 · 3 나누기(0x5207a9ba, 점프표 0x5207aad7). 690·691 의 「103 [25, 0, 1]」 = 스위치 25 에 1 더하기
                case 103:
                    {
                        int cur = GameState.Switch(a[1]);
                        int v = a[2] switch { 0 => cur + a[3], 1 => cur - a[3], 2 => cur * a[3], 3 when a[3] != 0 => cur / a[3], _ => cur };
                        GameState.SetSwitch(a[1], v);
                    }
                    break;
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
                // 204 [배우, 물체, z 갈래, 틀 수] — 걷지 않고 물체 자리로 틀 수에 걸쳐 옮김(0x5207601a: 이동기 0x5204f877 → vtbl+0x88, 304 와 같은 보간기).
                // a2 > 0 이면 z 를 그 자리 바닥 높이로, 아니면 물체 z 그대로(자료엔 −1 · 0 뿐). 101(10005) · 109(10001) 등 — 걸어가는 202 와 달리 떠서 미끄러지는 연출
                case 204 when CastOf(a[1]) is { } gl && TargetPos(a[2]) is { } gto:
                    gl.GlideFrom = gl.Pos; gl.GlideTo = gto; gl.GlideT = 0; gl.GlideDur = Math.Max(1, a[4]);
                    if (a[3] > 0 && _map.Ground.HeightAt(gto.X, gto.Y, gto.Z + StepUp) is { } gz) gl.GlideTo = new Vector3(gto.X, gto.Y, gz);
                    break;
                // 219 / 220 [배우, 틀] — 배우를 자기 앞(지역 −Y, 회전 +0x108)으로 틀마다 1 / 4 만큼 a1 틀 동안 밀기(0x5207767d / 0x52077799, 모션은 그대로). 387·116 에서 한 번씩
                case 219 or 220:
                    {
                        float sp = a[0] == 219 ? 1f : 4f;
                        if (IsPlayer(a[1])) { _advanceLeft = Math.Max(1, a[2]); _advanceSpeed = sp; }
                        else if (CastOf(a[1]) is { } adv) { adv.AdvanceLeft = Math.Max(1, a[2]); adv.AdvanceSpeed = sp; }
                    }
                    break;
                case 207:
                    // 207 [배우, 각(도), 틀, 쪽] — 지금 방향에서 a1 도만큼 돈다(0x52076c08: a3 == 0 이면 yaw − a1·π/180, 아니면 +), a2 틀에 걸쳐(206 과 같은 회전기)
                    {
                        float d207 = a[2] * MathF.PI / 180f * (a[4] == 0 ? -1 : 1), f207 = Math.Max(1, a[3]);
                        if (IsPlayer(a[1])) _turn = (_yaw, d207, f207, 0);
                        else if (CastOf(a[1]) is { } t207) t207.Turn = (t207.Yaw, d207, f207, 0);
                    }
                    break;
                case 200 when CastOf(a[1]) is { } who:
                    if (who.R.HasSequence(a[2])) { who.Motion = a[2]; who.Tick = 0; }
                    who.Next = a[4] > 0 && who.R.HasSequence(a[4]) && a[4] != a[2] ? a[4] : -1;
                    break;
                case 202 or 203 when CastOf(a[1]) is { } mover:
                    if (_field.Objects.Find(o => o.Id == a[2]) is { } m2) { mover.WalkTo = m2.Position; mover.Running = a[0] == 203; mover.KeepMotion = false; }
                    break;
                // 217 [배우, 물체, z] — 202 와 같은 이동기(vtbl+0x8c)로 물체 쪽으로 가되 늘 바로 앞의 200(모션) 뒤에 온다 → 걷기 모션으로 바꾸지 않고 지금 모션 그대로 옮김으로 본다(116 에서만 4번, 추정)
                case 217 when CastOf(a[1]) is { } km:
                    if (_field.Objects.Find(o => o.Id == a[2]) is { } m3) { km.WalkTo = m3.Position; km.Running = false; km.KeepMotion = true; }
                    break;
                case 202 or 203 when IsPlayer(a[1]):
                    if (_field.Objects.Find(o => o.Id == a[2]) is { } mark) { _walkTo = mark.Position; _walkRun = a[0] == 203; }
                    break;
                case 502 or 506 or 515: _enterFade = 20; break;
                case 503 or 507 or 516: _enterFade = 20; break;   // 나가기도 일단 같은 어둠 — 다음 들어오기에서 밝아진다
                case 605: Audio.Music.Play(a[1]); break;
                case 606: Audio.Music.Stop(); break;
                // 511 [단계 0~39] 안개 단계 바꾸기(0x5207a34c) · 512 안개 끄기(그리개 0xb60 끔, 0x5207a5a0) — 색은 이 필드의 것(fld 0xec)
                case 511: SetFog(a[1]); break;
                case 513: _lightsOff.Remove(a[1]); break;   // 빛 id 켜기(0x52026fcf)
                case 514: _lightsOff.Add(a[1]); break;      // 끄기(0x52027087)
                case 512: _fogColor.W = -1; break;
                case 607: Audio.Music.Play2(a[1]); break;   // 음악 1번 채널(14번 — 0번 배경음 위에 겹침)
                case 608: Audio.Music.Stop2(); break;
                // 610 [쪽, 빠르기] — 배경음 페이드(0x5207c910): 빠르기 0/1/2 = 30/60/90 틀, 쪽 1 = 줄이기 · −1 = 키우기(추정). 611 — 조우가 없는 필드(fld 전투 필드 5칸이 모두 −1)에서만 줄이기(0x5207cb5f), 길이는 못 따 60틀로
                case 610: _musicFade = (Audio.Music.Fade, a[1] >= 0 ? 0f : 1f, 0, a[2] switch { 0 => 30, 1 => 60, 2 => 90, _ => 1 }); break;
                // 613 [%, 틀] — 대사(700) 바로 앞에서 배경음을 a0 % 로 a1 틀에 걸쳐 줄임(119·129·147 … 9번, 60~80 % · 30~90 틀). 채널 0~3 단언 0x52051354 를 거치는 음악 채널 쪽으로 보임(추정)
                case 613: _musicFade = (Audio.Music.Fade, Math.Clamp(a[1], 0, 100) / 100f, 0, Math.Max(1, a[2])); break;
                case 611 when _field.BattleFields.All(b => b < 0): _musicFade = (Audio.Music.Fade, 0f, 0, 60); break;
                case 15: LinkCompass(a[1]); break;   // 나침반 깜빡이를 물체 a0 에 잇기(0x52072c33 → 0x52070e30)
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
                // 212 [배우] — 배우를 지움(0x52076f8c → 0x520683ed: 목록에서 찾아 소멸자 vtbl[0](1)). 205 에서 10022~10026 다섯을 한꺼번에. 패치판은 숨김(211 이 다시 넣음)
                case 212 when CastOf(a[1]) is { } gone: gone.Hidden = true; break;
                // 214 [배우] — 깃발 +0xd4 = 0(0x52076ff7): 213(= −1, 숨김)을 푼다. 211 과 달리 장면에 다시 넣지는 않는다. 815번 · 197 필드
                case 214 when CastOf(a[1]) is { } unhide: unhide.Hidden = false; break;
                // 215 / 216 [배우] — 깃발 +0xd8 = −1 / 0 (0x520757fe / 0x5207588d). 깃발 읽개 0x5204ca4f = (+0xd4 ∨ +0xd8) 가 움직임 쪽, 0x5204ca1c = (+0xd4 ∨ +0xd0) 가 그리기 쪽으로 보여
                // (3D 물체 깃발 +0x118/+0x114/+0x11c 와 같은 꼴) 215 = 몸짓 멈춤, 216 = 다시 움직임으로 본다(추정)
                case 215 when CastOf(a[1]) is { } fr: fr.Frozen = true; break;
                case 208 when CastOf(a[1]) is { } fd:
                {
                    // [배우, 갈래, 값, 틀] (0x5207d250): 갈래 3 = 이미 3 이 아니면 값 100 에서 시작, (틀+1) 틀에 걸쳐 값으로.
                    // 갈래 0 = 100 으로 되돌림. 그 밖 갈래(4 — 126 에서 한 번)는 값 0 에서 시작 — 그림 뜻을 못 봐 갈래만 적고 그대로 그린다
                    int mode = a[2];
                    if (mode == 3 && fd.FadeMode != 3) { fd.FadeMode = 3; fd.Alpha = 1; }
                    else if (mode != 3 && mode != 0) { fd.FadeMode = mode; fd.Alpha = 0; }
                    float to = mode == 0 ? 1 : Math.Clamp(a[3], 0, 100) / 100f;
                    fd.AlphaFrom = fd.Alpha; fd.AlphaTo = to; fd.AlphaT = 0; fd.AlphaDur = Math.Max(1, a[4] + 1);
                    break;
                }
                case 216 when CastOf(a[1]) is { } uf: uf.Frozen = false; break;
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
                // 3D 물체 명령(0x52072cbe · 0x52072e20 · 0x52072f55 · 0x5207308a): 모델의 깃발 셋 — +0x11c 멈춤(움직임 갱신이 봄), +0x114 숨김(그리기가 봄).
                // 300 = 재생(멈춤 0 + 시퀀스) · 301 = 멈춤(−1) · 302 = 보이기(숨김 0) · 303 = 숨기기(−1). 흔한 차례 302 → 300 → 301 → 303 = 보이고 · 틀고 · 멈추고 · 숨김
                case 300 when _npcs.Find(n => n.O.Id == a[1]) is { } lo:
                    lo.Playing = true; lo.Speed = a[2] > 0 ? a[2] / 100f : 1; break;
                case 301 when _npcs.Find(n => n.O.Id == a[1]) is { } lp: lp.Playing = false; break;
                case 302 when _npcs.Find(n => n.O.Id == a[1]) is { } lv: lv.Hidden = false; break;
                case 303 when _npcs.Find(n => n.O.Id == a[1]) is { } lh: lh.Hidden = true; break;
                // 310 [3D 물체, 크기 %, 틀 수−1] — 전투 310 과 같은 꼴(값/100 배로 틀+1 에 걸쳐). 278(30 → 50 %)·483(17 → 30 %, 16 → 60 %)
                case 308 when _npcs.Find(n => n.O.Id == a[1] && n.Loop) is { } nf:
                {
                    int mode = a[2];   // 208 과 같은 셈(676 [23, 3, 0, 15] 한 번)
                    if (mode == 3 && nf.FadeMode != 3) { nf.FadeMode = 3; nf.Alpha = 1; }
                    else if (mode != 3 && mode != 0) { nf.FadeMode = mode; nf.Alpha = 0; }
                    nf.AlphaFrom = nf.Alpha; nf.AlphaTo = mode == 0 ? 1 : Math.Clamp(a[3], 0, 100) / 100f; nf.AlphaT = 0; nf.AlphaDur = Math.Max(1, a[4] + 1);
                    break;
                }
                case 310 when _npcs.Find(n => n.O.Id == a[1]) is { } sc:
                    sc.ScaleFrom = sc.Scale; sc.ScaleTo = a[2] / 100f; sc.ScaleT = 0; sc.ScaleDur = Math.Max(1, a[3] + 1); break;
                // 306 [3D 루프 물체, 목표(캐릭터 또는 물체), 틀−1] — 루프 물체가 목표 쪽을 보게 돌림(0x52073799: 두 자리 차 v2 의 각, 206 의 물체판). 278 사건 14 에서 물체 7 이 표지 28 쪽. 패치판은 바로 돌림
                case 306 when _npcs.Find(n => n.O.Id == a[1]) is { } face && TargetPos(a[2]) is { } fp && new Vector2(fp.X - face.Pos.X, fp.Y - face.Pos.Y).LengthSquared() > 1e-7f:
                    face.Yaw = MathF.Atan2(fp.X - face.Pos.X, -(fp.Y - face.Pos.Y)); break;
                // 304 [3D 루프 물체, 목표 물체, ?, 틀 수] — 루프 물체(MCOID_VOBS3LOOPOBJ)를 목표 물체 자리로 틀 수에 걸쳐 옮김(0x520731bf, 「nFrame > 0」). 676 에서만 쓰임
                case 304 when _npcs.Find(n => n.O.Id == a[1]) is { } mv && _field.Objects.Find(o => o.Id == a[2]) is { } goal304:
                    mv.MoveFrom = mv.Pos; mv.MoveTo = goal304.Position; mv.MoveT = 0; mv.MoveDur = Math.Max(1, a[4]); break;
                case 804: GameState.Join(a[1]); break;
                case 809 when GameState.Party.Contains(a[1]): GameState.Leader = a[1]; break;   // 조작 캐릭터 바꾸기 — 다음 필드부터 그 인물로    // 파티 넣기 a0 = CharData id (0x5207f255)
                case 805: GameState.Leave(a[1]); break;   // 빼기 (0x5207f3c7)
                case 810: GameState.Learn(a[1], a[2]); break;
                // 800 [캐릭터, 칸, 값] — 캐릭터 값 한 칸 바꾸기(0x5207e8f5, 점프표 0x5207ea55): 칸 0~12 = +0x6d·+0x6e·+0x70~+0x8c·+0x9c(몸 amd)·+0xaa·+0xbe, 13 = +0x9e 대화 얼굴. 자료엔 346 의 [3, 13, 293] 하나뿐 → 얼굴만 다룸
                case 800 when a[2] == 13: GameState.FaceOverride[a[1]] = a[3]; break;   // 기술 배우기 [캐릭터, 기술] (0x5207f602)
                case 600 when _field.Objects.Find(o => o.Id == a[1] && o.Kind == 11) is { } so: Audio.SoundEffects.StartLoop($"amb{so.Id}", so.Model); break;
                case 601: Audio.SoundEffects.StopLoop($"amb{a[1]}"); break;
                case 602 or 603: Audio.SoundEffects.Play(a[1]); break;   // a0 = SED 번호
                case 500:
                    if (_field.Cuts.TryGetValue(a[1], out var cut)) { _cutRaw = cut; _cut = CutTuning.Apply(cut, CutTuning.Get(_field.Number, cut.Id)); _cutTick = 0; }
                    break;
                case 501 or 504: _cut = null; if (a[0] == 504) _camLook = -1; break;     // 따라가기 · 되돌리기 → 패치판 따라가는 카메라
                case 508: _camLook = a[1]; break;     // 카메라 캐릭터 보기 a0 = 배우
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
        return new BattleScene(gfx, screen, new BattleScene.Setup(bf.MapNumber, -1, GameState.BattleMembers(), 0, bf.Music, null)   // 자리 없는 사건 전투도 출전 넷(세이브 +0x4e 전투칸 4 — 분석-세이브)
        {
            Allies = allies.Count > 0 ? allies : null,
            Foes = foes,
            BattleField = battleField,
            // 끝나면 전투 필드로 — 그 필드의 302 사건이 결과를 보고 다음으로 GOTO 한다
            After = won => { GameState.LastBattleWon = won; return new FieldScene(gfx, screen, battleField, autoSave: false); },   // 이 필드는 전투로 넘어갈 때 이미 버려졌다
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
            int faceNo = GameState.FaceOverride.GetValueOrDefault(ch, cp.Face);
            if (faceNo > 0 && McFiles.Exists("BGR", faceNo, "BGR"))
                try { _face = Images.Bgr(faceNo); } catch (InvalidDataException) { }
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
        /// <summary>사건 215 로 몸짓 멈춤(깃발 +0xd8 = −1) — 216 이 푼다.</summary>
        public bool Frozen;
        /// <summary>사건 217 — 옮기는 동안 걷기 모션으로 안 바꿈.</summary>
        public bool KeepMotion;
        /// <summary>사건 219/220 — 앞(−Y)으로 틀마다 Speed 만큼, Left 틀 동안(모션 그대로).</summary>
        public float AdvanceLeft, AdvanceSpeed;
        public (float From, float Delta, float Total, float Done)? Turn;
        /// <summary>사건 204 미끄러지기 — 걷는 모션 없이 틀 수에 걸쳐 물체 자리로.</summary>
        public Vector3 GlideFrom, GlideTo;
        public float GlideT, GlideDur;
        public bool Gliding => GlideT < GlideDur;
        /// <summary>사건 208 — 재질 갈래(+0x24)와 값(+0x28, 0~100). 갈래 3 = 투명도(100 다 보임), 0 = 보통으로 되돌림.</summary>
        public int FadeMode;
        public float Alpha = 1, AlphaFrom = 1, AlphaTo = 1, AlphaT, AlphaDur;
        public bool Fading => AlphaT < AlphaDur;
    }
    private readonly List<Cast> _cast = [];

    /// <summary>이 필드의 조작 캐릭터: 칼린츠(0)가 서 있으면 칼린츠, 없으면 809 로 정한 인물이 서 있을 때 그 인물.
    /// (원본은 파티 +0x64 차례 하나 — 불러오기에서 그 값을 아직 안 남기므로 서 있는 인물로 고른다.)</summary>
    private readonly int _playerChar;

    private static int PlayerCharFor(Field f)
    {
        if (f.Actors.Count == 0 || f.Actors.Exists(a => a.Character == 0)) return 0;
        return GameState.Leader != 0 && f.Actors.Exists(a => a.Character == GameState.Leader) ? GameState.Leader : 0;
    }

    private void LoadCast()
    {
        var meshes = new Dictionary<int, CharacterMesh>();
        bool playerTaken = false;
        foreach (var a in _field.Actors)
        {
            if (a.Character == _playerChar && !playerTaken) { playerTaken = true; continue; }   // 첫 조작 캐릭터(보통 칼린츠) = 플레이어
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

    private readonly HashSet<FieldEvent> _zoneInside = [];

    private Cast? CastOf(int actor) => actor >= 10000 ? _cast.Find(c => c.Id == actor - 10000) : null;

    private void UpdateCast(float tk)
    {
        foreach (var c in _cast)
        {
            if (!c.Frozen) c.Tick += tk;
            if (c.Turn != null) c.Yaw = StepTurn(ref c.Turn, tk);
            if (c.AdvanceLeft > 0) { float st = Math.Min(tk, c.AdvanceLeft); c.AdvanceLeft -= st; c.Pos += new Vector3(MathF.Sin(c.Yaw), -MathF.Cos(c.Yaw), 0) * c.AdvanceSpeed * st; }
            if (c.Fading) { c.AlphaT = Math.Min(c.AlphaDur, c.AlphaT + tk); c.Alpha = c.AlphaFrom + (c.AlphaTo - c.AlphaFrom) * (c.AlphaT / c.AlphaDur); }
            if (c.Gliding) { c.GlideT = Math.Min(c.GlideDur, c.GlideT + tk); c.Pos = Vector3.Lerp(c.GlideFrom, c.GlideTo, c.GlideT / c.GlideDur); }
            if (c.WalkTo is { } goal)
            {
                var d = new Vector2(goal.X - c.Pos.X, goal.Y - c.Pos.Y);
                float step = (c.Running ? RunSpeed : WalkSpeed) * tk;
                if (d.Length() <= step + 1) { c.WalkTo = null; if (!c.KeepMotion) { c.Motion = CharacterRenderer.Stand; c.Tick = 0; } c.KeepMotion = false; }
                else
                {
                    var n = Vector2.Normalize(d);
                    c.Pos += new Vector3(n * step, 0);
                    if (_map.Ground.HeightAt(c.Pos.X, c.Pos.Y, c.Pos.Z + StepUp) is { } z) c.Pos.Z = z;
                    c.Yaw = MathF.Atan2(n.X, -n.Y);
                    int mv = c.Running ? CharacterRenderer.Run : CharacterRenderer.Walk;
                    if (!c.KeepMotion && c.Motion != mv) { c.Motion = mv; c.Tick = 0; }
                }
            }
            if (c.Next >= 0 && c.R.SequenceTicks(c.Motion) is int len && len > 0 && c.Tick >= len) { c.Motion = c.Next; c.Next = -1; c.Tick = 0; }
        }
    }

    private void DrawCast(Constants k)
    {
        foreach (var c in _cast)
        {
            if (c.Hidden || c.FadeMode == 3 && c.Alpha <= 0.01f) continue;
            c.R.Pose(c.R.HasSequence(c.Motion) ? c.Motion : CharacterRenderer.Stand, c.Tick);
            var kc = k;
            if (c.FadeMode == 3) kc.Tint = new Vector4(k.Tint.X, k.Tint.Y, k.Tint.Z, k.Tint.W * c.Alpha);
            c.R.Draw(kc, Matrix4x4.CreateRotationZ(c.Yaw) * Matrix4x4.CreateTranslation(c.Pos));
        }
    }

    private static readonly Lazy<Dictionary<int, CharParam>> Chars = new(() => CharParam.LoadAll().ToDictionary(c => c.Id));
}
