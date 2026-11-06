using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Render;
using Vortice.Direct3D11;

namespace MagnaCarta.Scenes;

/// <summary>
/// 시작 화면(원본 MCTitle.cpp 메뉴 상태). 800×600 판: 배경 FrameData 210(BGR 363~374),
/// 단추 FrameData 224~231(BGR 400~407)을 (342, 362 + 35k) 에, 판 번호를 오른쪽 아래에.
/// 검정에서 50틱 페이드 인, 누르면 20틱 페이드 아웃. — 분석-렌더링-2D
/// 원본은 이 앞에 로고(MOV 0023)·오프닝(MOV 0010) Bink 동영상을 트는데, Bink 풀개가 없어 건너뛴다.
/// </summary>
internal sealed class TitleScene : IScene
{
    private const int ScreenW = 800, ScreenH = 600;
    private const int ButtonX = 342, ButtonY = 362, ButtonStep = 35;
    private static readonly (int Normal, int Over)[] ButtonFrames = [(225, 224), (227, 226), (229, 228), (231, 230)];
    // Load game · Continue 는 패치판 세이브 한 칸(SaveGame)을 읽는다 — 없으면 흐리게
    // Load game = 패치판 세이브 + 원본 MC00~03.sav 고르기, Continue = 패치판 세이브
    private readonly bool[] Enabled = [true, SaveGame.AnyExists() || Enumerable.Range(0, SaveGame.Slots).Any(i => OriginalSave.Peek(i) != null), SaveGame.AnyExists(), true];

    // 불러오기 — 원본 LOAD GAME 창(FrameData 109, 250×405)을 가운데에. 칸 줄은 필드 메뉴와 같은 자리(AUTO y69, 1~10 y117+25k).
    // 쪽 0 = 패치판 칸(AUTO + 01~10), 쪽 1 = 원본 MCnn.sav — 칸 단추 명령 0x5e~0x68 에서 0x5e 를 뺀 수가 파일 번호(0x520810c7 → 0x5207021e 의 "MC%02d.sav")라
    // 첫 줄 AUTO = MC00, 1~10 = MC01~MC10(줄 차례는 단추 id 차례로 추정). 좌우로 쪽 바꿈.
    // 줄마다 (_chosen 값, 장소, 시간) — 빈 줄은 null. _chosen: 110 + 칸 = 패치판, 10 + i = 원본
    private (int Code, string Place, TimeSpan Play)?[]? _loadRows;
    private int _loadIndex, _loadPage;
    private bool _hasOriginal;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _loadTex;
    private const int LoadX = (ScreenW - 250) / 2, LoadY = (ScreenH - 405) / 2;

    private static int LoadRowY(int row) => row == 0 ? 69 : 117 + 25 * (row - 1);

    private void OpenLoadList()
    {
        _hasOriginal = Enumerable.Range(0, SaveGame.Slots).Any(i => OriginalSave.Peek(i) != null);
        _loadPage = SaveGame.AnyExists() || !_hasOriginal ? 0 : 1;
        FillLoadRows();
        BuildLoadTexture();
    }

    private void FillLoadRows()
    {
        _loadRows = new (int, string, TimeSpan)?[SaveGame.Slots];
        if (_loadPage == 0)
            for (int k = 0; k < SaveGame.Slots; k++)
                _loadRows[k] = SaveGame.Info(k) is { } info ? (110 + k, info.Place, info.Play) : null;
        else
            for (int i = 0; i < SaveGame.Slots; i++)
                if (OriginalSave.Peek(i) is { } p)
                    _loadRows[i] = (10 + i, $"MC{i:D2} {(p.Place.Length > 0 ? p.Place : "필드 " + p.Field)}{(p.CrcOk ? "" : " (CRC 틀림)")}", p.Play);
        _loadIndex = Array.FindIndex(_loadRows, r => r != null);
        if (_loadPage == 0 && SaveGame.Latest() is >= 0 and var latest) _loadIndex = latest;
        if (_loadIndex < 0) _loadIndex = 0;
    }

    private void BuildLoadTexture()
    {
        _loadTex?.Dispose();
        var font = new BitmapFont(0);
        var img = new Image32(250, 425);   // 창 아래 20 줄은 쪽 표시
        img.Blend(FrameData.Load(109).Compose(), 0, 0);
        for (int k = 0; k < _loadRows!.Length; k++)
        {
            int ry = LoadRowY(k);
            bool sel = k == _loadIndex;
            if (sel)
            {
                img.Fill(18, ry - 1, 220, 1, 255, 200, 80); img.Fill(18, ry + 20, 220, 1, 255, 200, 80);
                img.Fill(17, ry - 1, 1, 22, 255, 200, 80); img.Fill(238, ry - 1, 1, 22, 255, 200, 80);
            }
            byte g = sel ? (byte)225 : (byte)205, b = sel ? (byte)130 : (byte)190;
            if (_loadRows[k] is { } r)
            {
                string place = r.Place;
                while (place.Length > 1 && Width(font, SaveGame.RowText(place, r.Play)) > 210) place = place[..^1];
                font.Draw(img, 25, ry + 3, SaveGame.RowText(place, r.Play), 255, g, b);   // 원본 줄 글 꼴(필드 메뉴와 같음)
            }
        }
        // 쪽 표시 — 원본 창엔 없는 패치판 글(원본 세이브가 있을 때만)
        if (_hasOriginal) font.Draw(img, 6, 409, _loadPage == 0 ? "패치판 세이브  (좌우: 원본 세이브)" : "원본 세이브 MC00~10  (좌우: 패치판)", 255, 235, 200);
        _loadTex = _gfx.Texture(img, false);

        static int Width(BitmapFont f, string t) => f.Draw(new Image32(1, 1), 0, 0, t, 0, 0, 0);
    }

    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly ID3D11ShaderResourceView _background;
    private readonly (ID3D11ShaderResourceView Tex, int W, int H)[] _normal, _over;

    private enum Phase { FadeIn, Menu, FadeOut }
    private Phase _phase = Phase.FadeIn;
    private float _tick;
    private int _hover = 0, _chosen = -1;

    public TitleScene(Gfx gfx, ScreenImage screen)
    {
        _gfx = gfx;
        _screen = screen;
        var bg = FrameData.Load(210).Compose();
        var full = new Image32(ScreenW, ScreenH);
        full.Fill(0, 0, ScreenW, ScreenH, 0, 0, 0);
        full.Blend(bg, 0, 0);
        // 판 번호 「v1.04a」(ValueData 0x2a=104) — 원본 자리 (너비−52, 높이−20), 베이지색. 패치판 표시를 덧붙인다
        var font = new BitmapFont(0);
        font.Draw(full, ScreenW - 0x34, ScreenH - 0x14, "v1.04a", 169, 146, 109);
        font.Draw(full, 8, ScreenH - 0x14, "패치판", 169, 146, 109);
        _background = gfx.Texture(full, false);
        Audio.Music.Play(17);   // 메뉴가 뜰 때 BGM 17 (분석-렌더링-2D)

        _normal = new (ID3D11ShaderResourceView, int, int)[4];
        _over = new (ID3D11ShaderResourceView, int, int)[4];
        for (int k = 0; k < 4; k++)
        {
            var n = FrameData.Load(ButtonFrames[k].Normal).Compose();
            var o = FrameData.Load(ButtonFrames[k].Over).Compose();
            _normal[k] = (gfx.Texture(n, false), n.Width, n.Height);
            _over[k] = (gfx.Texture(o, false), o.Width, o.Height);
        }
    }

    private float Fade => _phase switch
    {
        Phase.FadeIn => Math.Clamp(_tick / 50f, 0, 1),
        Phase.FadeOut => 1 - Math.Clamp(_tick / 20f, 0, 1),
        _ => 1,
    };

    public IScene? Update(float seconds, Input input)
    {
        _tick += seconds * Timing.TicksPerSecond;
        var (ox, oy, w, h) = _screen.Fit(ScreenW, ScreenH);
        float mx = (input.MouseX - ox) / w * ScreenW, my = (input.MouseY - oy) / h * ScreenH;
        int under = -1;
        for (int k = 0; k < 4; k++)
            if (mx >= ButtonX && mx < ButtonX + _normal[k].W && my >= ButtonY + k * ButtonStep && my < ButtonY + k * ButtonStep + _normal[k].H)
                under = k;

        switch (_phase)
        {
            case Phase.FadeIn:
                if (_tick >= 50 || input.Pressed(Input.Enter) || input.LeftClicked) { _phase = Phase.Menu; _tick = 0; }
                break;
            case Phase.Menu when _loadRows != null:
            {
                int n = _loadRows.Length, before = _loadIndex, page = _loadPage;
                int row = -1;
                for (int k = 0; k < n; k++)
                    if (mx >= LoadX + 18 && mx < LoadX + 238 && my >= LoadY + LoadRowY(k) && my < LoadY + LoadRowY(k) + 20) row = k;
                if (input.LeftClicked && row >= 0) _loadIndex = row;
                if (input.Pressed(Input.DownKey)) _loadIndex = (_loadIndex + 1) % n;
                if (input.Pressed(Input.Up)) _loadIndex = (_loadIndex + n - 1) % n;
                if (_hasOriginal && (input.Pressed(Input.Left) || input.Pressed(Input.Right))) { _loadPage ^= 1; FillLoadRows(); }
                if (input.Pressed(Input.Escape)) { _loadRows = null; _loadTex?.Dispose(); _loadTex = null; break; }
                if ((input.Pressed(Input.Enter) || input.Pressed(Input.Space) || input.LeftClicked && row >= 0) && _loadRows[_loadIndex] is { } pick)
                { _chosen = pick.Code; _phase = Phase.FadeOut; _tick = 0; }
                if (_loadIndex != before || _loadPage != page) BuildLoadTexture();
                break;
            }
            case Phase.Menu:
                if (under >= 0 && Enabled[under]) _hover = under;
                if (input.Pressed(Input.DownKey)) _hover = Next(_hover, 1);
                if (input.Pressed(Input.Up)) _hover = Next(_hover, -1);
                if ((input.LeftClicked && under >= 0 && Enabled[under]) || input.Pressed(Input.Enter) || input.Pressed(Input.Space))
                {
                    _chosen = input.LeftClicked && under >= 0 ? under : _hover;
                    if (_chosen == 1) { OpenLoadList(); break; }
                    _phase = Phase.FadeOut; _tick = 0;
                }
                if (input.Pressed(Input.Escape)) { _chosen = 3; _phase = Phase.FadeOut; _tick = 0; }
                break;
            case Phase.FadeOut:
                if (_tick >= 20)
                    return _chosen switch
                    {
                        0 => NewGame(),
                        2 or 9 => Continue(),
                        >= 10 and < 10 + SaveGame.Slots => LoadOriginal(_chosen - 10),
                        >= 110 and < 110 + SaveGame.Slots => LoadPatch(_chosen - 110),
                        3 => null,
                        _ => this,
                    };
                break;
        }
        return this;
    }

    private int Next(int k, int dir)
    {
        for (int i = 0; i < 4; i++)
        {
            k = (k + dir + 4) % 4;
            if (Enabled[k]) return k;
        }
        return k;
    }

    private IScene NewGame()
    {
        SaveGame.Reset();
        // 원본 그대로 651(파티 넣기·전역 스위치) → 626(프롤로그 글 FrameData 237, 동영상 0002 는 Bink 라 건너뜀) → 625 → 114 → 531
        return new FieldScene(_gfx, _screen, FieldScene.NewGameField);
    }

    private IScene LoadOriginal(int slot)
    {
        SaveGame.Reset();
        if (OriginalSave.Load(slot) is not { } d) return this;
        return new FieldScene(_gfx, _screen, d.Field, null, (d.Pos, d.Yaw));
    }

    private IScene Continue() => SaveGame.Latest() is var k and >= 0 ? LoadPatch(k) : NewGame();

    private IScene LoadPatch(int slot)
    {
        if (SaveGame.Load(slot) is not { } d) return NewGame();
        return new FieldScene(_gfx, _screen, d.Field, null, (new Vector3(d.X, d.Y, d.Z), d.Yaw));
    }

    public void Draw()
    {
        _gfx.Begin(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        var (ox, oy, w, h) = _screen.Fit(ScreenW, ScreenH);
        float s = w / ScreenW, f = Fade;
        _screen.Draw(_background, ox, oy, w, h, new Vector4(f, f, f, 1));
        for (int k = 0; k < 4; k++)
        {
            var (tex, bw, bh) = k == _hover && _phase != Phase.FadeIn ? _over[k] : _normal[k];
            float a = Enabled[k] ? 1 : 0.35f;
            _screen.Draw(tex, ox + ButtonX * s, oy + (ButtonY + k * ButtonStep) * s, bw * s, bh * s, new Vector4(f, f, f, a));
        }
        if (_loadRows != null && _loadTex != null)
            _screen.Draw(_loadTex, ox + LoadX * s, oy + LoadY * s, 250 * s, 425 * s, Vector4.One);
    }

    public void Dispose()
    {
        _background.Dispose();
        _loadTex?.Dispose();
        foreach (var b in _normal) b.Tex.Dispose();
        foreach (var b in _over) b.Tex.Dispose();
    }
}

internal static class Timing
{
    /// <summary>원본 틱 = 33ms(일감 간격 0x21, 분석-틱과이동) → 1000/33 ≈ 30.3 틱/초.</summary>
    public const float TicksPerSecond = 1000f / 33f;
}
