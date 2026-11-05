"""마그나카르타 전투 화면 UI(MCBtlMenu.cpp · MCBtlEtc.cpp) 분석 · 원본 그림으로 전투 화면 짜 맞추기.

원본 폴더의 파일을 읽기만 한다(환경 변수 MC_DIR). 분석 글: 옵시디안 Project/Magnacarta/분석/분석-전투화면.md

    python tools/mc/battle_ui.py parts            # 전투 묶음(0x5208a4b3~0x520b12dd)의 FrameData 만들기 호출
    python tools/mc/battle_ui.py cards            # 캐릭터 id → 전투 카드 FrameData (밝은 / 어두운) 표
    python tools/mc/battle_ui.py shot out.png     # 800×600 전투 화면 짜 맞춤
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
import render2d as r2  # noqa: E402
import menu  # noqa: E402

from PIL import Image, ImageDraw  # noqa: E402

W, H = 800, 600
BASE = (0, H - 480)            # 0x52009027: (0, H − 480) — 640×480 판 좌표를 아래에 붙임
RIGHT = (W - 640, 0)           # 0x520090ad: (W − 640, 0) — 오른쪽에 붙임

# 0x520abb1c 안 jmp 표(0x520ac26b/0x520ac247 · 0x520ac2a4/0x520ac280): 캐릭터 id → 카드 FrameData
CARD_BRIGHT = {0: 215, 1: 218, 2: 219, 3: 217, 4: 220, 5: 222, 6: 221, 20: 223}
CARD_DARK = {0: 177, 1: 179, 2: 181, 3: 178, 4: 182, 5: 184, 6: 183, 20: 185}


def fdi(n):
    return r2.render_fdi(n)


def pos(n):
    return r2.read_fdi(n)["pos"]


def card_table():
    """jmp 표를 다시 풀어 확인한다."""
    import story
    import capstone
    p = story.Pe()
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

    def table(bytetab, jt):
        out = {}
        for cid in range(21):
            b = p.img[bytetab - p.base + cid]
            tgt = struct.unpack_from("<I", p.img, jt - p.base + 4 * b)[0]
            ins = next(md.disasm(p.img[tgt - p.base:tgt - p.base + 16], tgt))
            v = ins.op_str.split(",")[-1].strip()
            if v.startswith("0x") and int(v, 16) < 0x1000:
                out[cid] = int(v, 16)
        return out
    return table(0x520ac26b, 0x520ac247), table(0x520ac2a4, 0x520ac280)


def add(a, b, *c):
    x, y = a[0] + b[0], a[1] + b[1]
    for d in c:
        x, y = x + d[0], y + d[1]
    return x, y


def shot(out, party=(0, 2, 1), hp=((695, 695), (545, 545), (520, 520)), active=0):
    im = Image.new("RGBA", (W, H), (34, 30, 28, 255))
    d = ImageDraw.Draw(im)
    # 바닥 띠: 640판 FrameData 100 · 800판 206 (0x520aefcc 너비 비교), 자리 BASE
    im.alpha_composite(fdi(206), BASE)
    # 파티 카드: 그릇 FrameData 216(pos (10, 404)) 안에 카드 i 를 BASE + (116·i, 0) 에 (0x520abd44 imul 0x74)
    font = r2.Font(0)
    box = pos(216)
    for i, cid in enumerate(party):
        fd = (CARD_BRIGHT if i == active else CARD_DARK)[cid]
        cx, cy = add(box, BASE, (116 * i, 0))
        im.alpha_composite(fdi(fd), (cx, cy))
        cur, mx = hp[i]
        # "%4d / %4d" 글(글꼴 0x155) 을 카드 (28, 20) 에 (0x5204d480(28.0, 20.0, 0))
        font.draw(im, cx + 28, cy + 20, "%4d / %4d" % (cur, mx), (255, 255, 255))
    # TP 막대(moving Turn Point, FrameData 58 pos (465, 380)) 를 BASE + RIGHT 에 (0x520aeef4)
    im.alpha_composite(fdi(58), add(pos(58), BASE, RIGHT))
    # 차례 얼굴(m_pTurnFace, 캐릭터 +0x250 의 FrameData — 칼린츠 25 · 적 40) 를 (583, 306) + BASE + RIGHT 에 (0x520b0974)
    im.alpha_composite(fdi(25), add((583, 306), BASE, RIGHT))
    # 명령 상자(FrameData 56 pos (10, 132), 168×270): Attack 238/239(또는 Crazy Mode 47/48) · Ability 43/44 · Turn End 53/54
    mb = pos(56)
    for k, (normal, over) in enumerate(((238, 239), (43, 44), (53, 54))):   # 올림 = 효과음 541 이 붙은 쪽
        im.alpha_composite(fdi(over if k == 0 else normal), (mb[0], mb[1] + 45 * k))
    # 연결공격 표시(FrameData 139~142 COMBO 1·2·3·FINAL CRASH, pos (500, 240)) — 기준이 BASE 인지는 추정
    im.alpha_composite(fdi(139), add(pos(139), BASE, RIGHT))
    # 피해 숫자: 흰 글씨 FrameData 89~98(BGR 194~203), 회복은 초록 79~88(BGR 204~213) — 색으로 본 뜻(추정)
    x0, y0 = 360, 260
    for k, ch in enumerate("357"):
        im.alpha_composite(fdi(89 + int(ch)), (x0 + 14 * k, y0))
    for k, ch in enumerate("500"):
        im.alpha_composite(fdi(79 + int(ch)), (x0 + 120 + 14 * k, y0))
    im.alpha_composite(fdi(134), (x0 + 240, y0 - 4))   # MISS!
    d.text((360, 240), "damage(white)   heal(green)   MISS", fill=(200, 200, 200))
    im.convert("RGB").save(out)
    return im


def main(argv):
    if argv[1] == "parts":
        for addr, t, imms in menu.creations(0x5208a4b3, 0x520b12dd):
            print(hex(addr), menu.NAMES[t], imms[-8:])
    elif argv[1] == "cards":
        b, k = card_table()
        print("밝은", b)
        print("어두운", k)
    elif argv[1] == "shot":
        shot(argv[2])


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv)
