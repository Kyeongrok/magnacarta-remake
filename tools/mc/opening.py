"""마그나카르타 — 새 게임 첫 장면(Field 651 → 626 → 625 → 114 → 531) 분석 도구.

원본 폴더(환경 변수 MC_DIR)를 읽기만 한다. story·stage·render2d·chars 를 읽기 전용으로 import 해 쓴다.
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-시작장면.md

    python tools/mc/opening.py dis 0x52078209        # 함수 하나 풀기(문자열 주석)
    python tools/mc/opening.py scene                  # 첫 장면 사건 표(명령·대사·화자·목소리)
    python tools/mc/opening.py cams 625               # fld F 카메라 컷: 자리·바라보는 쪽
    python tools/mc/opening.py box out.png            # 대사창을 원본 그림으로 짜 맞춤
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import story   # noqa: E402
import stage   # noqa: E402

OPENING = [651, 626, 625, 114, 531]


# ── 정적 분석: 함수 하나를 ret 까지, push 하는 문자열은 주석으로 ─────────────────

def dis_func(p, va, limit=4000, maxret=1):
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    out, rets = [], 0
    o = va - p.base
    for ins in md.disasm(p.img[o:o + limit * 8], va):
        note = ""
        for tok in ins.op_str.replace(",", " ").replace("[", " ").replace("]", " ").split():
            if tok.startswith("0x") and len(tok) >= 9:
                v = int(tok, 16)
                if p.base + 0xbd000 <= v < p.base + len(p.img):
                    try:
                        s = p.cstr(v)
                        if 2 <= len(s) < 120 and all(ch.isprintable() for ch in s):
                            note = "  ; \"%s\"" % s
                    except Exception:
                        pass
        out.append("%08x  %-7s %s%s" % (ins.address, ins.mnemonic, ins.op_str, note))
        if ins.mnemonic == "ret":
            rets += 1
            if rets >= maxret:
                break
        if len(out) >= limit:
            break
    return "\n".join(out)


# ── 장면 표 ───────────────────────────────────────────────────────────────────

def speaker_names(num):
    """fld B: 10000+id → CharData 이름·얼굴 BGR(+0x9e)."""
    import chars
    byid = chars.chars_by_id()
    f = stage.Fld(num)
    out = {}
    for x0, x1, p, q, _ in f.b:
        c = byid.get(x1)
        out[10000 + x0] = (c["name"] if c else "?", c["u9e"] if c else -1, x1, p, q)
    return out


def scene(out=print):
    for num in OPENING:
        hdr = story.field_header(num)
        names = speaker_names(num) if hdr[0] != -1 else {}
        try:
            lines = dict(story.fpb(hdr[2]))
        except OSError:
            lines = {}
        d = story.read("Field", "%04d.fld" % num)
        evs, _ = story.parse_events(d, story.find_events(d))
        out("## Field %04d  wrd %d  Fpb %04d" % (num, hdr[0], hdr[2]))
        for who, (nm, face, cid, pos, rot) in sorted(names.items()):
            out("   인물 %d = CharData %d %s (얼굴 BGR %d) 자리 %s" % (who, cid, nm, face, [round(v, 1) for v in pos]))
        for i, (t, conds, acts) in enumerate(evs):
            out("  ev%d 최대실행=%d" % (i, t))
            for c in conds:
                out("    IF " + story.fmt_cmd(c, cond=True))
            for c in acts:
                s = story.fmt_cmd(c, list(lines.items()))
                if c[0] in (700, 704):
                    nm = names.get(c[1], ("?",))[0]
                    s += "   ← %s, 목소리 SVD %s" % (nm, c[8])
                out("    DO " + s)


# ── 카메라 컷 ─────────────────────────────────────────────────────────────────

def cams(num):
    f = stage.Fld(num)
    res = []
    for hd, items in f.f:
        keys = [struct.unpack_from("<19f", it) for it in items]
        res.append((hd, keys))
    return res


def camera_view(key):
    """fld F 키 → (자리, 앞, 위, 오른쪽). 앞 = −행2, 위 = 행1, 오른쪽 = 행0 (625 컷 2~5 가 칼린츠를 보는 것으로 맞춤)."""
    import numpy as np
    m = np.array(key[:16], dtype=float).reshape(4, 4)
    return m[3, :3], -m[2, :3], m[1, :3], m[0, :3]


# ── 대사창 짜 맞추기 ──────────────────────────────────────────────────────────
# MCTalkBox (0x5205ab40, 700 처리 0x52078209): 글 칸 460×52(+0x1b4/+0x1b8), 글 시작 (155, 9),
# 얼굴 = MCVBgrObj(CharData +0x9e 의 BGR) 자리 (0, −143), 「다음」 = MCSprite OBS 0002 모션 0 (460+140, 52+13).
# 기준점은 640×480 판을 아래로 붙이는 (0, 화면높이−480) — 자식 자리의 축 방향은 확인 못 함(그림은 추정 배치).

def compose_box(out, bg=None, face=41, text="후우...또 그 빌어먹을 꿈을...", width=800, height=600, reveal=None):
    from PIL import Image, ImageDraw
    import render2d
    if bg and os.path.exists(bg):
        img = Image.open(bg).convert("RGBA").resize((width, height))
    else:
        img = Image.new("RGBA", (width, height), (30, 34, 40, 255))
    ox, oy = 0, height - 480                       # 0x52009027
    # 아래 띠(추정 — 원본 코드에서 바탕 그림·색 채움을 못 찾았다)
    band = Image.new("RGBA", (width, 110), (0, 0, 0, 140))
    img.alpha_composite(band, (0, height - 110))
    f = render2d.read_bgr(face)["image"].convert("RGBA")
    img.alpha_composite(f, (ox, height - f.height))
    font = render2d.Font(1)
    tx, ty = ox + 155 + 30, height - 52 - 30
    shown = text if reveal is None else text[:reveal]
    font.draw(img, tx, ty, shown, (255, 255, 255))
    d, o = render2d.read_obs(2)
    g = o["groups"][0]
    fr = render2d.obs_frame(d, g, g["frames"][0][1])
    img.alpha_composite(fr["image"], (tx + 460 - 10 + fr["x"], ty + 52 - 8 + fr["y"]))
    dr = ImageDraw.Draw(img)
    dr.rectangle((tx, ty, tx + 460, ty + 52), outline=(255, 200, 0, 160))
    img.convert("RGB").save(out)


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = argv[1]
    if cmd == "box":
        compose_box(argv[2], argv[3] if len(argv) > 3 else None)
        return
    if cmd == "dis":
        p = story.Pe()
        print(dis_func(p, int(argv[2], 16), int(argv[3]) if len(argv) > 3 else 4000))
    elif cmd == "scene":
        scene()
    elif cmd == "cams":
        for hd, keys in cams(int(argv[2])):
            print("컷", hd)
            for k in (keys[0], keys[-1]):
                m = k[:16]
                print("   자리 %s  행0 %s 행1 %s 행2 %s  fov %.1f near %.1f far %.1f" % (
                    [round(v, 1) for v in m[12:15]], [round(v, 2) for v in m[0:3]],
                    [round(v, 2) for v in m[4:7]], [round(v, 2) for v in m[8:11]], k[16], k[17], k[18]))


if __name__ == "__main__":
    main(sys.argv)
