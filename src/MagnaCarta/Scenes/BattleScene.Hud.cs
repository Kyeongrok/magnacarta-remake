using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Render;
using Vortice.Direct3D11;

namespace MagnaCarta.Scenes;

/// <summary>
/// 원본 전투 화면 부품(분석-전투화면, 800×600): 640판 좌표에 BASE (0, H−480) 를 더해 아래에 붙이고, 오른쪽 부품은 RIGHT (W−640, 0) 도 더한다.
/// 바닥 띠 FrameData 206, 파티 카드(지금 차례 밝은 215~ · 나머지 어두운 177~) 를 그릇 216 자리 + (116i, 0) 에, HP 「%4d / %4d」 카드 (28, 20),
/// TP 막대 58, 차례 얼굴(칼린츠 25 · 적 40), 명령 상자 56 에 Attack 238/239 · Ability 43/44 · Turn End 53/54.
/// </summary>
internal sealed partial class BattleScene
{
    private const int HudW = 800, HudH = 600;
    private static readonly Vector2 Base = new(0, HudH - 480), Right = new(HudW - 640, 0);
    private static readonly Dictionary<int, int> CardBright = new() { [0] = 215, [1] = 218, [2] = 219, [3] = 217, [4] = 220, [5] = 222, [6] = 221, [20] = 223 };
    private static readonly Dictionary<int, int> CardDark = new() { [0] = 177, [1] = 179, [2] = 181, [3] = 178, [4] = 182, [5] = 184, [6] = 183, [20] = 185 };
    private static readonly Dictionary<int, (Image32 Img, int X, int Y)> Frames = [];
    // 차례 얼굴은 CharData +0xa0(분석-전투효과). 이 표는 그 칸이 0 일 때만 쓰는 예전 짐작
    private static readonly Dictionary<int, int> TurnFaces = new() { [0] = 25, [1] = 26, [2] = 27, [6] = 28, [20] = 29, [3] = 30 };

    private readonly Image32 _hud = new(HudW, HudH);
    private ID3D11Texture2D? _hudTex;
    private ID3D11ShaderResourceView? _hudView;
    private BitmapFont? _hudFont;
    private int _tpPct = 100;

    private static (Image32 Img, int X, int Y) F(int n)
    {
        if (!Frames.TryGetValue(n, out var f))
        {
            if (McFiles.Exists("FrameData", n, "fdi")) { var fd = FrameData.Load(n); f = (fd.Compose(cover: CardBright.ContainsValue(n) || CardDark.ContainsValue(n)), fd.X, fd.Y); }   // 카드는 FrameData 크기 80×150 보다 그림(128)이 넓다 — 자르지 않음
            else f = (new Image32(1, 1), 0, 0);
            Frames[n] = f;
        }
        return f;
    }

    private void Put(int n, Vector2 at) => _hud.Blend(F(n).Img, (int)at.X, (int)at.Y);
    /// <summary>HP × 100 / 최대 HP(0x5208e28c / 0x5208e244, 버림).</summary>
    private static int HpPct(Fighter f) => f.MaxHp > 0 ? Math.Clamp(f.Hp * 100 / f.MaxHp, 0, 100) : 0;
    private static Vector2 Pos(int n) => new(F(n).X, F(n).Y);

    private void DrawHud(Matrix4x4 vp)
    {
        _hudFont ??= new BitmapFont(0);
        Array.Clear(_hud.Bgra);
        Put(206, Base);
        var box = Pos(216);
        int i = 0;
        foreach (var f in Party)
        {
            bool turn = f == _actor && _phase is Phase.Command or Phase.Acting;
            var table = turn ? CardBright : CardDark;
            if (table.TryGetValue(f.P.Id, out int card))
            {
                var at = box + Base + new Vector2(116 * i, 0);
                // HP 막대: 카드 = 막대 객체(graph(카드, WinDiData 43, 1), 0x520abe44) — 카드 그림 BGR 의 막대 칸 (7, 42)~(107, 46) 에
                // 같은 초록 (0, 255, 126) 을 가로 HP %, 세로 120 %(4 × 1.2 → 4) 로 깔고 카드를 얹는다
                _hud.Fill((int)at.X + 7, (int)at.Y + 42, 100 * HpPct(f) / 100, 4, 0, 255, 126);
                Put(card, at);
                string hp = $"{f.Hp,4} / {f.MaxHp,4}";
                _hudFont.Draw(_hud, (int)at.X + 28, (int)at.Y + 20, hp, 255, 255, 255);
            }
            else
            {
                // 카드 그림이 없는 인물(칼린츠(아이) 22 등) — 패치판: 어두운 칸에 이름·체력만
                var at = box + Base + new Vector2(116 * i, 0);
                _hud.Fill((int)at.X + 4, (int)at.Y + 2, 108, 44, (byte)(turn ? 70 : 30), (byte)(turn ? 50 : 30), (byte)(turn ? 20 : 30), 200);
                _hudFont.Draw(_hud, (int)at.X + 8, (int)at.Y + 4, f.P.Name, 255, 255, 255);
                _hudFont.Draw(_hud, (int)at.X + 28, (int)at.Y + 20, $"{f.Hp,4} / {f.MaxHp,4}", 255, 255, 255);
                _hud.Fill((int)at.X + 7, (int)at.Y + 42, 100 * HpPct(f) / 100, 4, 0, 255, 126);
            }
            i++;
        }
        // TP 막대 m_pTPGraph(0x520596a0): 그림 58 뒤에 WinDiData 31 색(0, 255, 126) 사각형을 남은 TP % 만큼 깐다(0x5205996f — 가로 = 칸 × %/100).
        // 값은 턴 시작 100(0x520a46c9), 그 뒤 (지금 TP − 쓸 TP) × 100 / 최대(0x520a4bc4) — 아군 차례에만 바뀌고 적 차례엔 그대로 둔다.
        if (_actor is { Enemy: false } a0 && a0.P.Tp > 0) _tpPct = Math.Clamp(a0.Tp * 100 / a0.P.Tp, 0, 100);
        var tpAt = Pos(58) + Base + Right;
        _hud.Fill((int)tpAt.X + 7, (int)tpAt.Y + 12, 150 * _tpPct / 100, 19, 0, 255, 126);   // 칸 = BGR 96 의 막대 칸 (7, 12)~(157, 28), 세로 120 %
        Put(58, tpAt);
        if (_actor != null) Put(_actor.P.TurnFace > 0 ? _actor.P.TurnFace : _actor.Enemy ? 40 : TurnFaces.GetValueOrDefault(_actor.P.Id, 25), new Vector2(583, 306) + Base + Right);
        // 크래시 스택 「0~3 CRASH STACKS」 FrameData 258~261 — 차례가 아군이면 그 단계, 아니면 숨김(0x520b0dc7, 640판 (537, 357))
        if (_actor is { Enemy: false } && _phase is Phase.Command or Phase.Acting) Put(258 + Math.Clamp(_actor.Apocalypse, 0, 3), new Vector2(537, 357) + Base + Right);
        if (_phase == Phase.Command && _actor is { Enemy: false })
        {
            var mb = Pos(56);
            // 필살 단계 3(Value 56)이면 첫 단추가 Attack 대신 Crazy Mode 47/48 — 오버크래쉬를 쓸 수 있다는 표시(0x520b020b)
            (int N, int O)[] buttons = [_actor.Apocalypse >= 3 ? (47, 48) : (238, 239), (43, 44), (53, 54)];
            for (int k = 0; k < 3; k++) Put(k == 0 ? buttons[k].O : buttons[k].N, mb + new Vector2(0, 45 * k));
        }
        // 피해 숫자: 흰 89~98, 회복 초록 79~88 (뜻은 색으로 추정)
        foreach (var p in _popups)
        {
            var h = Vector4.Transform(new Vector4(p.At + new Vector3(0, 0, p.Age * 30), 1), vp);
            if (h.W <= 0.1f) continue;
            float sx = (h.X / h.W * 0.5f + 0.5f) * HudW, sy = (0.5f - h.Y / h.W * 0.5f) * HudH;
            bool heal = p.Text.StartsWith('+');
            string digits = p.Text.TrimStart('+');
            if (!digits.All(char.IsDigit)) continue;
            int x = (int)sx - digits.Length * 7;
            foreach (char ch in digits) { Put((heal ? 79 : 89) + (ch - '0'), new Vector2(x, sy)); x += 14; }
        }
        if (_phase == Phase.Won)
        {
            // 결과 창 FrameData 245 「Carta Result」 를 (601, 196) 에, 속성 7줄 「%4d」 를 창 (128, 62 + 24i) 에(0x520ac2b9)
            var at = new Vector2(601, 196);
            Put(245, at);
            for (int k = 0; k < 7; k++) _hudFont.Draw(_hud, (int)at.X + 128, (int)at.Y + 62 + 24 * k, $"{_cartaGain[k],4}", 255, 255, 255);
        }
        _hudTex ??= _gfx.DynamicTexture(HudW, HudH, out _hudView);
        _gfx.UploadTexture(_hudTex, _hud);
        var (fx, fy, fw, fh) = _screen.Fit(HudW, HudH);
        _screen.Draw(_hudView!, fx, fy, fw, fh, Vector4.One);
    }

    // ── 3D 효과(연출 300, 분석-전투효과) ──
    private sealed class Fx
    {
        public required CharacterRenderer R; public Vector3 Pos; public float Yaw, Tick, Life; public int Seq, Slot;
        public float Scale = 1, ScaleFrom = 1, ScaleTo = 1, ScaleT = 1, ScaleDur = 1;
        public Vector3 MoveFrom, MoveTo; public float MoveT = 1, MoveDur = 1;
        public bool UntilEnd;
    }
    // 켜기 전에 정한 크기(310 은 300 보다 먼저 오기도 한다)
    private readonly Dictionary<int, float> _fxPendingScale = [];
    private readonly List<Fx> _fx = [];
    private readonly Dictionary<int, CharacterRenderer?> _fxModels = [];

    private void SpawnEffect(Affect sc, Affect.Step st, Fighter caster, Fighter target)
    {
        if (!sc.Effects.TryGetValue(st.Group, out int model)) return;
        if (!_fxModels.TryGetValue(model, out var r))
        {
            r = null;
            try { if (McFiles.Exists("3DOBS", model, "3do")) r = new CharacterRenderer(_gfx, CharacterMesh.Load(model)); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException) { }
            _fxModels[model] = r;
        }
        if (r == null) return;
        // 자리 = 기준점 + RotZ(yaw)·오프셋 + (0, 0, 키 × {0, ½, 1}) (0x5209341e)
        Vector3 pos = Vector3.Zero; float yaw = 0;
        if (sc.Spots.TryGetValue(st.Arg4, out var sp) && sp.Role is 0 or 1)
        {
            var who = sp.Role == 0 ? caster : target;
            var basePos = sp.PosMode == 1 ? who.Home : who.Pos;
            yaw = who.Yaw;
            float h = who.P.BodyHeight * (sp.Height switch { 1 => 0.5f, 2 => 1f, _ => 0f });
            pos = basePos + Vector3.Transform(sp.Offset, Matrix4x4.CreateRotationZ(yaw)) + new Vector3(0, 0, h);
        }
        int len = Math.Max(r.SequenceTicks(st.Value), 10);
        float life = st.Arg3 < 0 ? 90 : len * Math.Max(1, st.Arg3);   // −1(끝없이)은 패치판이 3초로 자름
        _fxPendingScale.Remove(st.Group, out float sc0);
        _fx.Add(new Fx { R = r, Pos = pos, Yaw = yaw, Seq = st.Value, Life = st.Arg3 < 0 ? float.MaxValue : life, UntilEnd = st.Arg3 < 0, Slot = st.Group,
            Scale = sc0 > 0 ? sc0 : 1, ScaleFrom = sc0 > 0 ? sc0 : 1, ScaleTo = sc0 > 0 ? sc0 : 1 });
    }

    // ── 연출 카메라(500): 배우 기준이면 그 배우의 자리·회전으로 옮겨 튼다(행렬 곱 차례는 추정) ──
    private (CameraCut Cut, Matrix4x4 Basis, float Tick)? _fxCam;

    private void StartFxCamera(Affect sc, Affect.Step st, Fighter caster, Fighter target)
    {
        if (!sc.Cameras.TryGetValue(st.Group, out int num) || Affect.LoadCamera(num) is not { } cut) return;
        var basis = Matrix4x4.Identity;
        if (sc.Spots.TryGetValue(st.Arg3, out var sp) && sp.Role is 0 or 1)
        {
            var who = sp.Role == 0 ? caster : target;
            basis = Matrix4x4.CreateRotationZ(who.Yaw) * Matrix4x4.CreateTranslation(sp.PosMode == 1 ? who.Home : who.Pos);
        }
        _fxCam = (cut, basis, 0);
    }

    private bool FxCameraView(out Matrix4x4 view, out float fov, out Vector3 eye)
    {
        view = default; fov = MathF.PI / 4; eye = default;
        if (_fxCam is not { } fc || fc.Tick >= fc.Cut.Length) return false;
        var (e, f, u, fv) = fc.Cut.At(fc.Tick);
        eye = Vector3.Transform(e, fc.Basis);
        var fw = Vector3.TransformNormal(f, fc.Basis);
        var up = Vector3.TransformNormal(u, fc.Basis);
        view = Matrix4x4.CreateLookAt(eye, eye + fw, up);
        if (fv is > 5 and < 170) fov = fv * MathF.PI / 180;
        return true;
    }

    private Vector3 SpotPos(Affect sc, int spot, Fighter caster, Fighter target, out float yaw)
    {
        yaw = 0;
        if (!sc.Spots.TryGetValue(spot, out var sp) || sp.Role is not (0 or 1)) return Vector3.Zero;
        var who = sp.Role == 0 ? caster : target;
        yaw = who.Yaw;
        float h = who.P.BodyHeight * (sp.Height switch { 1 => 0.5f, 2 => 1f, _ => 0f });
        return (sp.PosMode == 1 ? who.Home : who.Pos) + Vector3.Transform(sp.Offset, Matrix4x4.CreateRotationZ(yaw)) + new Vector3(0, 0, h);
    }

    /// <summary>310 크기(값/100 배, (틀+1)틀) · 306 옮기기(자리로, 틀) · 302/303 끄기.</summary>
    private void EffectCommand(Affect sc, Affect.Step st, Fighter caster, Fighter target)
    {
        var f = _fx.FindLast(e => e.Slot == st.Group);
        switch (st.Op)
        {
            case 310:
                float to = st.Value / 100f;
                if (f == null) { _fxPendingScale[st.Group] = to; break; }
                f.ScaleFrom = f.Scale; f.ScaleTo = to; f.ScaleT = 0; f.ScaleDur = Math.Max(1, st.Arg3 + 1);
                break;
            case 306 when f != null:
                f.MoveFrom = f.Pos; f.MoveTo = SpotPos(sc, st.Value, caster, target, out _); f.MoveT = 0; f.MoveDur = Math.Max(1, st.Arg3);
                break;
            case 303 when f != null: f.Life = 0; break;
        }
    }

    /// <summary>행동이 끝나면 끝없이 도는 효과(되풀이 −1)를 끈다.</summary>
    private void EndActionEffects() { _fx.RemoveAll(f => f.UntilEnd); _fxPendingScale.Clear(); }

    private void UpdateEffects(float ticks)
    {
        foreach (var f in _fx)
        {
            if (f.ScaleT < f.ScaleDur) { f.ScaleT = Math.Min(f.ScaleDur, f.ScaleT + ticks); f.Scale = f.ScaleFrom + (f.ScaleTo - f.ScaleFrom) * (f.ScaleT / f.ScaleDur); }
            if (f.MoveT < f.MoveDur) { f.MoveT = Math.Min(f.MoveDur, f.MoveT + ticks); f.Pos = Vector3.Lerp(f.MoveFrom, f.MoveTo, f.MoveT / f.MoveDur); }
        }
        if (_fxCam is { } fc) _fxCam = fc.Tick + ticks >= fc.Cut.Length ? null : (fc.Cut, fc.Basis, fc.Tick + ticks);
        foreach (var f in _fx) f.Tick += ticks;
        _fx.RemoveAll(f => f.Tick >= f.Life);
    }

    private void DrawEffects(Constants c)
    {
        foreach (var f in _fx)
        {
            f.R.Pose(f.Seq, f.Tick);
            f.R.DrawAdditive(c, Matrix4x4.CreateScale(f.Scale) * Matrix4x4.CreateRotationZ(f.Yaw) * Matrix4x4.CreateTranslation(f.Pos));
        }
    }

    private void DisposeHud() { _hudView?.Dispose(); _hudTex?.Dispose(); foreach (var r in _fxModels.Values) r?.Dispose(); }
}
