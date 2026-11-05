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
    private readonly bool[] Enabled = [true, SaveGame.Exists() || Enumerable.Range(0, 4).Any(i => OriginalSave.Peek(i) != null), SaveGame.Exists(), true];

    // 불러오기 목록 (-1 = 패치판 세이브, 0~3 = 원본 MCxx.sav)
    private List<(int Slot, string Label)>? _loadList;
    private int _loadIndex;
    private Vortice.Direct3D11.ID3D11ShaderResourceView? _loadTex;
    private int _loadW, _loadH;

    private void OpenLoadList()
    {
        _loadList = [];
        if (SaveGame.Exists()) _loadList.Add((-1, "패치판  " + (SaveGame.Describe() ?? "")));
        for (int i = 0; i < 4; i++)
            if (OriginalSave.Peek(i) is { } p)
                _loadList.Add((i, $"MC{i:D2}  {(p.Place.Length > 0 ? p.Place : "필드 " + p.Field)}  {(int)p.Play.TotalHours}시간 {p.Play.Minutes}분{(p.CrcOk ? "" : "  (CRC 틀림)")}"));
        _loadIndex = 0;
        BuildLoadTexture();
    }

    private void BuildLoadTexture()
    {
        _loadTex?.Dispose();
        var font = new BitmapFont(0);
        int w = 300, h = 30 + 18 * _loadList!.Count;
        var img = new Image32(w, h);
        img.Fill(0, 0, w, h, 10, 8, 6, 225);
        img.Fill(0, 0, w, 1, 169, 146, 109); img.Fill(0, h - 1, w, 1, 169, 146, 109);
        font.Draw(img, 10, 6, "LOAD GAME", 255, 220, 140);
        for (int i = 0; i < _loadList.Count; i++)
        {
            bool sel = i == _loadIndex;
            font.Draw(img, 14, 26 + 18 * i, (sel ? "> " : "  ") + _loadList[i].Label, 255, sel ? (byte)230 : (byte)210, sel ? (byte)120 : (byte)200);
        }
        _loadTex = _gfx.Texture(img, false); _loadW = w; _loadH = h;
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
            case Phase.Menu when _loadList != null:
                if (input.Pressed(Input.DownKey)) { _loadIndex = (_loadIndex + 1) % _loadList.Count; BuildLoadTexture(); }
                if (input.Pressed(Input.Up)) { _loadIndex = (_loadIndex + _loadList.Count - 1) % _loadList.Count; BuildLoadTexture(); }
                if (input.Pressed(Input.Escape)) { _loadList = null; break; }
                if (input.Pressed(Input.Enter) || input.Pressed(Input.Space)) { _chosen = 10 + _loadList[_loadIndex].Slot; _phase = Phase.FadeOut; _tick = 0; }
                break;
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
                        >= 10 and <= 13 => LoadOriginal(_chosen - 10),
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

    private IScene Continue()
    {
        if (SaveGame.Load() is not { } d) return NewGame();
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
        if (_loadList != null && _loadTex != null)
            _screen.Draw(_loadTex, ox + (ScreenW - _loadW * 1.5f) / 2 * s, oy + 200 * s, _loadW * 1.5f * s, _loadH * 1.5f * s, Vector4.One);
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
