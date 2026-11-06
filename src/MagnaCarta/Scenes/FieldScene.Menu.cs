using System.Numerics;
using MagnaCarta.Data;
using Vortice.Direct3D11;

namespace MagnaCarta.Scenes;

/// <summary>
/// 원본 상태 화면(분석-필드메뉴): 바탕 FrameData 57 을 800판 ((W−640)/2, (H−480)/2 + 30) = (80, 90) 에,
/// 탭 넷(Status 151/150 · Carta 208/207 · Item 153/152 · Party 155/154)을 바탕 (510, 130 + 30k) 에,
/// 파티 카드(FrameData 215 칼린츠 등 — 번호는 추정)를 (20 + 85i, 10) 에(원본 간격 53 은 카드가 겹쳐 넓힘),
/// PARAMETER + EQUIPMENT 판 FrameData 146 을 (20, 110) 에, 능력치 HP·INT·STR·DEF·DEX 를 판 (65, 45 + 24k) 에,
/// 장비(무기·방어구·반지·목걸이·기타)를 판 (338, 45 + 24k) 에. 글자 색은 못 땄다(흰 글).
/// </summary>
internal sealed partial class FieldScene
{
    private const int MenuW = 800, MenuH = 600, MenuBx = 80, MenuBy = 90;
    private static readonly (int Normal, int Over)[] MenuTabs = [(151, 150), (208, 207), (153, 152), (155, 154)];
    // 파티 카드 그림 — 원본은 레지스터로 넘겨 못 풀었다(추정: 215 칼린츠 · 218 첼시 · 219 로프마)
    private static readonly Dictionary<int, int> CardFrames = new() { [0] = 215, [1] = 218, [2] = 219, [3] = 217, [4] = 220, [5] = 222, [6] = 221, [20] = 223 };   // 전투 카드 표(분석-전투화면)와 같은 짝

    private Image32? _menuBase;
    private int _menuBaseTab = -1;
    private ID3D11Texture2D? _menuTex;
    private ID3D11ShaderResourceView? _menuView;
    private readonly Image32 _menuImg = new(MenuW, MenuH);
    private int _statusTab;   // 0 원본 Status · 1 패치판 아이템/장비 목록

    private static readonly Dictionary<int, Image32> FrameCache = [];
    private static Image32 Frame(int n)
    {
        if (!FrameCache.TryGetValue(n, out var img)) FrameCache[n] = img = McFiles.Exists("FrameData", n, "fdi") ? FrameData.Load(n).Compose() : new Image32(1, 1);
        return img;
    }

    private Image32 MenuBase(int tab)
    {
        if (_menuBase != null && _menuBaseTab == tab) return _menuBase;
        var im = new Image32(MenuW, MenuH);
        im.Blend(Frame(57), MenuBx, MenuBy);
        for (int k = 0; k < 4; k++) im.Blend(Frame(k == tab ? MenuTabs[k].Over : MenuTabs[k].Normal), MenuBx + 510, MenuBy + 130 + 30 * k);
        if (tab == 0) im.Blend(Frame(146), MenuBx + 20, MenuBy + 110);
        if (tab == 1) im.Blend(Frame(147), MenuBx + 20, MenuBy + 110);
        if (tab == 3) im.Blend(Frame(233), MenuBx + 20, MenuBy + 110);   // Party 쪽 「PARTY - BATTLE MEMBER ALIGNMENT」(0x52087772)   // Carta 쪽 「CLICK TO CHANGE YOUR CARTA」
        if (tab == 2)
        {
            // Item 쪽 = FrameData 148(EQUIPMENT LIST + ITEM STACKS), 쪽 넘김 252·253 (0x52087081)
            im.Blend(Frame(148), MenuBx + 20, MenuBy + 110);
            im.Blend(Frame(252), MenuBx + 20 + 283, MenuBy + 110 + 157);
            im.Blend(Frame(253), MenuBx + 20 + 378, MenuBy + 110 + 157);
        }
        _menuBase = im; _menuBaseTab = tab;
        return im;
    }

    private void DrawOriginalStatus()
    {
        var img = _menuImg;
        int origTab = _statusTab;   // 원본 탭 차례: 0 Status · 1 Carta · 2 Item · 3 Party (4 = 패치판 장비 끼기 목록)
        Buffer.BlockCopy(MenuBase(origTab).Bgra, 0, img.Bgra, 0, img.Bgra.Length);
        var font = _ui.Font;
        for (int i = 0; i < GameState.Party.Count; i++)
            if (CardFrames.TryGetValue(GameState.Party[i], out int card))
            {
                img.Blend(Frame(card), MenuBx + 20 + 85 * i, MenuBy + 10);
                // 카드 HP 칸 — 전투 HUD 와 같은 자리(+28, +20). 패치판은 전투 밖 체력을 따로 두지 않아 최대/최대
                if (Chars.Value.TryGetValue(GameState.Party[i], out var pc))
                {
                    int mh = GameState.Effective(pc).MaxHp;
                    font.Draw(img, MenuBx + 20 + 85 * i + 28, MenuBy + 10 + 20, $"{mh,4}", 255, 255, 255);   // 카드가 85px 간격으로 겹쳐 「/최대」 는 가려진다
                }
                if (i == _statusMember) img.Fill(MenuBx + 20 + 85 * i, MenuBy + 64, 80, 2, 255, 220, 120);
            }
        if (origTab == 3)
        {
            // 칸 1~7(판 그림의 숫자 x ≈ 83 + 51k)마다 파티원 이름과 출전 점 — 켜짐 FrameData 234(밝은 초록) · 꺼짐 235. 칸 안 자리는 그림에서 잰 값(원본 자식 자리는 고리라 못 땀)
            int px = MenuBx + 20, py = MenuBy + 110;
            for (int i = 0; i < Math.Min(7, GameState.Party.Count); i++)
            {
                int cx = px + 83 + 51 * i + 3;
                int id = GameState.Party[i];
                bool on = GameState.BattleMembers().Contains(id);
                var dot = Frame(on ? 234 : 235);
                img.Blend(dot, cx - dot.Width / 2, py + 56);
                string nm = Chars.Value.TryGetValue(id, out var pcn) ? pcn.Name : id.ToString();
                if (nm.Length > 3) nm = nm[..3];
                int tw = _ui.TextWidth(nm);
                font.Draw(img, cx - tw / 2, py + 78, nm, on ? (byte)255 : (byte)150, on ? (byte)255 : (byte)150, on ? (byte)255 : (byte)150);
                if (i == _statusMember) img.Fill(cx - 20, py + 98, 40, 2, 255, 220, 120);
            }
            if (_statusNote.Length > 0) font.Draw(img, px + 20, py + 112, _statusNote, 255, 230, 150);
            font.Draw(img, px + 20, py + 196, "좌우 사람   Space 출전 켜기/끄기", 200, 190, 170);
            goto done;
        }
        if (_statusMember < GameState.Party.Count && Chars.Value.TryGetValue(GameState.Party[_statusMember], out var c0))
        {
            var c = GameState.Effective(c0);
            int px = MenuBx + 20, py = MenuBy + 110;
            if (origTab == 1)
            {
                // 다섯 부위(머리·가슴·배·팔·다리)마다 끼운 카르타 속성과 그 친화도 — 글 (80, 16 + 24k). 바꾸기(◀▶ 242·244)는 아직
                // 판의 줄 차례는 가슴(HP)·머리(INT)·팔(STR)·배(DEF)·다리(DEX) — 줄마다 친화 값(80), 속성(160), 카르타 이름(205). 칸 자리 160·205 는 그림에서 잰 값
                int[] el = [c.Breast, c.Head, c.Arm, c.Abdomen, c.Leg];
                string[] names = ["-", "지", "화", "풍", "암", "수", "전", "광"];
                for (int k = 0; k < 5; k++)
                {
                    int e = el[k];
                    int ry = py + 16 + 24 * k;
                    if (e is < 1 or > 7) continue;
                    font.Draw(img, px + 80, ry, c.Affinity[e - 1].ToString().PadLeft(5), 255, 255, 255);
                    font.Draw(img, px + 163, ry, names[e], 255, 255, 255);
                    font.Draw(img, px + 210, ry, names[e] + " 카르타", 255, 255, 255);
                }
                // 카르타 점수 7칸과 친화 나눠 주기(0x52081ec3: ◀ 점수 하나로 그 줄 속성 친화 +1 — 기본 친화까지, ▶ 되돌림)
                string pts = string.Join(" ", Enumerable.Range(0, 7).Select(e => names[e + 1] + GameState.CartaPoints[e]));
                font.Draw(img, px + 20, py + 208, "점수  " + pts, 255, 230, 150);
                font.Draw(img, px + 20, py + 226, "위아래 줄 고르기  [ 친화 +1   ] 되돌리기", 200, 190, 170);
                font.Draw(img, px + 60, py + 16 + 24 * _cartaRow, ">", 255, 220, 120);
                font.Draw(img, MenuBx + 24, MenuBy + 92, c.Name, 255, 220, 140);
                goto done;
            }
            if (origTab == 2)
            {
                // 장비 줄 다섯 (90, 37 + 24k), 금·은 열쇠 수 (105, 157)·(237, 157)
                var sl = GameState.Slots(c.Id);
                string sgn = Singis.Value.TryGetValue(c.Singi, out var s2) ? s2.Name : "-";
                string[] rows = [sgn, Name(sl[0]), Name(sl[1]), Name(sl[2]), Name(sl[3])];
                for (int k = 0; k < 5; k++) font.Draw(img, px + 90, py + 37 + 24 * k + 1, rows[k], 255, 255, 255);
                font.Draw(img, px + 105, py + 157 - 7, GameState.Items.Count(i => i == 44).ToString(), 255, 255, 255);
                font.Draw(img, px + 237, py + 157 - 7, GameState.Items.Count(i => i == 43).ToString(), 255, 255, 255);
                // ITEM STACKS 다섯 칸 — 열쇠 아닌 가진 아이템(칸 자리는 그림에서 잰 값, 원본 자식 자리는 못 땄다)
                var stacks = GameState.Items.Where(i => i is not (43 or 44)).Distinct().Take(5).ToList();
                for (int k = 0; k < stacks.Count; k++)
                {
                    int cnt = GameState.Items.Count(i => i == stacks[k]);
                    font.Draw(img, px + 292, py + 37 + 24 * k + 1, ItemNames.Of(stacks[k]) + (cnt > 1 ? " x" + cnt : ""), 255, 255, 255);
                }
                font.Draw(img, MenuBx + 24, MenuBy + 92, c.Name, 255, 220, 140);
                goto done;
            }
            int[] values = [c.MaxHp, c.IntPower, c.PhysicalAttack, c.Armor, c.Agility];
            for (int k = 0; k < 5; k++)
            {
                string v = values[k].ToString().PadLeft(8);
                font.Draw(img, px + 65 + 1, py + 45 + 24 * k - 6, v, 0, 0, 0);
                font.Draw(img, px + 65, py + 45 + 24 * k - 7, v, 255, 255, 255);
            }
            var slots = GameState.Slots(c.Id);
            string sg = Singis.Value.TryGetValue(c.Singi, out var s) ? s.Name : "-";
            string[] eq = [sg, Name(slots[0]), Name(slots[1]), Name(slots[2]), Name(slots[3])];
            for (int k = 0; k < 5; k++) font.Draw(img, px + 338, py + 45 + 24 * k - 1, eq[k], 255, 255, 255);
            font.Draw(img, MenuBx + 24, MenuBy + 92, c.Name, 255, 220, 140);
        }
        done:
        font.Draw(img, MenuBx + 20, MenuBy + 400, "좌우 사람   Tab 다음 쪽(Status / Carta / Item / Party / 장비 끼기)   I 닫기", 200, 190, 170);
        if (_menuTex == null) _menuTex = _gfx.DynamicTexture(MenuW, MenuH, out _menuView);
        _gfx.UploadTexture(_menuTex, img);
        var (x, y, w, h) = _screen.Fit(MenuW, MenuH);
        _screen.Draw(_menuView!, x, y, w, h, Vector4.One);

        static string Name(int id) => id >= 0 ? ItemNames.Of(id) : "-";
    }

    // ── 시스템 메뉴 (MCFldMenu 0x5207f9d9): 단추 다섯 197×39 를 (W−197, H−195) 부터 쌓음 ──
    private static readonly (int Normal, int Over)[] SystemButtons = [(121, 120), (123, 122), (125, 124), (127, 126), (129, 128)];
    private bool _sysMenu;
    private int _sysIndex;
    private ID3D11Texture2D? _sysTex;
    private ID3D11ShaderResourceView? _sysView;
    private readonly Image32 _sysImg = new(MenuW, MenuH);

    /// <summary>Status · Save · Load · Audio · Back to Title (명령 0x49~0x4d, 0x52080af0). 돌려주는 장면이 있으면 바꾼다.</summary>
    private IScene? UpdateSystemMenu(Input input)
    {
        var (fx, fy, fw, fh) = _screen.Fit(MenuW, MenuH);
        float mx = (input.MouseX - fx) / fw * MenuW, my = (input.MouseY - fy) / fh * MenuH;
        int under = -1;
        for (int k = 0; k < 5; k++)
            if (mx >= MenuW - 197 && my >= MenuH - 195 + 39 * k && my < MenuH - 195 + 39 * (k + 1)) under = k;
        if (under >= 0) _sysIndex = under;
        if (input.Pressed(Input.Up)) _sysIndex = (_sysIndex + 4) % 5;
        if (input.Pressed(Input.DownKey)) _sysIndex = (_sysIndex + 1) % 5;
        if (input.Pressed(Input.Escape)) { _sysMenu = false; return null; }
        if (!(input.Pressed(Input.Enter) || input.Pressed(Input.Space) || input.LeftClicked && under >= 0)) return null;
        _sysMenu = false;
        switch (_sysIndex)
        {
            case 0: _status = true; _statusTab = 0; break;
            case 1: OpenSlotPicker(true, 1); break;
            case 2:
                if (!SaveGame.AnyExists()) { _dialog = "저장한 것이 없다."; _talking = null; break; }
                OpenSlotPicker(false, Math.Max(0, SaveGame.Latest()));
                break;
            case 3: _audioWin = true; _audioRow = 0; break;
            case 4: return new TitleScene(_gfx, _screen);
        }
        return null;
    }

    private void DrawSystemMenu()
    {
        var img = _sysImg;
        Array.Clear(img.Bgra);
        img.Blend(Frame(16), MenuW - 197, MenuH - 195);
        for (int k = 0; k < 5; k++) img.Blend(Frame(k == _sysIndex ? SystemButtons[k].Over : SystemButtons[k].Normal), MenuW - 197, MenuH - 195 + 39 * k);
        // 원본 보통/올림 그림(121/120 …)은 밝기 차이가 작아(평균 19.6 대 21.5) 패치판이 고른 단추 왼쪽에 표시를 더한다
        img.Fill(MenuW - 200, MenuH - 195 + 39 * _sysIndex + 8, 4, 22, 255, 200, 80);
        if (_sysTex == null) _sysTex = _gfx.DynamicTexture(MenuW, MenuH, out _sysView);
        _gfx.UploadTexture(_sysTex, img);
        var (x, y, w, h) = _screen.Fit(MenuW, MenuH);
        _screen.Draw(_sysView!, x, y, w, h, Vector4.One);
    }

    // ── 저장 · 불러오기 칸 고르기 — 원본 SAVE/LOAD 창(FrameData 112/109, 250×405, 화면 가운데). 칸 AUTO + 1~10 ──
    // 칸 줄은 그림에서 잰 자리: AUTO y 69~88, 1~10 은 y 117 부터 25 간격, 높이 20, x 18~237. 열 STAGE x≈62 · PLAY TIME x≈178
    private bool _slotPick, _slotSave;
    private int _slotIndex;
    private (string Place, string Time, TimeSpan Play)?[]? _slotInfo;   // 창을 연 동안 읽어 둔 칸 내용
    private ID3D11Texture2D? _slotTex;
    private ID3D11ShaderResourceView? _slotView;
    private const int SlotWinX = (MenuW - 250) / 2, SlotWinY = (MenuH - 405) / 2;

    private bool _slotConfirm;
    private int _confirmIndex;   // 0 OK · 1 CANCEL
    private const int ConfirmX = (MenuW - 180) / 2, ConfirmY = (MenuH - 80) / 2;
    private static readonly (int Normal, int Over)[] ConfirmButtons = [(117, 116), (119, 118)];
    private static readonly int[] ConfirmBx = [22, 100];

    private static int SlotRowY(int slot) => slot == 0 ? 69 : 117 + 25 * (slot - 1);

    private void OpenSlotPicker(bool save, int index)
    {
        _slotPick = true; _slotSave = save; _slotIndex = index; _slotConfirm = false;
        _slotInfo = Enumerable.Range(0, SaveGame.Slots).Select(SaveGame.Info).ToArray();
    }

    private IScene? UpdateSlotPicker(Input input)
    {
        var (fx, fy, fw, fh) = _screen.Fit(MenuW, MenuH);
        float mx = (input.MouseX - fx) / fw * MenuW - SlotWinX, my = (input.MouseY - fy) / fh * MenuH - SlotWinY;
        int under = -1;
        for (int k = 0; k < SaveGame.Slots; k++)
            if (mx >= 18 && mx < 238 && my >= SlotRowY(k) && my < SlotRowY(k) + 20) under = k;
        if (_slotConfirm)
        {
            // 확인 창(FrameData 115, 화면 가운데 (W−180)/2, (H−80)/2 — 0x52080d14) — OK (22,50) · CANCEL (100,50), 단추 크기는 그림 크기
            float cx = mx + SlotWinX - ConfirmX, cy = my + SlotWinY - ConfirmY;
            int btn = -1;
            for (int k = 0; k < 2; k++)
            {
                var im = Frame(ConfirmButtons[k].Normal);
                if (cx >= ConfirmBx[k] && cx < ConfirmBx[k] + im.Width && cy >= 50 && cy < 50 + im.Height) btn = k;
            }
            if (btn >= 0) _confirmIndex = btn;
            if (input.Pressed(Input.Left) || input.Pressed(Input.Right)) _confirmIndex ^= 1;
            if (input.Pressed(Input.Escape)) { _slotConfirm = false; return null; }
            if (!(input.Pressed(Input.Enter) || input.Pressed(Input.Space) || input.LeftClicked && btn >= 0)) return null;
            _slotConfirm = false;
            if (_confirmIndex == 1) return null;   // CANCEL — 칸 목록으로
        }
        else
        {
            // SAVE 창은 AUTO 줄을 못 고른다 — 원본 처리 0x52080c53 이 명령 0x4f(AUTO) 이하를 걸러 칸 1~10 만(LOAD 는 0x5e = AUTO 부터)
            int first = _slotSave ? 1 : 0, count = SaveGame.Slots - first;
            if (input.Pressed(Input.Up)) _slotIndex = first + (_slotIndex - first + count - 1) % count;
            if (input.Pressed(Input.DownKey)) _slotIndex = first + (_slotIndex - first + 1) % count;
            if (input.Pressed(Input.Escape)) { _slotPick = false; return null; }
            if (input.LeftClicked && under >= first) _slotIndex = under;
            if (input.LeftClicked && under == 0 && _slotSave) return null;
            if (!(input.Pressed(Input.Enter) || input.Pressed(Input.Space) || input.LeftClicked && under >= 0)) return null;
            // 빈 칸 불러오기는 묻지 않고 알린다. 나머지는 「선택하신 항목이 정확합니까?」
            if (_slotSave || _slotInfo?[_slotIndex] != null) { _slotConfirm = true; _confirmIndex = 0; return null; }
        }
        _slotPick = false;
        string where = _field.PlaceName.Length > 0 ? _field.PlaceName : $"필드 {_field.Number}";
        if (_slotSave)
        {
            SaveGame.Save(_field.Number, _pos, _yaw, _field.PlaceName, _slotIndex);
            _dialog = $"{SaveGame.SlotName(_slotIndex)} 칸에 저장했다. ({where})"; _talking = null;
            return null;
        }
        if (SaveGame.Load(_slotIndex) is { } d) return new FieldScene(_gfx, _screen, d.Field, null, (new Vector3(d.X, d.Y, d.Z), d.Yaw));
        _dialog = $"{SaveGame.SlotName(_slotIndex)} 칸은 비어 있다."; _talking = null;
        return null;
    }

    private void DrawSlotPicker()
    {
        if (!_slotPick) return;
        var img = _sysImg;
        Array.Clear(img.Bgra);
        int wx = SlotWinX, wy = SlotWinY;
        img.Blend(Frame(_slotSave ? 112 : 109), wx, wy);
        var font = _ui.Font;
        for (int k = 0; k < SaveGame.Slots; k++)
        {
            int ry = wy + SlotRowY(k);
            bool sel = k == _slotIndex;
            if (sel)
            {
                // 고른 칸 — 원본 표시는 못 땄다(추정). 금빛 테두리
                img.Fill(wx + 18, ry - 1, 220, 1, 255, 200, 80); img.Fill(wx + 18, ry + 20, 220, 1, 255, 200, 80);
                img.Fill(wx + 17, ry - 1, 1, 22, 255, 200, 80); img.Fill(wx + 238, ry - 1, 1, 22, 255, 200, 80);
            }
            byte g = sel ? (byte)225 : (byte)205, b = sel ? (byte)130 : (byte)190;
            if (_slotSave && k == 0) { g = 150; b = 130; }   // 저장 못 하는 AUTO 줄은 흐리게(패치판 표시)
            if (_slotInfo?[k] is { } info)
            {
                // 원본 줄 글 「       장소 (시:분)」 을 (25, 줄+3) 에 — 줄 칸(x 18~237)을 넘지 않게 장소를 줄인다
                string place = info.Place;
                while (place.Length > 1 && _ui.TextWidth(SaveGame.RowText(place, info.Play)) > 210) place = place[..^1];
                font.Draw(img, wx + 25, ry + 3, SaveGame.RowText(place, info.Play), 255, g, b);
            }
        }
        if (_slotConfirm)
        {
            img.Blend(Frame(115), ConfirmX, ConfirmY);
            for (int k = 0; k < 2; k++)
                img.Blend(Frame(k == _confirmIndex ? ConfirmButtons[k].Over : ConfirmButtons[k].Normal), ConfirmX + ConfirmBx[k], ConfirmY + 50);
        }
        if (_slotTex == null) _slotTex = _gfx.DynamicTexture(MenuW, MenuH, out _slotView);
        _gfx.UploadTexture(_slotTex, img);
        var (x, y, w, h) = _screen.Fit(MenuW, MenuH);
        _screen.Draw(_slotView!, x, y, w, h, Vector4.One);
    }

    // ── AUDIO SETTINGS 창 — FrameData 130 을 가운데((W−250)/2, (H−200)/2), 줄 셋 BGM · SOUND · VOICE 의 단추(FrameData 114, 208×14)를 (20, 68/93/118).
    // 손잡이 FrameData 131(16×16 초록 점)은 그림 속 줄 x 103~201 위를 움직인다(줄 자리는 그림에서 잰 것, 손잡이 꼴은 추정) ──
    private bool _audioWin, _audioDrag;
    private int _audioRow;
    private const int AudioX = (MenuW - 250) / 2, AudioY = (MenuH - 200) / 2, TrackX0 = 103, TrackX1 = 203;   // 원본 값 0~100 = 줄 위 화소 0~100
    private static readonly int[] AudioRowY = [68, 93, 118];

    private void UpdateAudioWindow(Input input)
    {
        var (fx, fy, fw, fh) = _screen.Fit(MenuW, MenuH);
        float mx = (input.MouseX - fx) / fw * MenuW - AudioX, my = (input.MouseY - fy) / fh * MenuH - AudioY;
        int under = -1;
        for (int k = 0; k < 3; k++)
            if (mx >= 20 && mx < 228 && my >= AudioRowY[k] && my < AudioRowY[k] + 14) under = k;
        var lv = Audio.Volume.Levels;
        bool changed = false;
        if (input.LeftClicked && under >= 0) { _audioRow = under; _audioDrag = true; }
        if (!input.LeftDown) _audioDrag = false;
        if (_audioDrag)
        {
            // 누른 줄의 손잡이를 단추를 놓을 때까지 마우스 x 로 끈다
            int v = (int)Math.Clamp((mx - TrackX0) / (TrackX1 - TrackX0) * Audio.Volume.Max, 0, Audio.Volume.Max);
            if (v != lv[_audioRow]) { lv[_audioRow] = v; changed = true; }
        }
        if (input.Pressed(Input.Up)) _audioRow = (_audioRow + 2) % 3;
        if (input.Pressed(Input.DownKey)) _audioRow = (_audioRow + 1) % 3;
        int step = Audio.Volume.Max / 10;
        if (input.Pressed(Input.Left)) { lv[_audioRow] = Math.Max(0, lv[_audioRow] - step); changed = true; }
        if (input.Pressed(Input.Right)) { lv[_audioRow] = Math.Min(Audio.Volume.Max, lv[_audioRow] + step); changed = true; }
        if (changed)
        {
            Audio.Volume.Apply();
            if (_audioRow == 1 && FootSteps[_field.Floor * 8] > 0) Audio.SoundEffects.Play(FootSteps[_field.Floor * 8]);   // 크기 들려주기(발소리)
        }
        if (input.Pressed(Input.Escape) || input.Pressed(Input.Enter) || input.Pressed(Input.Space)) { _audioWin = false; Audio.Volume.Save(); }
    }

    private void DrawAudioWindow()
    {
        var img = _sysImg;
        Array.Clear(img.Bgra);
        img.Blend(Frame(130), AudioX, AudioY);
        var knob = Frame(131);
        for (int k = 0; k < 3; k++)
        {
            int cx = AudioX + TrackX0 + (TrackX1 - TrackX0) * Audio.Volume.Levels[k] / Audio.Volume.Max;
            int cy = AudioY + AudioRowY[k] + 6;
            // 고른 줄 — 줄 단추의 올림 그림 FrameData 132(초록 이름 칸 + 초록 줄)를 단추 자리에. 보통 그림 114 는 투명
            if (k == _audioRow) img.Blend(Frame(132), AudioX + 20, AudioY + AudioRowY[k]);
            img.Blend(knob, cx - knob.Width / 2, cy - knob.Height / 2);
        }
        if (_slotTex == null) _slotTex = _gfx.DynamicTexture(MenuW, MenuH, out _slotView);
        _gfx.UploadTexture(_slotTex, img);
        var (x, y, w, h) = _screen.Fit(MenuW, MenuH);
        _screen.Draw(_slotView!, x, y, w, h, Vector4.One);
    }

    private void DisposeMenu() { _menuView?.Dispose(); _menuTex?.Dispose(); _sysView?.Dispose(); _sysTex?.Dispose(); _slotView?.Dispose(); _slotTex?.Dispose(); }
}
