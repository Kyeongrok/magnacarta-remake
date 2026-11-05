using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Render;
using Vortice.Direct3D11;

namespace MagnaCarta.Scenes;

/// <summary>
/// 필드 하나에서 칼린츠를 움직인다. 맵·시작 자리는 fld 에서 읽는다(첫 조작 필드 531 로웰숲 남동부),
/// 몸은 AmdData 0000 칸 0 = 3DOBS 0101, 동작은 시퀀스 100 서기 · 101 걷기 · 102 달리기.
/// 원본에서 못 딴 값(걷기·달리기 속도, 틱 길이, 필드 카메라)은 지은 값이다 — 분석-렌더링-3D §8.
/// </summary>
internal sealed partial class FieldScene : IScene
{
    public const int NewGameField = 651;
    public const int FirstField = 531;   // 첫 조작 필드(분석-시작장면). 651·626·625·114 는 입력을 잠근 연출 필드

    // ── 지은 값(원본 미확인) ──
    // 사건 202 가 이동기에 주는 값(분석-틱과이동): 걷기 1.0 · 달리기 4.0 단위/틱 — 플레이어 조작도 같다고 봄(추정)
    private const float WalkSpeed = 1.0f, RunSpeed = 4.0f;
    private const float TurnRate = 0.15f * Timing.TicksPerSecond;   // 한 번에 0.15 rad(틱마다로 추정)
    private const float StepUp = 14f, StepDown = 60f;           // 한 번에 오르내릴 수 있는 높이

    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly Field _field;
    private readonly MapRenderer _map;
    private readonly CharacterRenderer _calintz;
    private readonly ID3D11ShaderResourceView _help;
    private readonly int _helpW, _helpH;

    private Vector3 _pos;
    private float _yaw;                  // 0 = 정면이 −Y
    private int _motion = CharacterRenderer.Stand;
    private float _motionTick;

    private float _camYaw, _camPitch = 0.42f, _camDist = 260f, _camNow = 260f;
    // 원본 필드 카메라(fld D 감시 카메라) — V 로 패치판 뒤따르는 카메라와 바꾼다
    private bool _originalCamera = true;
    private Vector3[] _cctvPos = [];
    private int _cctvActive = -1;
    private Vector3 _lastEye;

    // 출구: E 구역(조건 304) → 사건 4/5 GOTO (분석-도시 §2)
    private record Exit(int Region, int To, int EntryA, int EntryB, List<int[]>? Conds = null);
    private readonly List<Exit> _exits = [];
    private Exit? _leaving;
    // 무작위 조우 (분석-던전 §2): fld 0x10 단계 → 주기 P, P 틱이 차면 rand%(P/3)+1 틱 카운트다운, 0 이 되면 전투
    private static readonly int[] EncounterPeriod = [1300, 1100, 800, 400, 300];
    private float _encTicks, _encCountdown = -1;
    private static readonly Random Rng = new();

    // 마을 사람 (fld C 갈래 3, 분석-도시): +0x24 줄 아이템 · +0x26 준 표시 전역 스위치 · +0x28/2a/2c 대사 Fpb id(−1 끝)
    private sealed class Npc
    {
        public required Field.FieldObject O;
        public required CharacterRenderer R;
        public Vector3 Pos;
        public float Yaw, Tick;
        public short W(int i) => BitConverter.ToInt16(O.Extra, i * 2);
        public bool Chest => O.Kind is 6 or 7;
        /// <summary>갈래 2 = 3D 루프 물체(사건 300 켜기 · 302/303 멈춤, 추정). 켜기 전에는 첫 틀에 서 있다.</summary>
        public bool Loop => O.Kind == 2;
        public bool Playing;
        public float Speed = 1;
        public bool Opened;
        public int Seq => Loop ? 0 : Chest ? (Opened ? (Tick < 40 ? 1 : 2) : 0) : CharacterRenderer.Stand;
    }
    private readonly List<Npc> _npcs = [];
    private readonly Dictionary<int, string> _lines;
    private readonly Overlay _ui;
    private Npc? _talking;
    private int _lineIndex;
    private string? _dialog;
    private float _leaveTicks, _since, _enterFade = 20, _titleTime = 3.5f;
    private readonly ID3D11ShaderResourceView _black;
    private ID3D11ShaderResourceView? _title;
    private int _titleW, _titleH;

    /// <summary>발소리 표 바닥 9 × 8칸: 칸 0~3 = 걷기 wlk, 4~7 = 달리기 wlk (로더 0x5206f9f5). 파일마다 머리 int 3 + 바닥마다 int 4.</summary>
    private int[] FootSteps = new int[72];
    private static readonly int TestMotion = int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_MOTION"), out int tm) ? tm : 0;

    private static int[] LoadFootSteps(int charId)
    {
        var t = new int[72];
        var c = Data.CharParam.LoadAll().Find(x => x.Id == charId);
        if (c == null) return t;
        foreach (var (num, at) in new[] { (c.WalkSound, 0), (c.RunSound, 4) })
        {
            if (num < 0 || !McFiles.Exists("Walk", num, "wlk")) continue;
            var r = new Reader(McFiles.Read("Walk", num, "wlk"));
            r.Pos = 12;
            for (int f = 0; f < 9 && r.Pos + 16 <= r.Data.Length; f++)
                for (int k = 0; k < 4; k++) t[f * 8 + at + k] = r.I32();
        }
        return t;
    }

    public FieldScene(Gfx gfx, ScreenImage screen, int fieldNumber, (int A, int B)? entry = null, (Vector3 Pos, float Yaw)? at = null)
    {
        _gfx = gfx;
        _screen = screen;
        _field = Field.Load(fieldNumber);
        // 맵 없는 필드(−1, 사건만 있는 651 등)는 빈 맵
        _map = new MapRenderer(gfx, _field.MapNumber >= 0 && McFiles.Exists("MAP", _field.MapNumber, "wrd") ? WorldMap.Load(_field.MapNumber) : new WorldMap());
        // fld +4 = 배경음 번호로 보임(분석-스테이지 추정). 없는 번호·−1 이면 끈다
        if (_field.Music >= 0) Audio.Music.Play(_field.Music); else Audio.Music.Stop();

        FootSteps = LoadFootSteps(0);
        var model = Amd.Load(0).GetValueOrDefault(0, 101);
        _calintz = new CharacterRenderer(gfx, CharacterMesh.Load(model));

        // fld B 묶음의 인물 번호는 CharData id (칼린츠 = 0) — 분석-캐릭터
        var start = _field.Actors.Find(a => a.Character == 0)?.Position ?? _field.Objects.Find(o => o.Kind == 0)?.Position ?? Vector3.Zero;
        _pos = start;
        if (entry is { } en && _field.Objects.Find(o => o.Kind == 0 && o.Id == en.A) is { } mark)
        {
            // GOTO 5 의 a1 표지에 서서 a2 표지 쪽을 본다(추정)
            _pos = mark.Position;
            if (_field.Objects.Find(o => o.Kind == 0 && o.Id == en.B) is { } look && look != mark)
                _yaw = MathF.Atan2(look.Position.X - _pos.X, -(look.Position.Y - _pos.Y));
        }
        foreach (var ev in _field.Events)
        {
            // 스위치 조건은 밟을 때 지금 스위치로 따진다(지역 100 · 전역 101)
            if (!ev.Conditions.All(c => c[0] is 0 or 304 or 100 or 101)) continue;
            var conds = ev.Conditions.Where(c => c[0] is 100 or 101).ToList();
            foreach (var c in ev.Conditions.Where(c => c[0] == 304))
                foreach (var a in ev.Actions.Where(a => a[0] is 4 or 5))
                    _exits.Add(new Exit(c[2], a[1], a[0] == 5 ? a[2] : -1, a[0] == 5 ? a[3] : -1, conds));
        }
        _lines = Fpb.Load(_field.FpbNumber);
        // 소리 물체(갈래 0xB, 0x5206f242 가 단언): +0x28 word 가 켜짐 표시 — 600 이 1 로 둔다. 파일에서 이미 1 인 것은 들어오자마자 튼다
        Audio.SoundEffects.StopAllLoops();
        foreach (var so in _field.Objects.Where(o => o.Kind == 11 && o.Extra.Length >= 6 && BitConverter.ToInt16(o.Extra, 4) == 1))
            Audio.SoundEffects.StartLoop($"amb{so.Id}", so.Model);
        LoadCast();
        _ui = new Overlay(gfx, screen);
        var meshes = new Dictionary<int, CharacterMesh>();
        foreach (var o in _field.Objects.Where(o => o.Kind is 2 or 3 or 6 or 7 && McFiles.Exists("3DOBS", o.Model, "3do")))
        {
            try
            {
                if (!meshes.TryGetValue(o.Model, out var mesh)) meshes[o.Model] = mesh = CharacterMesh.Load(o.Model);
                var npc = new Npc { O = o, R = new CharacterRenderer(gfx, mesh), Pos = o.Position, Tick = Rng.Next(60) };
                // 돌림 값의 단위는 확인 못 함 — 라디안으로 본다
                npc.Yaw = o.Rotation.Z;
                // 상자: 시퀀스 0 닫힘 · 1 열림 · 2 열린 채. 이미 연 상자(전역 스위치 +0x26)는 열린 채로
                if (o.Kind is 6 or 7) npc.Opened = GameState.OpenedChests.Contains((_field.Number, o.Id));
                if (_map.Ground.HeightAt(npc.Pos.X, npc.Pos.Y, npc.Pos.Z + 60) is { } nz) npc.Pos.Z = nz;
                _npcs.Add(npc);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException) { }
        }
        var black = new Image32(1, 1);
        black.Fill(0, 0, 1, 1, 0, 0, 0);
        _black = gfx.Texture(black, false);
        if (_map.Ground.HeightAt(_pos.X, _pos.Y, _pos.Z + 200) is { } z) _pos.Z = z;
        if (at is { } a0) { _pos = a0.Pos; _yaw = a0.Yaw; }
        _camYaw = _yaw;
        if (_field.PlaceName.Length > 0) ShowTitle(_field.PlaceName);

        var font = new BitmapFont(0);
        const string help = "방향키/WASD 걷기   Shift 달리기   오른쪽 끌기 시점   휠 거리   I 상태   V 시점   F5 저장   B 전투 시험   Esc 메뉴";
        var img = new Image32(840, font.Height + 6);
        img.Fill(0, 0, img.Width, img.Height, 0, 0, 0, 120);
        int end = font.Draw(img, 6, 3, help, 220, 205, 170);
        _helpW = Math.Min(end + 6, img.Width); _helpH = img.Height;
        _help = gfx.Texture(img, false);
    }

    public Vector3 Position => _pos;
    public int FieldNumber => _field.Number;
    public string PlaceName => _field.PlaceName;


    private void ShowTitle(string text)
    {
        _title?.Dispose();
        var tf = new BitmapFont(0);
        var timg = new Image32(400, tf.Height + 10);
        timg.Fill(0, 0, timg.Width, timg.Height, 0, 0, 0, 140);
        _titleW = Math.Min(tf.Draw(timg, 10, 5, text, 235, 220, 180) + 10, timg.Width);
        _titleH = timg.Height;
        _title = _gfx.Texture(timg, false);
        _titleTime = 3.5f;
    }

    private bool UsingCctv => _originalCamera && _field.Cameras.Count > 0 && _cut == null;

    /// <summary>
    /// 감시 카메라 한 틀(0x5205282f): 가장 가까운 카메라를 골라(추정) 플레이어와 줄 길이를 지키도록
    /// 축마다 d̂·(L − 줄)/나눗수 만큼 옮기고 움직일 상자 안으로 자른다. 바라보는 점 높이는 못 땄다(+32, 지은 값).
    /// </summary>
    private Vector3 StepCctv(Vector3 target)
    {
        if (_cctvPos.Length != _field.Cameras.Count) _cctvPos = _field.Cameras.Select(c => c.Start).ToArray();
        int best = 0;
        for (int i = 1; i < _cctvPos.Length; i++)
            if (Vector3.Distance(_cctvPos[i], target) < Vector3.Distance(_cctvPos[best], target)) best = i;
        if (best != _cctvActive)
        {
            if (_cctvActive >= 0 && _field.Cameras[best].Smooth == 1)
                _cctvPos[best] = Vector3.Clamp(_cctvPos[_cctvActive], _field.Cameras[best].Min, _field.Cameras[best].Max);
            _cctvActive = best;
        }
        var cam = _field.Cameras[best];
        var d = target - _cctvPos[best];
        float l = d.Length();
        if (l > 1e-3f && MathF.Abs(l - cam.Rope) >= 1)
        {
            // 원본은 틀마다 — 패치판은 지난 틀의 틱 수만큼 되풀이한 것과 비슷하게 1 − (1 − 1/n)^틱
            float k = 1 - MathF.Pow(1 - 1f / cam.Divisor, Math.Max(_frameTicks, 0.01f));
            _cctvPos[best] += d / l * (l - cam.Rope) * k;
            _cctvPos[best] = Vector3.Clamp(_cctvPos[best], cam.Min, cam.Max);
        }
        return _cctvPos[best];
    }

    private float _frameTicks;

    private static bool Compare(int v, int op, int k) => op switch
    {
        0 => v == k, 1 => v < k, 2 => v <= k, 3 => v > k, 4 => v >= k, 5 => v != k, _ => false,
    };

    public IScene? Update(float seconds, Input input)
    {
        float tk = seconds * Timing.TicksPerSecond;
        _frameTicks = tk;
        GameState.PlayMs += seconds * 1000;
        _since += seconds; _titleTime -= seconds;
        _enterFade = Math.Max(0, _enterFade - tk);
        if (_leaving != null)
        {
            _leaveTicks += tk;
            return _leaveTicks >= 20 ? new FieldScene(_gfx, _screen, _leaving.To, _leaving.EntryA >= 0 ? (_leaving.EntryA, _leaving.EntryB) : null) : this;
        }
        foreach (var n in _npcs) if (!n.Loop || n.Playing) n.Tick += tk * n.Speed;
        UpdateCast(tk);
        // 소리 물체 크기: 가까울수록 크게(지은 식 — 원본 감쇠 값은 못 땄다), 1/4 초마다
        if ((_ambTimer -= seconds) <= 0)
        {
            _ambTimer = 0.25f;
            foreach (var so in _field.Objects.Where(o => o.Kind == 11))
            {
                string key = $"amb{so.Id}";
                if (!Audio.SoundEffects.IsLooping(key)) continue;
                float d = Vector3.Distance(so.Position, _pos);
                Audio.SoundEffects.SetLoopVolume(key, (int)(900 * Math.Clamp(1 - d / 1200f, 0.05f, 1)));
            }
        }
        if (RunEvents(tk, input) is { } next) return next;
        if (_leaving != null) return this;
        if (_locked || _scriptDialog)
        {
            if (_walkTo == null && _scriptMotionNext < 0 && !_calintz.HasSequence(_motion)) _motion = CharacterRenderer.Stand;
            _motionTick += tk;
            return this;
        }
        // 대화 중에는 움직이지 않는다. Space/Enter 로 넘긴다
        if (_dialog != null)
        {
            if (input.Pressed(Input.Space) || input.Pressed(Input.Enter) || input.LeftClicked) Advance();
            _motion = CharacterRenderer.Stand;
            _motionTick += tk;
            return this;
        }
        if (!_status && (input.Pressed(Input.Space) || input.Pressed(Input.Enter)) && _npcs.Where(n => !n.Loop && Vector2.Distance(new(n.Pos.X, n.Pos.Y), new(_pos.X, _pos.Y)) < 70f)
                .MinBy(n => Vector2.Distance(new(n.Pos.X, n.Pos.Y), new(_pos.X, _pos.Y))) is { } who)
        {
            if (who.Chest) { OpenChest(who); return this; }
            _talking = who; _lineIndex = -1;
            // 서로 마주 본다
            who.Yaw = MathF.Atan2(_pos.X - who.Pos.X, -(_pos.Y - who.Pos.Y));
            _yaw = MathF.Atan2(who.Pos.X - _pos.X, -(who.Pos.Y - _pos.Y));
            Advance();
            return this;
        }
        if (input.Pressed('I') && _dialog == null) { _status = !_status; _statusTab = 0; }
        else if (input.Pressed(0x09) && _dialog == null) { if (!_status) { _status = true; _statusTab = 0; } else _statusTab = (_statusTab + 1) % 4; }
        if (input.Pressed('V')) { _originalCamera = !_originalCamera; _camYaw = MathF.Atan2(_pos.X - _lastEye.X, -(_pos.Y - _lastEye.Y)); }   // 상태 창(패치판 — 원본 메뉴 화면은 아직 안 풂)
        if (_status)
        {
            if (_statusTab == 1 && _statusMember < GameState.Party.Count && Chars.Value.TryGetValue(GameState.Party[_statusMember], out var cc0))
            {
                // Carta 쪽: 위아래 줄(가슴·머리·팔·배·다리), [ 친화 +1 · ] 되돌리기
                if (input.Pressed(Input.Up)) _cartaRow = (_cartaRow + 4) % 5;
                if (input.Pressed(Input.DownKey)) _cartaRow = (_cartaRow + 1) % 5;
                var ce = GameState.Effective(cc0);
                int[] rowEl = [ce.Breast, ce.Head, ce.Arm, ce.Abdomen, ce.Leg];
                if (input.Pressed(0xDB)) GameState.ShiftAffinity(ce, rowEl[_cartaRow], +1);
                if (input.Pressed(0xDD)) GameState.ShiftAffinity(ce, rowEl[_cartaRow], -1);
            }
            // 좌우 파티원, 위아래 아이템, Enter 끼기(옛것은 가방으로)
            if (input.Pressed(Input.Left)) _statusMember = Math.Max(0, _statusMember - 1);
            if (input.Pressed(Input.Right)) _statusMember = Math.Min(GameState.Party.Count - 1, _statusMember + 1);
            var bag = GameState.Items.Distinct().ToList();
            if (input.Pressed(Input.Up)) _statusItem = Math.Max(0, _statusItem - 1);
            if (input.Pressed(Input.DownKey)) _statusItem = Math.Min(Math.Max(bag.Count - 1, 0), _statusItem + 1);
            if (input.Pressed(Input.Enter) && _statusItem < bag.Count && _statusMember < GameState.Party.Count)
                _statusNote = GameState.Equip(GameState.Party[_statusMember], bag[_statusItem]) ? "장비했다." : "장비할 수 없다.";
            _motion = CharacterRenderer.Stand; _motionTick += tk; return this;
        }
        if (input.Pressed(0x74))   // F5 세이브 (원본의 세이브 자리 규칙은 못 봤다 — 어디서나 된다)
        {
            SaveGame.Save(_field.Number, _pos, _yaw, _field.PlaceName);
            _dialog = $"저장했다. ({(_field.PlaceName.Length > 0 ? _field.PlaceName : $"필드 {_field.Number}")})";
            _talking = null;
        }
        // Esc = 원본 시스템 메뉴(여는 키는 원본에서 못 찾았다 — Esc 로 둠)
        if (_sysMenu) { if (UpdateSystemMenu(input) is { } sysNext) return sysNext; _motion = CharacterRenderer.Stand; _motionTick += tk; return this; }
        if (input.Pressed(Input.Escape)) { _sysMenu = true; _sysIndex = 0; return this; }
        if (input.Pressed('B')) return new BattleScene(_gfx, _screen);   // 시험: 지하수로 캐노펀 전투

        // 시점
        if (input.RightDown)
        {
            _camYaw -= input.DragX * 0.008f;
            _camPitch = Math.Clamp(_camPitch + input.DragY * 0.006f, 0.05f, 1.35f);
        }
        _camDist = Math.Clamp(_camDist * MathF.Pow(0.9f, input.Wheel), 90f, 900f);

        // 이동 — 카메라 기준. 카메라는 캐릭터 뒤(−앞) 에 있고 앞 = (sin yaw, −cos yaw)
        // 원본 카메라일 때는 지금 카메라가 보는 쪽이 「앞」
        if (UsingCctv) { var look = new Vector2(_pos.X - _lastEye.X, _pos.Y - _lastEye.Y); if (look.LengthSquared() > 1) _camYaw = MathF.Atan2(look.X, -look.Y); }
        var fwd = new Vector2(MathF.Sin(_camYaw), -MathF.Cos(_camYaw));
        // 원본 카메라 키는 오른손 기저(분석-시작장면) — 오른손 LookAt 에서 화면 오른쪽 = (앞.y, −앞.x)
        var right = new Vector2(fwd.Y, -fwd.X);
        var dir = Vector2.Zero;
        if (input.Down(Input.Up) || input.Down('W')) dir += fwd;
        if (input.Down(Input.DownKey) || input.Down('S')) dir -= fwd;
        if (input.Down(Input.Right) || input.Down('D')) dir += right;
        if (input.Down(Input.Left) || input.Down('A')) dir -= right;

        int motion = CharacterRenderer.Stand;
        if (dir.LengthSquared() > 0.01f)
        {
            dir = Vector2.Normalize(dir);
            bool run = input.Down(Input.Shift);
            motion = run ? CharacterRenderer.Run : CharacterRenderer.Walk;
            float step = (run ? RunSpeed : WalkSpeed) * seconds * Timing.TicksPerSecond;
            TryMove(dir * step);
            // 정면 −Y 를 dir 로 돌리는 yaw
            float target = MathF.Atan2(dir.X, -dir.Y);
            float diff = MathF.IEEERemainder(target - _yaw, MathF.Tau);
            _yaw += Math.Clamp(diff, -TurnRate * seconds, TurnRate * seconds);
        }
        if (motion != _motion) { _motion = motion; _motionTick = 0; }
        // 출구 구역에 들어서면 20틱 어두워진 뒤 다음 필드로. 들어온 직후 1초는 보지 않는다(들어선 자리가 구역 안일 때)
        // 조우 — 걷는 동안만 센다(원본이 멈춰 있을 때도 세는지는 못 봄, 지은 동작)
        if (motion != CharacterRenderer.Stand && _field.EncounterStep is >= 0 and <= 4 && _field.Stage >= 0 && _field.Groups.Any(g => g >= 0) && _since > 1f)
        {
            int period = EncounterPeriod[_field.EncounterStep];
            if (Environment.GetEnvironmentVariable("MC_TEST_ENCOUNTER") is { Length: > 0 }) period = 30;
            if (_encCountdown < 0) { _encTicks += tk; if (_encTicks >= period) { _encTicks = 0; _encCountdown = Rng.Next(Math.Max(1, period / 3)) + 1; } }
            else if ((_encCountdown -= tk) <= 0)
            {
                _encCountdown = -1;
                var groups = _field.Groups.Where(g => g >= 0 && McFiles.Exists("Mon", g, "Mon")).ToList();
                int bf = _field.BattleFields[Rng.Next(5)];     // 원본도 빈칸 검사 없이 rand%5
                int music = bf >= 0 && McFiles.Exists("Field", bf, "fld") ? Field.Load(bf).Music : 10;
                if (groups.Count > 0 && McFiles.Exists("MAP", _field.Stage, "wrd"))
                {
                    _motion = CharacterRenderer.Stand; _motionTick = 0;
                    return new BattleScene(_gfx, _screen, new BattleScene.Setup(_field.Stage, groups[Rng.Next(groups.Count)], GameState.Party.Take(4).ToArray(), 0, music, this, _field.Music) { BattleField = bf });
                }
            }
        }
        // 확인용: MC_TEST_EVOLVE=캐릭터 id 이면 1.2초 뒤 진화 사건(807)을 흉내 낸다
        if (_since > 1.2f && _since - seconds <= 1.2f && int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_EVOLVE"), out int tev)
            && Chars.Value.TryGetValue(tev, out var tevc) && GameState.NextSingi(tevc) is { Count: > 0 } tnx)
        {
            if (tnx.Count == 1) { GameState.Evolve(tev, tnx[0].Id); EvolveMessage(tevc, tnx[0]); }
            else { _choice = (tevc, tnx); _choiceIndex = 0; }
            _running ??= new FieldEvent();
        }
        // 확인용: MC_TEST_NPC=1 이면 1.2초 뒤 첫 마을 사람 옆으로
        if (_since > 1.2f && _since - seconds <= 1.2f && Environment.GetEnvironmentVariable("MC_TEST_NPC") is { Length: > 0 } && _npcs.Count > 0)
        {
            var tv = Environment.GetEnvironmentVariable("MC_TEST_NPC");
            var tn = tv == "chest" ? _npcs.FirstOrDefault(n => n.Chest) ?? _npcs[0] : int.TryParse(tv, out int ti) && ti < _npcs.Count ? _npcs[ti] : _npcs[0];
            _pos = tn.Pos + new Vector3(0, -55, 0);
        }
        // 확인용: MC_TEST_EVENTBATTLE=전투 필드 이면 1.2초 뒤 그 사건 전투(명령 9)를 연다
        if (_since > 1.2f && _since - seconds <= 1.2f && int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_EVENTBATTLE"), out int teb)
            && StartEventBattle(teb) is { } tbattle) return tbattle;
        // 확인용: MC_TEST_EXIT=k 이면 1.2초 뒤 k 번째 출구 구역 가운데로 옮긴다
        if (_since > 1.2f && _since - seconds <= 1.2f && int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_EXIT"), out int tex) && tex < _exits.Count
            && _field.Regions.Find(rg => rg.Id == _exits[tex].Region) is { } trg)
            _pos = (trg.Min + trg.Max) / 2;
        if (_since > 1f)
            foreach (var ex in _exits)
                if (_field.Regions.Find(rg => rg.Id == ex.Region) is { } rg && rg.Contains(_pos))
                {
                    // 원본 버그: 0638 이 없는 fld 0745 로 간다 — 패치판은 없는 필드면 막는다
                    if (!McFiles.Exists("Field", ex.To, "fld")) continue;
                    if (ex.Conds != null && !ex.Conds.All(c => CondOk(c, _localSwitches))) continue;
                    _leaving = ex; _leaveTicks = 0;
                    break;
                }
        float before = _motionTick;
        _motionTick += seconds * Timing.TicksPerSecond;
        foreach (var cmd in _calintz.CommandsBetween(_motion, before, _motionTick))
            if (cmd.Type == 10) Audio.SoundEffects.Play(cmd.B0);   // 모션 효과음(분석-모션명령)
            else if (cmd.Type == 11 && cmd.B0 is >= 0 and < 8 && FootSteps[_field.Floor * 8 + cmd.B0] > 0)
                Audio.SoundEffects.Play(FootSteps[_field.Floor * 8 + cmd.B0]);   // 발소리 = 표[바닥 × 8 + b0]
        return this;
    }

    /// <summary>대사 3줄을 차례로, 다 들은 뒤 아직 안 받았으면 아이템 하나(전역 스위치로 한 번만).</summary>
    private void Advance()
    {
        if (_talking == null) { _dialog = null; return; }
        var n = _talking;
        while (++_lineIndex < 3)
        {
            int id = n.W(2 + _lineIndex);
            if (id < 0) break;
            if (_lines.TryGetValue(id, out var text)) { _dialog = text; return; }
        }
        int item = n.W(0), sw = n.W(1);
        if (_lineIndex == 3 && item >= 0 && GameState.Switch(sw) == 0)
        {
            GameState.SetSwitch(sw, 1);
            GameState.Items.Add(item);
            _dialog = $"{Obj(ItemNames.Of(item))} 얻었다.";
            _lineIndex = 4;
            return;
        }
        _dialog = null; _talking = null;
    }

    /// <summary>
    /// 보물상자(fld C 갈래 6·7, 분석-도시): +0x24 아이템, +0x26 연 표시 전역 스위치, 갈래 7 은 +0x28 열쇠 아이템을 쓰고 없앤다.
    /// 원본 버그 고침: 열쇠 칸이 0 인 자물쇠 상자 5개는 아이템 0 「전설의 팔찌」를 열쇠로 먹는다 — 패치판은 모델로 은(657)·금(656) 열쇠를 고른다.
    /// </summary>
    private void OpenChest(Npc c)
    {
        _talking = null;
        if (c.Opened) { _dialog = "빈 상자다."; _lineIndex = 9; return; }
        if (c.O.Kind == 7)
        {
            int key = c.W(2);
            if (key == 0) key = c.O.Model == 656 ? 44 : 43;
            if (!GameState.Items.Remove(key)) { _dialog = $"잠겨 있다. {Josa(ItemNames.Of(key), "이", "가")} 필요하다."; _lineIndex = 9; return; }
        }
        c.Opened = true; c.Tick = 0;
        GameState.OpenedChests.Add((_field.Number, c.O.Id));
        GameState.SetSwitch(c.W(1), 1);   // 사건 조건이 볼 수 있게 원본처럼 스위치도 켠다
        int item = c.W(0);
        if (item >= 0) GameState.Items.Add(item);
        _dialog = item >= 0 ? $"{Obj(ItemNames.Of(item))} 얻었다." : "빈 상자다.";
        _lineIndex = 9;
    }

    private void TryMove(Vector2 d)
    {
        if (Step(d)) return;
        // 벽에 막히면 축 하나씩 미끄러진다
        if (Step(new Vector2(d.X, 0))) return;
        Step(new Vector2(0, d.Y));
    }

    private bool Step(Vector2 d)
    {
        if (d.LengthSquared() < 1e-8f) return false;
        float nx = _pos.X + d.X, ny = _pos.Y + d.Y;
        var h = _map.Ground.HeightAt(nx, ny, _pos.Z + StepUp);
        if (h is not { } z || z < _pos.Z - StepDown) return false;
        _pos = new Vector3(nx, ny, z);
        return true;
    }

    public void Draw()
    {
        _gfx.Begin(new Vortice.Mathematics.Color4(0.05f, 0.06f, 0.09f, 1));
        var target = _pos + new Vector3(0, 0, 32);
        var back = new Vector3(-MathF.Sin(_camYaw), MathF.Cos(_camYaw), 0);   // 앞의 반대
        var arm = back * MathF.Cos(_camPitch) + Vector3.UnitZ * MathF.Sin(_camPitch);
        // 나무·벽이 사이에 끼면 카메라를 앞으로 당긴다 (원본 필드 카메라는 못 땄다 — 지은 동작)
        float hit = _map.Ground.Raycast(target, target + arm * (_camDist + 12));
        float want = hit < 1 ? Math.Max(24f, hit * (_camDist + 12) - 12) : _camDist;
        _camNow = want < _camNow ? want : _camNow + (want - _camNow) * 0.15f;
        var eye = target + arm * _camNow;
        if (UsingCctv) eye = StepCctv(target);
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitZ);
        _lastEye = eye;
        float fov = MathF.PI / 4;
        if (_cut != null)
        {
            // 사건 카메라 컷(오른손 기저 — 앞 = −행2, 위 = 행1)
            var (ce, cf, cu, cfov) = _cut.At(_cutTick);
            eye = ce;
            view = Matrix4x4.CreateLookAt(ce, ce + cf, cu);
            if (cfov is > 5 and < 170) fov = cfov * MathF.PI / 180;
        }
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, _gfx.Width / (float)Math.Max(_gfx.Height, 1),
            Math.Max(_field.Near, 5f), Math.Max(_field.Far, 5000f));

        var amb = new Vector3(_field.Ambient.X, _field.Ambient.Y, _field.Ambient.Z);
        var c = new Constants
        {
            ViewProjection = view * proj,
            World = Matrix4x4.Identity,
            Tint = Vector4.One,
            LightDir = new Vector4(Vector3.Normalize(new Vector3(0.4f, 0.6f, -1f)), 0),
            LightColor = new Vector4(0.9f, 0.8f, 0.6f, 1),
            Ambient = new Vector4(amb * 1.5f + new Vector3(0.3f), 1),
            Fog = new Vector4(2500, 9000, 0, 0),
            FogColor = new Vector4(0.05f, 0.06f, 0.09f, 1),
            CameraPos = new Vector4(eye, 1),
        };
        _map.Draw(c);

        DrawCast(c);
        foreach (var n in _npcs)
        {
            int seq = n.Seq;
            n.R.Pose(n.R.HasSequence(seq) ? seq : 0, n.Tick);
            n.R.Draw(c, Matrix4x4.CreateRotationZ(n.Yaw) * Matrix4x4.CreateTranslation(n.Pos));
        }
        if (TestMotion > 0) _motion = TestMotion;   // 확인용 MC_TEST_MOTION=시퀀스
        _calintz.Pose(_motion, _motionTick);
        _calintz.Draw(c, Matrix4x4.CreateRotationZ(_yaw) * Matrix4x4.CreateTranslation(_pos));

        // 조작 도움말은 직접 움직일 수 있을 때만(컷씬·대사 중엔 가린다)
        if (!_locked && !_scriptDialog && _dialog == null && _running == null && !_status && !_sysMenu)
            _screen.Draw(_help, 10, _gfx.Height - _helpH * 1.5f - 10, _helpW * 1.5f, _helpH * 1.5f, Vector4.One);
        if (_title != null && _titleTime > 0)
            _screen.Draw(_title, (_gfx.Width - _titleW * 2f) / 2, 40, _titleW * 2f, _titleH * 2f, new Vector4(1, 1, 1, Math.Clamp(_titleTime, 0, 1)));
        _ui.Clear();
        if (_dialog != null)
        {
            // 대사창 — 원본 배치·바탕은 못 땄다(분석-시작장면: 추정). 아래쪽 반투명 상자
            int w = Math.Min(_ui.Width - 40, 520), h = 64, x = (_ui.Width - w) / 2, y = _ui.Height - h - 30;
            _ui.Box(x, y, w, h, 10, 8, 6, 200);
            _ui.Box(x, y, w, 1, 169, 146, 109); _ui.Box(x, y + h - 1, w, 1, 169, 146, 109);
            int tx = x + 10;
            if (_face != null)
            {
                // 얼굴 BGR(256×256)을 상자 왼쪽에 줄여 얹는다(원본 자리는 추정)
                // 그림 오른쪽(x ≈ 150~240)에 영문 이름이 찍혀 있어 통째로 줄이면 「----」 처럼 보였다 → 왼쪽 얼굴 칸(0~150 × 56~256)만
                int fw = 60, fh = 80;
                _ui.Box(x + 6, y - fh + h - 6, fw, fh, 0, 0, 0, 160);
                BlendScaled(_ui.Image, _face, x + 6, y - fh + h - 6, fw, fh, 0, 56, 150, 200);
                tx = x + fw + 14;
            }
            int ly = y + 8;
            if (_speaker.Length > 0) { _ui.Text(tx, y - 16, _speaker, 255, 220, 140); }
            // 사건 대사는 틀마다 5px 씩 드러난다(MCTalkBox)
            int budget = _scriptDialog ? (int)(_dialogAge * 5) : int.MaxValue;
            foreach (var line in Wrap(_dialog, x + w - 10 - tx))
            {
                string shown = line;
                while (shown.Length > 0 && _ui.TextWidth(shown) > budget) shown = shown[..^1];
                budget -= _ui.TextWidth(line);
                _ui.Text(tx, ly, shown, 235, 225, 200); ly += 16; if (ly > y + h - 14 || budget <= 0) break;
            }
        }
        else if (!_locked && _npcs.Any(n => !n.Loop && !(n.Chest && n.Opened) && Vector2.Distance(new(n.Pos.X, n.Pos.Y), new(_pos.X, _pos.Y)) < 70f))
            _ui.Text(_ui.Width / 2 - 40, _ui.Height - 60, _npcs.Where(n => !n.Loop && !(n.Chest && n.Opened) && Vector2.Distance(new(n.Pos.X, n.Pos.Y), new(_pos.X, _pos.Y)) < 70f).MinBy(n => Vector2.Distance(new(n.Pos.X, n.Pos.Y), new(_pos.X, _pos.Y)))!.Chest ? "Space 열기" : "Space 말 걸기", 255, 230, 150);
        if (_status && _statusTab == 3) DrawStatus();
        DrawChoice();
        if (_status && _statusTab is 0 or 1 or 2) { _ui.Present(); DrawOriginalStatus(); goto fade; }
        if (_sysMenu) { _ui.Present(); DrawSystemMenu(); goto fade; }
        if (_frame != null) { _ui.Present(); DrawFrame(); goto fade; }
        _ui.Present();
        fade:
        float fade = _leaving != null ? Math.Clamp(_leaveTicks / 20f, 0, 1) : _enterFade / 20f;
        if (fade > 0) _screen.Draw(_black, 0, 0, _gfx.Width, _gfx.Height, new Vector4(1, 1, 1, fade));
    }

    private float _ambTimer;
    private bool _status;
    private int _statusMember, _statusItem, _cartaRow;
    private string _statusNote = "";

    /// <summary>파티 능력치(원본 파생 식) · 신기 · 가진 아이템 — 패치판 상태 창.</summary>
    private void DrawStatus()
    {
        int w = _ui.Width - 60, h = _ui.Height - 90, x = 30, y = 30;
        _ui.Box(x, y, w, h, 8, 8, 12, 215);
        _ui.Box(x, y, w, 1, 169, 146, 109); _ui.Box(x, y + h - 1, w, 1, 169, 146, 109);
        _ui.Text(x + 10, y + 6, "파티", 255, 220, 140);
        var singi = Singis.Value;
        int cy = y + 24;
        for (int pi = 0; pi < GameState.Party.Count; pi++)
        {
            int id = GameState.Party[pi];
            if (!Chars.Value.TryGetValue(id, out var c0)) continue;
            var c = GameState.Effective(c0);
            string sg = singi.TryGetValue(c.Singi, out var sgi) ? sgi.Name : "-";
            bool sel = pi == _statusMember;
            _ui.Text(x + 10, cy, (sel ? "> " : "  ") + c.Name, 255, sel ? (byte)230 : (byte)230, sel ? (byte)120 : (byte)200);
            var eq = GameState.Slots(id).Select(i => i >= 0 ? ItemNames.Of(i) : "-");
            _ui.Text(x + 260, cy + 15, "장비 " + string.Join(" / ", eq), 200, 190, 170);
            _ui.Text(x + 80, cy, $"HP {c.MaxHp}  공격 {c.PhysicalAttack}  기술 {c.SkillAttack}  방어 {c.Armor}  민첩 {c.Agility}  WTP {c.Wtp}", 210, 210, 200);
            _ui.Text(x + 80, cy + 15, $"신기 {sg}", 180, 200, 230);
            cy += 34;
        }
        _ui.Text(x + 10, cy + 4, $"아이템 {GameState.Items.Count}개", 255, 220, 140);
        cy += 22;
        var bag = GameState.Items.Distinct().ToList();
        for (int k = 0; k < bag.Count && cy < y + h - 36; k++)
        {
            var it = ItemNames.Get(bag[k]);
            int cnt = GameState.Items.Count(i => i == bag[k]);
            bool sel = k == _statusItem;
            string slot = it == null ? "" : it.Slot switch { 0 => "몸", 1 => "반지", 2 => "목걸이", 3 => "기타", _ => "열쇠" };
            string t = (sel ? "> " : "  ") + ItemNames.Of(bag[k]) + (cnt > 1 ? " x" + cnt : "") + "  [" + slot + "]";
            _ui.Text(x + 10, cy, t, 255, sel ? (byte)230 : (byte)215, sel ? (byte)120 : (byte)200);
            if (sel && it != null) _ui.Text(x + 240, cy, it.Description, 180, 190, 200);
            cy += 15;
        }
        _ui.Text(x + 10, y + h - 18, "좌우 사람  위아래 아이템  Enter 장비   " + _statusNote, 180, 170, 150);
        _ui.Text(x + w - 110, y + h - 18, "Tab 상태  I 닫기", 180, 170, 150);
    }

    private static readonly Lazy<Dictionary<int, Singi>> Singis = new(BattleTables.LoadSingi);

    /// <summary>이름 + 을/를 — 마지막 한글 글자에 받침이 있으면 「을」. 한글이 아니면 「을(를)」.</summary>
    private static string Obj(string name) => Josa(name, "을", "를");

    /// <summary>이름 + 조사 — 받침 있으면 <paramref name="withBatchim"/>, 없으면 <paramref name="without"/>, 한글이 아니면 둘 다.</summary>
    private static string Josa(string name, string withBatchim, string without)
    {
        char last = name.TrimEnd().LastOrDefault();
        if (last is < '가' or > '힣') return $"{name}{withBatchim}({without})";
        return name + ((last - '가') % 28 != 0 ? withBatchim : without);
    }

    private static void BlendScaled(Image32 dst, Image32 src, int x, int y, int size) => BlendScaled(dst, src, x, y, size, size, 0, 0, src.Width, src.Height);

    /// <summary>src 의 (sx0, sy0, sw, sh) 칸을 w×h 로 줄여 얹는다.</summary>
    private static void BlendScaled(Image32 dst, Image32 src, int x, int y, int w, int h, int sx0, int sy0, int sw, int sh)
    {
        var tmp = new Image32(w, h);
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int sx = Math.Min(sx0 + xx * sw / w, src.Width - 1), sy = Math.Min(sy0 + yy * sh / h, src.Height - 1);
                Buffer.BlockCopy(src.Bgra, (sy * src.Width + sx) * 4, tmp.Bgra, (yy * w + xx) * 4, 4);
            }
        dst.Blend(tmp, x, y);
    }

    private IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var para in text.Split((char)10))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                var t = line.Length == 0 ? word : line + " " + word;
                if (_ui.TextWidth(t) > width && line.Length > 0) { yield return line; line = word; }
                else line = t;
            }
            yield return line;
        }
    }

    public void Dispose()
    {
        foreach (var n in _npcs) n.R.Dispose();
        foreach (var cst in _cast) cst.R.Dispose();
        _ui.Dispose();
        DisposeMenu();
        Audio.SoundEffects.StopAllLoops();
        _help.Dispose();
        _black.Dispose();
        _title?.Dispose();
        _calintz.Dispose();
        _map.Dispose();
    }
}
