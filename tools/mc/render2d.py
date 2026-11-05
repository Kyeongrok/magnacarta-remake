"""마그나카르타(Magna Carta, Softmax 2001) 2D 그림 읽개 — BGR · OBS · TXR · 글꼴.

게임 폴더의 파일을 읽기만 한다. 풀어낸 그림은 저장소에 넣지 않는다.
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-렌더링-2D.md

  python tools/mc/render2d.py bgr 209 out.png
  python tools/mc/render2d.py obs 44 outdir
  python tools/mc/render2d.py txr 0 out.png
  python tools/mc/render2d.py font 0 out.png "글자"
  python tools/mc/render2d.py fdi 210 out.png      (FrameData 한 장)
  python tools/mc/render2d.py title 800 out.png    (시작 화면 재구성)
"""
import io
import os
import struct
import sys

from PIL import Image

GAME_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")
MAGIC = 0x25129319  # 파일 앞 4바이트 19 93 12 25 (Softmax 1993-12-25)


def game_path(*rel):
    return os.path.join(GAME_DIR, *rel)


def u16(b, o): return struct.unpack_from("<H", b, o)[0]
def s16(b, o): return struct.unpack_from("<h", b, o)[0]
def u32(b, o): return struct.unpack_from("<I", b, o)[0]


# ── TGA (BGR·TXR 안에 든 것) ────────────────────────────────────────────────

def _looks_tga(d, o):
    if o + 18 > len(d) or d[o] != 0 or d[o + 1] != 0 or d[o + 2] not in (2, 10):
        return False
    w, h, bpp = struct.unpack_from("<HHB", d, o + 12)
    return 0 < w <= 4096 and 0 < h <= 4096 and bpp in (16, 24, 32)


def decode_tga(d, o=0):
    """무압축(2)·RLE(10) 트루컬러 TGA → RGBA Image. 원점 비트(desc bit5)를 따른다."""
    idlen, cmt, it = d[o], d[o + 1], d[o + 2]
    w, h, bpp, desc = struct.unpack_from("<HHBB", d, o + 12)
    p = o + 18 + idlen + (u16(d, o + 5) * ((d[o + 7] + 7) // 8) if cmt else 0)
    bp = bpp // 8
    n = w * h
    if it == 2:
        raw = d[p:p + n * bp]
    else:  # RLE
        out = bytearray()
        while len(out) < n * bp:
            c = d[p]; p += 1
            if c & 0x80:
                out += d[p:p + bp] * ((c & 0x7F) + 1); p += bp
            else:
                k = (c + 1) * bp; out += d[p:p + k]; p += k
        raw = bytes(out[:n * bp])
    if bp == 4:
        im = Image.frombuffer("RGBA", (w, h), raw, "raw", "BGRA", 0, 1)
    elif bp == 3:
        im = Image.frombuffer("RGB", (w, h), raw, "raw", "BGR", 0, 1).convert("RGBA")
    else:  # 16비트 = A1R5G5B5
        px = struct.unpack("<%dH" % n, raw)
        im = Image.new("RGBA", (w, h))
        im.putdata([(((v >> 10) & 31) * 255 // 31, ((v >> 5) & 31) * 255 // 31, (v & 31) * 255 // 31,
                     255 if (v & 0x8000 or not (desc & 15)) else 0) for v in px])
    if not desc & 0x20:  # 원점 왼쪽 아래
        im = im.transpose(Image.FLIP_TOP_BOTTOM)
    return im


# ── BGR ─────────────────────────────────────────────────────────────────────
# u32 MAGIC, u32 n, (i32 x0,y0,x1,y1)×n, u32 kind(5=TGA, 0=BMP), [12바이트 0], 그림 파일 통째

def read_bgr(num):
    d = open(game_path("BGR", "%04d.BGR" % num), "rb").read()
    return parse_bgr(d)


def parse_bgr(d):
    if u32(d, 0) != MAGIC:
        raise ValueError("BGR 머리가 아님")
    n = u32(d, 4)
    rects = [struct.unpack_from("<4i", d, 8 + 16 * i) for i in range(n)]
    p = 8 + 16 * n
    kind = u32(d, p)
    p += 4
    if kind == 5:
        if not _looks_tga(d, p):
            p += 12
        img = decode_tga(d, p)
    else:
        q = d.find(b"BM", p, p + 16)
        img = Image.open(io.BytesIO(d[q:])).convert("RGBA")
    return {"rects": rects, "kind": kind, "image": img}


# ── TXR ─────────────────────────────────────────────────────────────────────
# u32 kind(0=BMP 1=JPEG 2=GIF 5=TGA), 28바이트 쓰레기(초기화 안 된 메모리), 0x20부터 그림 파일

TXR_KINDS = {0: "bmp", 1: "jpg", 2: "gif", 5: "tga"}


def read_txr(num):
    d = open(game_path("TXRS", "%04d.txr" % num), "rb").read()
    return parse_txr(d)


def parse_txr(d):
    kind = u32(d, 0)
    if kind == 5:
        return decode_tga(d, 0x20)
    return Image.open(io.BytesIO(d[0x20:])).convert("RGBA")


# ── OBS (8비트 팔레트 스프라이트 + 모션표, 창세기전3 와 같은 꼴) ─────────────
# u16 0x1993, u16 벌 수 n, u16 벌 최대번호, (u16 번호, u32 위치)×n,
# u16 모션 수 m, u16 모션 최대번호, (u16 번호, u32 위치)×m
# 벌: u16 번호, u16 장 수, u16 장 최대번호+?, u8 투명 색인, 팔레트 768(RGB), (u16 장번호, u32 위치)×장 수
# 장: u16 번호, u32 크기, u16 w, u16 h, i16 x, i16 y, 압축 자료(크기)

OBS_PITCH = 0x280  # 압축 자료의 건너뛰기 값은 화면 너비 640 기준으로 적혀 있다


def parse_obs(d):
    if u16(d, 0) != 0x1993:
        raise ValueError("OBS 머리가 아님")
    n, nmax = u16(d, 2), u16(d, 4)
    groups = [(u16(d, 6 + 6 * i), u32(d, 8 + 6 * i)) for i in range(n)]
    p = 6 + 6 * n
    m = u16(d, p)
    motions = [(u16(d, p + 4 + 6 * i), u32(d, p + 6 + 6 * i)) for i in range(m)]
    out = []
    for gid, off in groups:
        g = _find_group(d, off, gid)
        if g:
            out.append(g)
    return {"groups": out, "motions": motions}


def _find_group(d, off, gid):
    for skip in range(0, 256):
        h = off + skip
        if h + 7 + 768 > len(d):
            return None
        if u16(d, h) != gid:
            continue
        cnt = u16(d, h + 2)
        if not 0 < cnt <= 1024:
            continue
        tr = d[h + 6]
        pal = d[h + 7:h + 7 + 768]
        c = h + 7 + 768
        frames, ok = [], True
        for _ in range(cnt):
            fid, fo = u16(d, c), u32(d, c + 2)
            if fo + 14 > len(d):
                ok = False; break
            size, w, hh = u32(d, fo + 2), u16(d, fo + 6), u16(d, fo + 8)
            if not (0 < w <= 2048 and 0 < hh <= 2048) or size > len(d) - fo - 14:
                ok = False; break
            frames.append((fid, fo))
            c += 6
        if ok:
            return {"id": gid, "transparent": tr, "palette": pal, "frames": frames}
    return None


def decode_obs_payload(payload, width, height, pitch=OBS_PITCH, fill=0):
    count = u16(payload, 0)
    out = bytearray([fill & 255]) * (width * height)
    cur, idx, wrapped = 2, 0, False
    for _ in range(count):
        skip = u16(payload, cur); quads = payload[cur + 2]; tail = payload[cur + 3]; cur += 4
        span = skip
        if skip:
            k = 0
            while k < (span & 0xFFFF):
                if wrapped:
                    wrapped = False
                    span += width - pitch
                    if k != (span & 0xFFFF):
                        idx += 1
                else:
                    idx += 1
                    if idx % width == 0:
                        span += width - pitch
                k += 1
        lit = tail + quads * 4
        out[idx:idx + lit] = payload[cur:cur + lit]
        idx += lit; cur += lit
        if (tail + quads) and idx % width == 0:
            wrapped = True
    return out


def obs_frame(d, group, fo, pitch=OBS_PITCH):
    fid, size = u16(d, fo), u32(d, fo + 2)
    w, h, x, y = u16(d, fo + 6), u16(d, fo + 8), s16(d, fo + 10), s16(d, fo + 12)
    ind = decode_obs_payload(d[fo + 14:fo + 14 + size], w, h, pitch, group["transparent"])
    pal = group["palette"]; tr = group["transparent"]
    rgba = bytearray(w * h * 4)
    for i, c in enumerate(ind):
        rgba[i * 4:i * 4 + 3] = pal[c * 3:c * 3 + 3]
        rgba[i * 4 + 3] = 0 if c == tr else 255
    return {"id": fid, "x": x, "y": y, "image": Image.frombytes("RGBA", (w, h), bytes(rgba))}


def read_obs(num):
    d = open(game_path("OBS", "%04d.OBS" % num), "rb").read()
    return d, parse_obs(d)


# ── FrameData (.fdi, 787바이트) — 2D 화면 한 장의 짜임 ──────────────────────
# u32 0xBA, u32 1, u32 0 | char name[128] | u8 flag | i32 x,y | i32 w,h | 원소 42바이트 × 15
# 원소: u8 used, u8 ?, i32 anchor(1~9), i32 x, i32 y, i32 kind, i32 id, i32 sub, i32 wdi, i32 w, i32 h, i32 ?
# kind 0·1 = 스프라이트(OBS id, 모션 sub)  3 = BGR id  4 = WinDiData 색 채움(w×h)  5 = 효과음 id  2 = 없음(assert)

FDI_KINDS = {0: "sprite", 1: "sprite", 3: "bgr", 4: "fill", 5: "sound"}


def read_fdi(num):
    d = open(game_path("FrameData", "%04d.fdi" % num), "rb").read()
    return parse_fdi(d)


def parse_fdi(d):
    name = d[12:140].split(b"\0")[0].decode("cp949", "replace")
    x, y, w, h = struct.unpack_from("<4i", d, 141)
    elems = []
    o = 157
    for _ in range(15):
        if o + 42 > len(d):
            break
        used, b1 = d[o], d[o + 1]
        anchor, ex, ey, kind, rid, sub, wdi, ew, eh, last = struct.unpack_from("<10i", d, o + 2)
        if used:
            elems.append(dict(b1=b1, anchor=anchor, x=ex, y=ey, kind=kind, id=rid, sub=sub, wdi=wdi, w=ew, h=eh, last=last))
        o += 42
    return dict(name=name, flag=d[140], pos=(x, y), size=(w, h), elems=elems)


def anchor_point(anchor, w, h):
    """원소 x,y 의 기준점 (MCFrame 0x5204e00b 점프표)."""
    return {1: (0, 0), 2: (w, 0), 3: (0, h), 4: (w, h), 5: (w // 2, 0), 6: (w // 2, h),
            7: (0, h // 2), 8: (w, h // 2), 9: (w // 2, h // 2)}.get(anchor, (0, 0))


def render_fdi(num):
    f = read_fdi(num)
    w, h = f["size"]
    if w <= 0 or h <= 0:  # 크기 0 이면 BGR 들을 덮는 크기로
        w = h = 0
        for e in f["elems"]:
            if e["kind"] == 3:
                im = read_bgr(e["id"])["image"]
                w, h = max(w, e["x"] + im.width), max(h, e["y"] + im.height)
    canvas = Image.new("RGBA", (max(w, 1), max(h, 1)), (0, 0, 0, 0))
    for e in f["elems"]:
        ax, ay = anchor_point(e["anchor"], *f["size"])
        if e["kind"] == 3:
            im = read_bgr(e["id"])["image"]
            canvas.alpha_composite(im, (ax + e["x"], ay + e["y"])) if ax + e["x"] < canvas.width else None
        elif e["kind"] == 4:
            c = read_wdi(e["wdi"])[0][1]
            canvas.paste(Image.new("RGBA", (e["w"], e["h"]), c + (255,)), (ax + e["x"], ay + e["y"]))
    return canvas


# ── WinDiData (.wdi, 44바이트) — 색 8개 ─────────────────────────────────────
# u32 0x20, u32 1, u32 0 | (u8 flag, u8 R, u8 G, u8 B) × 8

def read_wdi(num):
    d = open(game_path("WinDiData", "%04d.wdi" % num), "rb").read()
    return [(d[12 + 4 * i], tuple(d[13 + 4 * i:16 + 4 * i])) for i in range(8)]


# ── 글꼴 Font\{Eng,Kor,Chi}{0,1}.dat ────────────────────────────────────────
# 머리 100바이트: +2 글꼴 이름(cp949), +0x2a u8 너비표 있음, +0x5c u32 글자 수, +0x60 u32 자료 크기
# [너비표 u8×글자 수] 다음 1비트 그림: 세트0 = 14줄 × u16, 세트1 = 22줄 × 3바이트. 비트0 = 왼쪽.
# 번호: 첫 바이트 < 0xA4 → Eng[b-0x20], < 0xB0 → Eng[code-0xA3A0], < 0xCA → Kor[code-0xB0A0], 나머지 Chi[code-0xCAA0]

FONT_ROWS = {0: (14, 2), 1: (22, 3)}


class Font:
    def __init__(self, fontset=0):
        self.h, self.rb = FONT_ROWS[fontset]
        self.parts = {}
        for k in ("Eng", "Kor", "Chi"):
            d = open(game_path("Font", "%s%d.dat" % (k, fontset)), "rb").read()
            cnt = u32(d, 0x5C)
            has_w = d[0x2A] != 0
            self.parts[k] = (d[100:100 + cnt] if has_w else None, d[100 + (cnt if has_w else 0):])

    def glyphs(self, text):
        b = text.encode("cp949")
        i = 0
        while i < len(b):
            c = b[i]
            if c < 0xA4:
                yield "Eng", c - 0x20; i += 1; continue
            code = (c << 8) | b[i + 1]; i += 2
            if c < 0xB0:
                yield "Eng", code - 0xA3A0
            elif c < 0xCA:
                yield "Kor", code - 0xB0A0
            else:
                yield "Chi", code - 0xCAA0

    def draw(self, img, x, y, text, color=(255, 255, 255)):
        gb = self.h * self.rb
        for part, idx in self.glyphs(text):
            widths, data = self.parts[part]
            w = widths[idx] if widths else self.h
            g = data[idx * gb:(idx + 1) * gb]
            for yy in range(self.h):
                row = int.from_bytes(g[yy * self.rb:(yy + 1) * self.rb], "little")
                for xx in range(min(w, self.rb * 8)):  # 너비 밖은 쓰레기 비트
                    if row >> xx & 1 and 0 <= x + xx < img.width and 0 <= y + yy < img.height:
                        img.putpixel((x + xx, y + yy), color + (255,))
            x += w
        return x


# ── 시작 화면 재구성 (MCTitle, 메뉴가 뜬 상태 5) ──────────────────────────────

def compose_title(width=800, hover=0):
    hi = width >= 700
    bg = render_fdi(210 if hi else 209).crop((0, 0, width, 600 if hi else 480))
    x, y, step = (342, 362, 35) if hi else (261, 292, 30)
    for k, (normal, over) in enumerate(((225, 224), (227, 226), (229, 228), (231, 230))):
        bg.alpha_composite(render_fdi(over if k == hover else normal), (x, y + k * step))
    Font(0).draw(bg, bg.width - 0x34, bg.height - 0x14, "v1.04a", (169, 146, 109))
    return bg


# ── 명령 ────────────────────────────────────────────────────────────────────

def main(argv):
    kind = argv[1]
    if kind == "title":
        compose_title(int(argv[2])).convert("RGB").save(argv[3]); return
    num, out = argv[2], argv[3]
    if kind == "fdi":
        render_fdi(int(num)).save(out); print(read_fdi(int(num)))
    elif kind == "font":
        f = Font(int(num)); im = Image.new("RGBA", (600, 30), (0, 0, 0, 255)); f.draw(im, 2, 2, argv[4] if len(argv) > 4 else "마그나카르타 칼린츠 Start game"); im.save(out)
    elif kind == "bgr":
        r = read_bgr(int(num)); r["image"].save(out); print(r["rects"], r["image"].size)
    elif kind == "txr":
        read_txr(int(num)).save(out)
    elif kind == "obs":
        os.makedirs(out, exist_ok=True)
        d, o = read_obs(int(num))
        for g in o["groups"]:
            for fid, fo in g["frames"]:
                f = obs_frame(d, g, fo)
                f["image"].save(os.path.join(out, "g%02d_f%03d_x%d_y%d.png" % (g["id"], f["id"], f["x"], f["y"])))
        print(len(o["groups"]), "벌", "모션", len(o["motions"]))


if __name__ == "__main__":
    main(sys.argv)
