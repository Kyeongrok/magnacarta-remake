"""마그나카르타 스테이지(맵) 읽개 — MAP\\*.wrd, Field\\*.fld, Camera\\*.cam, Walk\\*.wlk, Atsn\\*.asn.

원본 폴더의 파일을 읽기만 한다. 풀어낸 자료는 저장소에 넣지 않는다.

    python tools/mc/stage.py info 16            # wrd 요약
    python tools/mc/stage.py top 16 out.png     # 위에서 본 그림
    python tools/mc/stage.py fld 625            # Field 파일 요약
    python tools/mc/stage.py cam 1              # 카메라 파일 요약
    python tools/mc/stage.py list               # 모든 wrd 요약 표
    python tools/mc/stage.py field 625 out.png  # 필드: 그 wrd 위에 인물·상자 표시
"""
import io
import os
import struct
import sys

GAME_DIR = os.environ.get(
    "MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


def game_path(*rel):
    return os.path.join(GAME_DIR, *rel)


class Reader:
    def __init__(self, data, off=0):
        self.d, self.o = data, off

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.o)[0]
        self.o += 4
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.o)[0]
        self.o += 4
        return v

    def f32(self, n=1):
        v = struct.unpack_from("<%df" % n, self.d, self.o)
        self.o += 4 * n
        return v if n > 1 else v[0]

    def cnt(self, unit=1):
        """개수 하나를 읽고, 남은 바이트로 다 담을 수 없으면 ValueError."""
        n = self.u32()
        if n * unit > len(self.d) - self.o:
            raise ValueError("개수 %d 가 너무 큼 @0x%x" % (n, self.o - 4))
        return n

    def raw(self, n):
        if self.o + n > len(self.d):
            raise ValueError("끝을 넘음 @0x%x" % self.o)
        v = self.d[self.o:self.o + n]
        self.o += n
        return v


# ── TXRS\%04d.txr ─────────────────────────────────────────────────────────────
# 0x20 바이트 머리(+0 u32 종류: 0 BMP, 1 JPEG, 5 TGA — 나머지는 쓰레기 값), 그 뒤 그림 파일 그대로

def load_txr(num):
    from PIL import Image
    with open(game_path("TXRS", "%04d.txr" % num), "rb") as f:
        data = f.read()
    img = Image.open(io.BytesIO(data[0x20:]))
    fmt = img.format
    img = img.convert("RGB")
    # TGA 가 아닌 것은 원본이 위아래 뒤집어 쓴다(분석-렌더링-3D 「텍스처 위아래」)
    if fmt != "TGA" and struct.unpack_from("<I", data)[0] != 5:
        img = img.transpose(Image.FLIP_TOP_BOTTOM)
    return img


# ── MAP\%04d.wrd ──────────────────────────────────────────────────────────────
# 로더: MCAlpha.dll 0x5202a780 근처 (sprintf "Map\%04d.wrd" @0x5202a7e0),
#       라이트맵 부분 읽개 0x520190ff.

class Wrd:
    def __init__(self, num):
        self.num = num
        with open(game_path("MAP", "%04d.wrd" % num), "rb") as f:
            d = f.read()
        self.size = len(d)
        r = Reader(d, 0)
        # 머리 0x100: "MC File Version 1.400000" + 만든 이 이름(0x40) + 날짜(0x80)
        self.version = d[0:0x40].split(b"\0")[0].decode("latin1")
        self.author = d[0x40:0x80].split(b"\0")[0].decode("latin1")
        self.date = d[0x80:0xc0].split(b"\0")[0].decode("latin1")
        r.o = 0x100
        self.total_tris = r.u32()            # 모든 면의 삼각형 수 합
        n_lm = r.u32()
        # 라이트맵 장: id, 한 변 w, 조각 수 cnt, 조각 cnt×28, RGB w×w×3
        self.lightmaps = []
        for _ in range(n_lm):
            lid, w, cnt = r.u32(), r.u32(), r.u32()
            rects = [struct.unpack_from("<I6f", d, r.o + 28 * i) for i in range(cnt)]
            r.o += 28 * cnt
            rgb = r.raw(w * w * 3)
            self.lightmaps.append({"id": lid, "w": w, "rects": rects, "rgb": rgb})
        r.o += 0x100                         # 둘째 머리 (쓰레기 채움) — Cfseek(+0x100, SEEK_CUR)
        n_grp = r.u32()
        self.groups = []
        for _ in range(n_grp):
            g = {"subs": []}
            ns = r.u32()
            g["pivots"] = [r.f32(3) for _ in range(ns)]
            for _ in range(ns):
                nv, fvf, stride, unk, lm_base, nf = (r.u32() for _ in range(6))
                sub = {"fvf": fvf, "stride": stride, "unk": unk, "lm_base": lm_base,
                       "verts": [], "faces": []}
                for i in range(nv):
                    sub["verts"].append(struct.unpack_from("<3f", d, r.o + i * stride) +
                                        self._uvs(d, r.o + i * stride, fvf))
                r.o += nv * stride
                for k in range(nf):
                    mat = r.f32(17)          # D3DMATERIAL8: 확산·주변·반사·발광 RGBA + power
                    tex = r.i32()            # TXRS 번호, -1 없음
                    flag = r.i32()           # 1 이면 다른 방식으로 텍스처 등록(투명?)
                    ntri = r.u32()
                    face = {"mat": mat, "tex": tex, "flag": flag, "ntri": ntri,
                            "lm_pages": [], "idx": []}
                    if ntri:
                        face["x"] = r.u32()
                        lc = sum(1 for lm in self.lightmaps if lm["id"] == lm_base + k)
                        face["lm_pages"] = [r.u32() for _ in range(lc)]   # 장마다 삼각형 수
                        face["idx"] = struct.unpack_from("<%dH" % (ntri * 3), d, r.o)
                        r.o += ntri * 6
                    sub["faces"].append(face)
                g["subs"].append(sub)
            # 변환 키프레임: a, b, keys, tracks — 트랙마다 id, n, n×u32, (keys+1)×4x4 행렬
            a, b, keys, ntr = (r.u32() for _ in range(4))
            g["anim"] = (a, b, keys)
            g["tracks"] = []
            for _ in range(ntr):
                tid, c = r.u32(), r.u32()
                extra = [r.u32() for _ in range(c)]
                mats = [r.f32(16) for _ in range(keys + 1)]
                g["tracks"].append({"id": tid, "extra": extra, "mats": mats})
            self.groups.append(g)
        n1 = r.u32()
        self.pairs = [(r.u32(), r.u32()) for _ in range(n1)]
        n2 = r.u32()
        self.lights = [r.raw(64) for _ in range(n2)]
        self.tail_vec = r.f32(3)
        self.tail = r.u32()
        self.end = r.o

    @staticmethod
    def _uvs(d, o, fvf):
        # FVF: 0x002 XYZ, 0x010 NORMAL, 0x040 DIFFUSE, 0x100·0x200 TEX1·TEX2
        o += 12
        if fvf & 0x10:
            o += 12
        if fvf & 0x40:
            o += 4
        if fvf & 0x80:
            o += 4
        ntex = (fvf >> 8) & 0xf
        return struct.unpack_from("<%df" % (2 * ntex), d, o) if ntex else ()

    def world_tris(self):
        """(세 꼭짓점 월드 좌표, 세 uv0, 세 uv1, tex, 라이트맵 장 번호) 를 차례로 낸다."""
        for g in self.groups:
            m = g["tracks"][0]["mats"][0] if g["tracks"] else None
            for sub in g["subs"]:
                vs = sub["verts"]
                for k, face in enumerate(sub["faces"]):
                    pages = [i for i, lm in enumerate(self.lightmaps)
                             if lm["id"] == sub["lm_base"] + k]
                    bounds, acc = [], 0
                    for n in face["lm_pages"]:
                        acc += n
                        bounds.append(acc)
                    idx = face["idx"]
                    for t in range(face["ntri"]):
                        page = None
                        for pi, bd in enumerate(bounds):
                            if t < bd:
                                page = pages[pi] if pi < len(pages) else None
                                break
                        tri = [vs[idx[3 * t + j]] for j in range(3)]
                        pos = [_xf(m, v[:3]) for v in tri]
                        uv0 = [v[3:5] if len(v) >= 5 else (0, 0) for v in tri]
                        uv1 = [v[5:7] if len(v) >= 7 else (0, 0) for v in tri]
                        yield pos, uv0, uv1, face["tex"], page


def _xf(m, p):
    if m is None:
        return p
    x, y, z = p
    return (x * m[0] + y * m[4] + z * m[8] + m[12],
            x * m[1] + y * m[5] + z * m[9] + m[13],
            x * m[2] + y * m[6] + z * m[10] + m[14])


def render_top(num, out, size=1024, facing=1, zmax=None, marks=None):
    """위(+Z)에서 내려다본 그림. 위를 보는 면(facing=+1, 감김으로 판단)만 텍스처×라이트맵으로 그린다.
    zmax 를 주면 그보다 높은 삼각형은 뺀다(지붕 걷기)."""
    import numpy as np
    from PIL import Image
    w = Wrd(num)
    tris = []
    for t in w.world_tris():
        (a, b, c) = t[0]
        nz = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
        if nz * facing <= 0:
            continue
        if zmax is not None and max(p[2] for p in t[0]) > zmax:
            continue
        tris.append(t)
    xs = [p[0] for t in tris for p in t[0]]
    ys = [p[1] for t in tris for p in t[0]]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    sc = (size - 20) / max(x1 - x0, y1 - y0, 1)
    W, H = int((x1 - x0) * sc) + 20, int((y1 - y0) * sc) + 20
    img = np.zeros((H, W, 3), np.float32)
    img[:] = (20, 20, 30)
    zbuf = np.full((H, W), -1e9, np.float32)
    texc = {}

    def tex_arr(tex):
        if tex not in texc:
            try:
                texc[tex] = np.asarray(load_txr(tex), np.float32)
            except Exception:
                texc[tex] = np.full((2, 2, 3), (255, 0, 255), np.float32)
        return texc[tex]

    lma = [np.frombuffer(l["rgb"], np.uint8).reshape(l["w"], l["w"], 3).astype(np.float32)
           for l in w.lightmaps]
    for pos, uv0, uv1, tex, page in tris:
        P = np.array([((p[0] - x0) * sc + 10, (y1 - p[1]) * sc + 10, p[2]) for p in pos], np.float32)
        bx0, by0 = np.floor(P[:, :2].min(0)).astype(int)
        bx1, by1 = np.ceil(P[:, :2].max(0)).astype(int)
        bx0, by0 = max(bx0, 0), max(by0, 0)
        bx1, by1 = min(bx1, W - 1), min(by1, H - 1)
        if bx1 < bx0 or by1 < by0:
            continue
        gx, gy = np.meshgrid(np.arange(bx0, bx1 + 1) + .5, np.arange(by0, by1 + 1) + .5)
        (ax, ay, _), (bx, by, _), (cx, cy, _) = P
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            continue
        l0 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
        l1 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
        l2 = 1 - l0 - l1
        m = (l0 >= -1e-4) & (l1 >= -1e-4) & (l2 >= -1e-4)
        if not m.any():
            continue
        z = l0 * P[0, 2] + l1 * P[1, 2] + l2 * P[2, 2]
        zb = zbuf[by0:by1 + 1, bx0:bx1 + 1]
        m &= z > zb
        if not m.any():
            continue
        U0 = np.array(uv0, np.float32)
        U1 = np.array(uv1, np.float32)
        u = l0 * U0[0, 0] + l1 * U0[1, 0] + l2 * U0[2, 0]
        v = l0 * U0[0, 1] + l1 * U0[1, 1] + l2 * U0[2, 1]
        ta = tex_arr(tex) if tex >= 0 else np.full((2, 2, 3), 200, np.float32)
        th, tw = ta.shape[:2]
        col = ta[(np.floor(v * th).astype(int) % th), (np.floor(u * tw).astype(int) % tw)]
        if page is not None:
            la = lma[page]
            n = la.shape[0]
            lu = l0 * U1[0, 0] + l1 * U1[1, 0] + l2 * U1[2, 0]
            lv = l0 * U1[0, 1] + l1 * U1[1, 1] + l2 * U1[2, 1]
            lit = la[np.clip((lv * n).astype(int), 0, n - 1), np.clip((lu * n).astype(int), 0, n - 1)]
            col = col * lit / 128.0
        reg = img[by0:by1 + 1, bx0:bx1 + 1]
        reg[m] = col[m]
        zb[m] = z[m]
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    if marks:
        from PIL import ImageDraw
        dr = ImageDraw.Draw(im)
        for kind, pts, col, label in marks:
            q = [((x - x0) * sc + 10, (y1 - y) * sc + 10) for x, y in pts]
            if kind == "pt":
                (px, py), = q
                dr.ellipse((px - 5, py - 5, px + 5, py + 5), outline=col, width=2)
            else:
                dr.polygon(q, outline=col)
                px, py = q[0]
            if label:
                dr.text((px + 6, py - 6), label, fill=col)
    # 빈 둘레 잘라내기
    a = np.asarray(im).astype(int)
    m = np.abs(a - np.array((20, 20, 30))).sum(2) > 6
    ys_, xs_ = np.nonzero(m)
    if len(xs_):
        im = im.crop((max(xs_.min() - 8, 0), max(ys_.min() - 8, 0), xs_.max() + 8, ys_.max() + 8))
    im.save(out)
    return (x0, x1, y0, y1), len(tris)


def render_field(num, out, size=1400):
    """필드(fld) 하나: 그 wrd 를 위에서 그리고 B(빨강)·C(하늘)·D 상자(노랑)·E 상자(분홍)를 얹는다."""
    f = Fld(num)
    marks = []
    for x0, x1, p, q, _ in f.b:
        marks.append(("pt", [p[:2]], (255, 60, 60), "B%d/%d" % (x0, x1)))
    for x0, x1, x2, p, q, _ in f.c:
        marks.append(("pt", [p[:2]], (80, 220, 255), "C%d/%d" % (x0, x1)))
    for x0, p, box, *ints in f.d:
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))],
                      (255, 230, 0), "D%d %s" % (x0, ints)))
    for p, x, box in f.e:
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))],
                      (255, 120, 220), "E%d" % x))
    return render_top(f.h[0], out, size=size, marks=marks)


# ── Field\%04d.fld ───────────────────────────────────────────────────────────
# 읽개: MCAlpha.dll 0x52065f87 (sprintf "Field\%04d.fld" @0x52065fb4). 같은 함수가 이어서 FPB\%04d.fpb 를 읽는다.
# 필드 객체(arg2) 의 자리를 이름 대신 +오프셋으로 적는다.

FLD_A, FLD_B, FLD_C, FLD_D, FLD_E = 0x4c, 0x34, 0x38, 0x7c, 0x70


class Fld:
    def __init__(self, num):
        self.num = num
        with open(game_path("Field", "%04d.fld" % num), "rb") as f:
            d = f.read()
        self.size = len(d)
        r = Reader(d)
        self.h = [r.u32() for _ in range(6)]              # +4 wrd 번호, +8, +0xc, +0x10, +0x14, +0x18
        self.slots = [(r.i32(), r.f32(5)) for _ in range(5)]   # +0x30 + i*0x18
        self.v_a8 = r.f32(6)
        self.v_c0 = r.f32(6)
        self.v_d8 = r.f32(5)
        self.v_ec = r.f32(5)
        self.h100 = [r.i32() for _ in range(3)]            # +0x100 +0x104 +0x108
        n_g = r.cnt(0x94 - 0x34)                                     # +0x478
        self.h10c = r.i32()
        self.name = r.raw(0x12)                           # +0x110
        self.blob = r.raw(0x2fe)                          # +0x122
        self.v420 = r.f32(4)
        self.h430 = r.i32()
        self.h434 = r.i32()
        n = r.cnt(0x10)                                       # +0x438  — 0x4c 짜리
        self.a = []
        for _ in range(n):
            v0, v1 = r.f32(4), r.f32(4)
            ints = [r.i32() for _ in range(7)]
            v2 = r.f32(4)
            self.a.append((v0, v1, ints, v2))
        n = r.cnt(0x10)                                       # +0x440  — 0x3c 짜리
        self.b = []
        for _ in range(n):
            x1, x0 = r.i32(), r.i32()
            p, q = r.f32(3), r.f32(3)
            self.b.append((x0, x1, p, q, r.raw(0x14)))
        n = r.cnt(0x10)                                       # +0x448  — 0x48 짜리
        self.c = []
        for _ in range(n):
            x1, x0, x2 = r.i32(), r.i32(), r.i32()
            p, q = r.f32(3), r.f32(3)
            self.c.append((x0, x1, x2, p, q, r.raw(0x14)))
        n = r.cnt(0x10)                                       # +0x450  — 0x7c 짜리: 상자
        self.d = []
        for _ in range(n):
            x0 = r.i32()
            p = r.f32(3)
            box = [r.f32(3) for _ in range(8)]
            self.d.append((x0, p, box, r.i32(), r.i32(), r.i32()))
        n = r.cnt(0x10)                                       # +0x458  — 0xe8 짜리: 상자 + 평면 6장(읽은 뒤 만든다)
        self.e = []
        for _ in range(n):
            p = r.f32(3)
            x = r.i32()
            box = [r.f32(3) for _ in range(8)]
            self.e.append((p, x, box))
        n = r.cnt(0x10)                                       # +0x470  — 0x18 짜리 + (n+1)×0x4c
        self.f = []
        for _ in range(n):
            hd = [r.i32() for _ in range(5)]
            if not 0 <= hd[4] < 10000:
                raise ValueError("항목 수 이상")
            items = [r.raw(0x4c) for _ in range(hd[4] + 1)]
            self.f.append((hd, items))
        self.g = []                                       # +0x478 — 0x94 짜리
        for _ in range(n_g):
            a, b, c = r.i32(), r.cnt(), r.cnt()
            if b * c * 36 > len(d):
                raise ValueError("키 수 이상")
            keys = [[r.raw(0x20) for _ in range(b)] for _ in range(c)]
            times = [[r.u32() for _ in range(b)] for _ in range(c)]
            m = r.f32(16)
            v = r.f32(4)
            kind = r.u32()
            extra = r.raw(0x6c)
            self.g.append({"a": a, "b": b, "c": c, "keys": keys, "times": times, "m": m, "v": v,
                           "kind": kind, "extra": extra})
        self.end = r.o


def load_fpb(num):
    """FPB\%04d.fpb — 필드에서 쓰는 글: n, n×(번호, 자리, 길이), 전체 길이, CP949 글."""
    with open(game_path("Fpb", "%04d.fpb" % num), "rb") as f:
        d = f.read()
    n = struct.unpack_from("<I", d, 0)[0]
    ent = [struct.unpack_from("<3I", d, 4 + 12 * i) for i in range(n)]
    o = 4 + 12 * n + 4
    return [(i, d[o + a:o + a + l].rstrip(b"\x00").split(b"\x00")[0].decode("cp949", "replace")) for i, a, l in ent]


# ── Camera\%04d.cam ─────────────────────────────────────────────────────────
# 읽개: MCAlpha.dll 0x5209d0ce 근처(전투 쪽). u32 틀 수, u32 키 사이 틀 수, u32 n, 키 0x4c × (n 또는 n+1).
# 대부분 틀 수 = 키 사이 틀 수 × n 이고 키는 n+1 개(끝 키 포함).
# 키 0x4c = 4x4 행렬(카메라 월드 행렬, 행 우선, 넷째 행 = 자리) + 시야각(도) + near + far.
# fld 의 F 묶음(카메라 컷)도 같은 0x4c 키를 쓴다.

def load_cam(num):
    with open(game_path("Camera", "%04d.cam" % num), "rb") as f:
        d = f.read()
    a, b, n = struct.unpack_from("<3I", d, 0)
    cnt = (len(d) - 12) // 0x4c
    keys = [struct.unpack_from("<19f", d, 12 + 0x4c * i) for i in range(cnt)]
    return a, b, n, keys


def cmd_fld(num):
    f = Fld(num)
    print("Field\%04d.fld" % num, f.size, "바이트, 읽은 끝", f.end, "(나머지는 사건 자료)")
    print("wrd", f.h[0] if f.h[0] != 0xffffffff else -1, "h8", f.h[1], "hc(번호-25)", f.h[2], "h", f.h[3:])
    print("조명 A", len(f.a))
    for x0, x1, p, q, _ in f.b:
        print("  B 인물 id %d 캐릭터 %d 자리 %s 돌림 %s" % (x0, x1, [round(v, 1) for v in p], [round(v, 1) for v in q]))
    for x0, x1, x2, p, q, _ in f.c:
        print("  C id %d 값 %d %d 자리 %s" % (x0, x1, x2, [round(v, 1) for v in p]))
    for x0, p, box, *ints in f.d:
        print("  D 상자 id %d 가운데 %s 값 %s" % (x0, [round(v, 1) for v in p], ints))
    for p, x, box in f.e:
        print("  E 상자 %d 자리 %s" % (x, [round(v, 1) for v in p]))
    for hd, items in f.f:
        print("  F 카메라 컷", hd, "키", len(items))
    print("G", len(f.g))


# ── 명령 ──────────────────────────────────────────────────────────────────────

def cmd_info(num):
    w = Wrd(num)
    print("MAP\\%04d.wrd" % num, w.size, "바이트", w.version, w.author, w.date)
    print("삼각형", w.total_tris, "라이트맵", [(l["id"], l["w"], len(l["rects"])) for l in w.lightmaps])
    texs = sorted({f["tex"] for g in w.groups for s in g["subs"] for f in s["faces"]})
    fvfs = sorted({(s["fvf"], s["stride"]) for g in w.groups for s in g["subs"]})
    print("그룹", len(w.groups), "FVF", [(hex(a), b) for a, b in fvfs])
    print("텍스처", texs)
    anim = [i for i, g in enumerate(w.groups) if g["anim"][2] > 0]
    print("키 있는 그룹", len(anim), "쌍", len(w.pairs), "조명", len(w.lights), "끝", w.tail_vec, w.tail,
          "읽은 끝", w.end, "/", w.size)


def cmd_list():
    for fn in sorted(os.listdir(game_path("MAP"))):
        num = int(fn[:4])
        try:
            w = Wrd(num)
            ok = "OK" if w.end == w.size else "남음 %d" % (w.size - w.end)
            print("%04d %9d tri %6d grp %4d lm %2d light %3d %s %s" % (
                num, w.size, w.total_tris, len(w.groups), len(w.lightmaps), len(w.lights), w.date, ok))
        except Exception as e:
            print("%04d 실패 %s" % (num, e))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    c = sys.argv[1] if len(sys.argv) > 1 else "list"
    if c == "info":
        cmd_info(int(sys.argv[2]))
    elif c == "top":
        fac = int(sys.argv[4]) if len(sys.argv) > 4 else 1
        zm = float(sys.argv[5]) if len(sys.argv) > 5 else None
        print(render_top(int(sys.argv[2]), sys.argv[3], facing=fac, zmax=zm))
    elif c == "list":
        cmd_list()
    elif c == "fld":
        cmd_fld(int(sys.argv[2]))
    elif c == "cam":
        a, b, n, keys = load_cam(int(sys.argv[2]))
        print("틀", a, "키 사이", b, "n", n, "키", len(keys))
        for k in keys[:3]:
            print([round(v, 3) for v in k])
    elif c == "field":
        print(render_field(int(sys.argv[2]), sys.argv[3]))
