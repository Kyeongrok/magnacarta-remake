"""마그나카르타 필드 메뉴(MCFldMenu.cpp) 분석 · 원본 그림으로 메뉴 화면 짜 맞추기.

원본 폴더의 파일을 읽기만 한다(환경 변수 MC_DIR). 분석 글: 옵시디안 Project/Magnacarta/분석/분석-필드메뉴.md

    python tools/mc/menu.py parts                  # 메뉴 창 만들기 함수의 (자식 id, FrameData, 자리) 목록
    python tools/mc/menu.py shot status out.png    # 상태 창 짜 맞춤 (800×600)
    python tools/mc/menu.py shot system out.png    # 시스템 메뉴(오른쪽 아래 단추 다섯)
    python tools/mc/menu.py shot item out.png      # 아이템 창
"""
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
import render2d as r2  # noqa: E402

from PIL import Image  # noqa: E402

W, H = 800, 600

# ── MCAlpha.dll 정적 분석 도우미 ────────────────────────────────────────────

FRAME_NEW = 0x52054fce     # MCFrame 자식 만들기 (FrameData, x, y, 부모, 자식 id)
BUTTON_NEW = 0x52056850    # 단추 (보통 FrameData, 올림 FrameData, …)
TEXT_NEW = 0x52056910      # 글 칸
POINT = 0x520094b0         # (x, y) 묶기
MENUBOX_NEW = 0x5205c5fa   # 단추 상자(세로로 단추를 쌓음)
MENUBTN_ADD = 0x5205c7fb   # 단추 상자에 단추 더하기 (보통, 올림, −1, 명령, 0)


def creations(lo, hi):
    """lo~hi 의 만들기 호출마다 (자리, 함수, 바로 앞 즉치 push 들) — 순서대로."""
    sys.path.insert(0, os.path.dirname(__file__))
    import story
    import capstone
    p = story.Pe()
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.skipdata = True
    ins = list(md.disasm(p.img[lo - p.base:hi - p.base], lo))
    out, imms = [], []
    for x in ins:
        if x.mnemonic == "push" and re.fullmatch(r"(0x[0-9a-f]+|-?\d+)", x.op_str):
            v = int(x.op_str, 16) if x.op_str.startswith("0x") else int(x.op_str)
            imms.append(v - (1 << 32) if v >= 0x80000000 else v)
        elif x.mnemonic == "call":
            try:
                t = int(x.op_str, 16)
            except ValueError:
                t = None
            if t in (FRAME_NEW, BUTTON_NEW, TEXT_NEW, POINT, MENUBOX_NEW, MENUBTN_ADD):
                out.append((x.address, t, list(imms)))
            if t != POINT:
                imms = []
    return out


NAMES = {FRAME_NEW: "frame", BUTTON_NEW: "button", TEXT_NEW: "text", POINT: "point", MENUBOX_NEW: "menubox", MENUBTN_ADD: "menubtn"}


def parts(lo=0x5208466c, hi=0x52087a18):
    """상태 창 묶음 생성자 0x5208466c 의 자식들: (자식 id, 종류, FrameData, x, y). x·y 가 레지스터면 None."""
    res, pending = [], None
    for addr, t, imms in creations(lo, hi):
        if t == POINT:
            # 보통 push 차례: 자식 id, …, y, x → imms 끝 둘이 (y, x)
            pending = (imms[0] if imms else None, imms[-1] if len(imms) >= 2 else None, imms[-2] if len(imms) >= 2 else None, addr)
        elif t in (FRAME_NEW, BUTTON_NEW, TEXT_NEW, MENUBOX_NEW):
            fd = imms[-1] if imms else None
            cid, x, y, _ = pending if pending else (None, None, None, None)
            res.append((hex(addr), NAMES[t], cid, fd, x, y))
            pending = None
        elif t == MENUBTN_ADD:
            res.append((hex(addr), "menubtn", None, imms[-1] if imms else None, imms[-3] if len(imms) >= 3 else None, imms[-4] if len(imms) >= 4 else None))
    return res


# ── 그림 ───────────────────────────────────────────────────────────────────

def fdi(num):
    return r2.render_fdi(num)


def text(img, x, y, s, color=(235, 225, 200), fontset=0):
    r2.Font(fontset).draw(img, x, y, s, color)


def screen_back():
    """시스템 화면 바탕 — 지금 필드 화면 위에 덮이므로 어두운 바탕으로 대신한다."""
    return Image.new("RGBA", (W, H), (18, 20, 26, 255))


def shot_system():
    im = screen_back()
    # MCFldMenu 생성자 0x5207f9d9: 단추 상자(FrameData 16 「Menu Back」 200×300)를 (W−197, H−195) 에,
    # 단추 다섯(197×39)을 위에서부터 쌓음: Status 121/120 · Save 123/122 · Load 125/124 · Audio 127/126 · Back to Title 129/128
    x0, y0 = W - 0xC5, H - 0xC3
    for k, (normal, over) in enumerate(((121, 120), (123, 122), (125, 124), (127, 126), (129, 128))):
        im.alpha_composite(fdi(over if k == 0 else normal), (x0, y0 + 39 * k))
    return im


def shot_status(char_name="칼린츠", stats=(695, 325, 357, 182, 182), equip=("소울 블레이드", "-", "-", "무사의 목걸이", "-")):
    im = screen_back()
    # 상태 묶음 생성자 0x5208466c: 바탕 m(+0x1b8) = FrameData 57 「Screen Back」 을 ((W−640)/2, (H−480)/2 + 30) 에
    bx, by = (W - 640) // 2, (H - 480) // 2 + 30
    # 탭 상자(FrameData 16) 를 바탕 (510, 130) 에, 탭 넷: Status 151/150 · Carta 208/207 · Item 153/152 · Party 155/154 (명령 0x79~0x7c)
    for k, (normal, over) in enumerate(((151, 150), (208, 207), (153, 152), (155, 154))):
        im.alpha_composite(fdi(over if k == 0 else normal), (bx + 510, by + 130 + 30 * k))
    # 캐릭터 카드(추정: 0x52084a86 — 파티 차례 i 마다 (20 + 53·i, 10))
    # 파티 카드(0x52084a86: 파티 i 마다 자식 (20 + 53·i, 10), FrameData 는 레지스터 — 카드 그림 FrameData 215(CALINTZ 밝은)·177(어두운) 로 추정)
    for i, fd in enumerate((215, 219, 218)):   # 칼린츠(밝음) · 로프마 · 첼시(어두운 판은 177~185)
        im.alpha_composite(fdi(fd), (bx + 20 + 85 * i, by + 10))
    # PARAMETER + EQUIPMENT 판 = FrameData 146(BGR 257·258) 을 바탕 (20, 110) 에
    px, py = bx + 20, by + 110
    im.alpha_composite(fdi(146), (px, py))
    # 능력치 글 5칸('% 8d', 자식 0x87~0x8b) 을 판 (65, 45 + 24·k) 에 — HP·INT·STR·DEF·DEX
    for k, v in enumerate(stats):
        text(im, px + 65, py + 45 + 24 * k - 7, "%8d" % v, (255, 255, 255))
    # 장비 글 5칸(자식 0x9a~0x9e, 120×20 칸 FrameData 266) 을 판 (338, 45 + 24·k) 에 — 무기·방어구·반지·목걸이·기타
    for k, v in enumerate(equip):
        text(im, px + 338, py + 45 + 24 * k - 1, v, (255, 255, 255))
    text(im, bx + 24, by + 92, char_name, (255, 220, 140))
    return im


def shot_item(equip=("소울 블레이드", "크로스 셔츠", "-", "무사의 목걸이", "-"), gold=0, silver=1):
    im = screen_back()
    bx, by = (W - 640) // 2, (H - 480) // 2 + 30
    for k, (normal, over) in enumerate(((151, 150), (208, 207), (153, 152), (155, 154))):
        im.alpha_composite(fdi(over if k == 2 else normal), (bx + 510, by + 130 + 30 * k))
    for i, fd in enumerate((215, 219, 218)):
        im.alpha_composite(fdi(fd), (bx + 20 + 85 * i, by + 10))
    # Item 쪽 = FrameData 148(BGR 261 EQUIPMENT LIST + 262 ITEM STACKS) 을 바탕 (20, 110) 에(0x52087081)
    px, py = bx + 20, by + 110
    im.alpha_composite(fdi(148), (px, py))
    # 장비 줄 단추 5(0x5208710e~0x5208732d) 를 판 (90, 37 + 24·k) 에 — 무기·방어구·반지·목걸이·기타
    for k, s in enumerate(equip):
        text(im, px + 90, py + 37 + 24 * k + 1, s, (255, 255, 255))
    # 금·은 열쇠 수 글(0x520873e5·0x520874a0) (105, 157)·(237, 157), 쪽 넘김 Prev/Next(FrameData 252·253) (283, 157)·(378, 157)
    text(im, px + 105, py + 157 - 7, "%d" % gold, (255, 255, 255))
    text(im, px + 237, py + 157 - 7, "%d" % silver, (255, 255, 255))
    im.alpha_composite(fdi(252), (px + 283, py + 157))
    im.alpha_composite(fdi(253), (px + 378, py + 157))
    return im


def main(argv):
    if argv[1] == "parts":
        for row in parts():
            print(row)
    elif argv[1] == "shot":
        im = {"status": shot_status, "system": shot_system, "item": shot_item}[argv[2]]()
        im.convert("RGB").save(argv[3])


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv)
