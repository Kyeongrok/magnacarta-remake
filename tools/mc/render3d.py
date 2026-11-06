"""마그나카르타 3D 자료 읽개 — .3do(모델) · .ani(뼈 애니) · .amd(캐릭터 모델 목록) · .txr(텍스처).

원본 폴더의 파일을 읽기만 한다. 뽑은 그림은 저장소에 넣지 않는다.
형식 풀이: 옵시디안 mv/Project/Magnacarta/분석/분석-렌더링-3D.md

    python tools/mc/render3d.py info 101          # 3do 구조 요약
    python tools/mc/render3d.py ani 0101          # ani 요약
    python tools/mc/render3d.py shot 101 out.png [시퀀스 차례] [열쇠틀]   # 텍스처 입혀 정면에서 그리기
"""
import os
import struct
import sys

import numpy as np

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


def path(*p):
    return os.path.join(MC_DIR, *p)


class Reader:
    def __init__(self, data):
        self.d = data
        self.o = 0

    def eof(self):
        return self.o >= len(self.d)

    def u32(self):
        if self.o + 4 > len(self.d):     # 원본 Cfread 처럼 파일 끝 뒤는 0 으로 본다
            self.o += 4
            return 0
        v = struct.unpack_from("<I", self.d, self.o)[0]
        self.o += 4
        return v

    def i32(self):
        if self.o + 4 > len(self.d):
            self.o += 4
            return 0
        v = struct.unpack_from("<i", self.d, self.o)[0]
        self.o += 4
        return v

    def u16(self):
        v = struct.unpack_from("<H", self.d, self.o)[0]
        self.o += 2
        return v

    def f32(self, n=1):
        v = struct.unpack_from("<%df" % n, self.d, self.o)
        self.o += 4 * n
        return v if n > 1 else v[0]

    def arr(self, dtype, n):
        a = np.frombuffer(self.d, dtype=dtype, count=n, offset=self.o)
        self.o += a.nbytes
        return a

    def raw(self, n):
        v = self.d[self.o:self.o + n]
        self.o += n
        return v


# ── .3do ─────────────────────────────────────────────────────────────────────
# MCAlpha.dll 0x5202d35e (CMCObjManager, "3dOBS\%04d.3do")

VERT = np.dtype([("pos", "<f4", 3), ("nrm", "<f4", 3), ("uv", "<f4", 2)])
DYN_VERT = np.dtype([("pos", "<f4", 3), ("link", "<u2", 4), ("x0c", "<i4"),
                     ("x10", "<u2"), ("x12", "<u2"), ("w", "<f4"), ("vtx", "<u4")])


class Material:
    pass


class Model:
    pass


def read_material(r):
    m = Material()
    m.color = r.f32(3)          # +08 RGB 배수
    m.flag = r.u32()            # +58 0 보통 · 1 · 2 (두 면/알파 갈래)
    m.ambient = r.f32(4)        # +18
    m.diffuse = r.f32(4)        # +28
    m.specular = r.f32(4)       # +38
    m.power = r.f32()           # +48
    m.tex = r.i32()             # +4C TXRS 번호 (-1 = 없음)
    m.x50 = r.u32()
    m.x54 = r.u32()
    return m


def load_3do(num):
    r = Reader(open(path("3DOBS", "%04d.3do" % num), "rb").read())
    m = Model()
    m.num = num
    m.kind = r.u32()
    m.parts = []                # [(verts, bone0, bone1, w0, w1, [(material, faces)])]
    if m.kind == 0:             # 뼈 하나짜리 몸 하나 (캐릭터)
        n = r.u32()
        m.fvf = r.u32()         # 0x112 = D3DFVF_XYZ|NORMAL|TEX1 (읽은 뒤 0x112 로 덮어씀)
        m.x10 = r.u32()
        verts = r.arr(VERT, n)
        b0 = r.arr("<u2", n)
        b1 = r.arr("<u2", n)
        w0 = r.arr("<f4", n)
        w1 = r.arr("<f4", n)
        groups = []
        for _ in range(r.u32()):
            mat = read_material(r)
            nf = r.u32()
            groups.append((mat, r.arr("<u2", nf * 3).reshape(-1, 3)))
        m.parts.append((verts, b0, b1, w0, w1, groups))
        m.x30 = r.u32()
        nb = r.u32()
        m.bone_pivots = r.arr("<f4", nb * 3).reshape(-1, 3)
    else:                       # 갈래 1: 뼈마다 조각 하나 (강체 — 배경 사물·무기·탈것 등)
        nb = r.u32()
        m.bone_pivots = r.arr("<f4", nb * 3).reshape(-1, 3)
        for bone in range(nb):
            nmat = r.u32()
            if nmat == 0:       # 그릴 것 없는 뼈
                continue
            nv, fvf, stride = r.u32(), r.u32(), r.u32()
            vb = r.raw(nv * stride)
            groups = []
            for _ in range(nmat):
                mat = read_material(r)
                nf = r.u32()
                groups.append((mat, r.arr("<u2", nf * 3).reshape(-1, 3) if nf else np.zeros((0, 3), "<u2")))
            x18 = r.u32()
            m.parts.append(dict(bone=bone, fvf=fvf, stride=stride, nv=nv, vb=vb, groups=groups, x18=x18))
    # 여기서부터는 없어도 된다 (옛 파일은 뼈 기준점에서 끝난다 — 원본은 EOF 에서 Cfread 가 0 을 돌려줌)
    # 옷자락·머리칼 흔들림(ASRDynamics1/2)
    m.dynamics = []
    nd = r.i32() if m.kind == 0 else 0      # 갈래 1 은 이 칸이 없다 (obj+0x80 이 1 일 때만 읽음)
    for _ in range(max(nd, 0)):
        kind = r.u32()
        n = r.u32()
        dv = r.arr(DYN_VERT, n)
        k = r.u32()
        idx = r.arr("<u4", k)
        center = r.f32(3)
        extra = [r.u32() for _ in range(4 if kind == 1 else 8)]
        m.dynamics.append((kind, dv, idx, center, extra))
    # 동작(시퀀스) 목록 — 시퀀스마다 .ani 열쇠틀 목록과 붙이개 명령
    tag = r.raw(16)
    m.has_veranim = tag.startswith(b"VERANIM")
    m.seqs = []
    for _ in range(max(r.i32(), 0)):
        a, b, c = r.u32(), r.u32(), r.u32()
        nk = r.u16()
        loop = r.u16()
        keys = [(r.u32(), r.u32()) for _ in range(nk)]   # (ani 번호, 틀 수/시간)
        cmds = []
        for _ in range(r.u32()):
            kind = r.i32()
            arg = r.u32()
            body = (r.u32(), [r.u32() for _ in range(5)], r.u32(), r.f32(6))
            cmds.append((kind, arg, body))
        m.seqs.append(dict(a=a, b=b, c=c, loop=loop, keys=keys, cmds=cmds))
    m.has_box = r.u32()
    m.box = r.arr("<f4", 24).reshape(8, 3) if m.has_box == 1 else None
    m.veranim = []
    if m.has_veranim:
        r.raw(16)
        for _ in range(r.u32()):
            a = r.u32()
            k = r.u32()
            m.veranim.append((a, [(r.u16(), r.f32(3)) for _ in range(k)]))
    m.rest = max(len(r.d) - r.o, 0)
    return m


# ── .ani ─────────────────────────────────────────────────────────────────────
# MCAlpha.dll 0x5202c88a ("Ainim\%04d.ani")

class Anim:
    pass


def load_ani(num):
    r = Reader(open(path("AINIM", "%04d.ani" % num), "rb").read())
    a = Anim()
    a.num = num
    a.x00 = r.u32()
    a.length = r.u32()      # +08 전체 틱 수 (= 3do 시퀀스 b)
    a.step = r.u32()        # +0C 틀 하나의 틱 수 (= 3do 시퀀스 c)
    a.frames = r.u32()      # +10 틀 수
    a.bones = r.u32()       # +14
    # 뼈마다 틀 수만큼 4x4 행렬(행 우선, D3D 꼴 — 아래 줄이 이동). 모델 공간 절대 행렬.
    a.mats = r.arr("<f4", a.bones * (a.frames + 1) * 16).reshape(a.bones, a.frames + 1, 4, 4)  # 틀 수 + 1 (마지막 = 첫 틀로 돌아옴)
    a.links = []
    for _ in range(a.bones):
        x = r.u32()
        k = r.u32()
        a.links.append((x, list(r.arr("<u4", k))))
    a.rest = len(r.d) - r.o
    return a


def load_amd(num):
    d = open(path("AmdData", "%04d.amd" % num), "rb").read()
    n, x = struct.unpack_from("<2I", d)
    return x, [struct.unpack_from("<2I", d, 8 + 8 * i) for i in range(n)]


# ── .txr ─────────────────────────────────────────────────────────────────────

def load_txr(num):
    """TXRS/nnnn.txr → RGBA ndarray.

    머리 0x20 바이트 (u32 갈래 + 원본 툴이 남긴 쓰레기 값) 뒤에 그림 파일이 통째로 들어 있다.
    갈래 0 = BMP(8/24비트), 1 = JPEG, 2 = GIF, 5 = TGA(24/32비트, 32비트는 알파 = 투명도).
    """
    from PIL import Image
    import io
    d = open(path("TXRS", "%04d.txr" % num), "rb").read()
    kind = struct.unpack_from("<I", d)[0]
    img = Image.open(io.BytesIO(d[0x20:]), formats=["TGA"] if kind == 5 else None).convert("RGBA")
    a = np.asarray(img)
    # TGA 가 아닌 것(BMP·JPEG·GIF)은 원본이 위아래 뒤집어 쓴다(분석-렌더링-3D 「텍스처 위아래」) — 하늘 3036·로프마 0100·0051 로 확인
    if kind != 5 and img.format != "TGA":
        a = a[::-1].copy()
    return a, kind


# ── 그리기 (소프트 래스터) ───────────────────────────────────────────────────

def skin(model, ani, frame):
    """(틀린 가설, 비교용으로만 남김) 정점이 모델 공간이라 보고 inv(첫 틀) · 지금 틀을 곱한다. 원본은 local_pos 쪽."""
    verts, b0, b1, w0, w1, groups = model.parts[0]
    p = np.concatenate([verts["pos"], np.ones((len(verts), 1), "f4")], 1)
    if ani is None:
        return p[:, :3]
    bind_inv = np.linalg.inv(ani.mats[:, 0])
    cur = ani.mats[:, frame % ani.frames]
    sk = np.einsum("bij,bjk->bik", bind_inv, cur)        # 행벡터: v * inv(bind) * cur
    out = np.einsum("ni,nij->nj", p, sk[np.minimum(b0, ani.bones - 1)]) * w0[:, None]
    has1 = b1 != 0xFFFF
    if has1.any():
        out[has1] += np.einsum("ni,nij->nj", p[has1], sk[b1[has1]]) * w1[has1, None]
    return out[:, :3]


def render(model, out_png, ani=None, frame=0, size=512, yaw=0.0, local=True, zup=True):
    from PIL import Image
    verts, b0, b1, w0, w1, groups = model.parts[0]
    if ani is None:
        pos = model.parts[0][0]["pos"].copy()
    elif local:
        pos = local_pos(model, ani, frame)
    else:
        pos = skin(model, ani, frame)
    if zup:                                   # 원본은 Z 가 위 → 화면 (x, z, -y)
        pos = np.stack([pos[:, 0], pos[:, 2], -pos[:, 1]], 1)
    c, s = np.cos(yaw), np.sin(yaw)
    rot = np.array([[c, 0, -s], [0, 1, 0], [s, 0, c]], "f4")
    pos = pos @ rot
    lo, hi = pos.min(0), pos.max(0)
    scale = (size * 0.9) / max(hi[0] - lo[0], hi[1] - lo[1])
    cx, cy = (lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2
    sx = (pos[:, 0] - cx) * scale + size / 2
    sy = size / 2 - (pos[:, 1] - cy) * scale
    sz = pos[:, 2]
    img = np.zeros((size, size, 3), "u1")
    img[:] = (40, 44, 52)
    zbuf = np.full((size, size), np.inf, "f4")
    texs = {}
    for mat, faces in groups:
        if mat.tex >= 0 and mat.tex not in texs:
            try:
                texs[mat.tex] = load_txr(mat.tex)[0]
            except Exception:
                texs[mat.tex] = None
        tex = texs.get(mat.tex)
        uv = verts["uv"]
        for f in faces:
            ax, ay, az = sx[f], sy[f], sz[f]
            x0, x1 = int(max(ax.min(), 0)), int(min(ax.max() + 1, size))
            y0, y1 = int(max(ay.min(), 0)), int(min(ay.max() + 1, size))
            if x0 >= x1 or y0 >= y1:
                continue
            d = (ax[1] - ax[0]) * (ay[2] - ay[0]) - (ax[2] - ax[0]) * (ay[1] - ay[0])
            if abs(d) < 1e-9:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
            l1 = ((ax[2] - gx) * (ay[0] - gy) - (ax[0] - gx) * (ay[2] - gy)) / d
            l2 = ((ax[0] - gx) * (ay[1] - gy) - (ax[1] - gx) * (ay[0] - gy)) / d
            l0 = 1 - l1 - l2
            inside = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
            if not inside.any():
                continue
            z = l0 * az[0] + l1 * az[1] + l2 * az[2]
            zb = zbuf[y0:y1, x0:x1]
            ok = inside & (z < zb)
            if not ok.any():
                continue
            if tex is not None:
                u = l0 * uv[f[0], 0] + l1 * uv[f[1], 0] + l2 * uv[f[2], 0]
                v = l0 * uv[f[0], 1] + l1 * uv[f[1], 1] + l2 * uv[f[2], 1]
                th, tw = tex.shape[:2]
                tx = (np.mod(u, 1) * tw).astype(int).clip(0, tw - 1)
                ty = (np.mod(v, 1) * th).astype(int).clip(0, th - 1)
                col = tex[ty, tx, :3]
                # 갈래 1·2 재질은 알파로 오려낸다(게임 셰이더와 같게 0.3 아래 버림) — 갈래 0 은 알파를 안 쓴다(분석-렌더링-3D)
                if mat.flag != 0 and tex.shape[2] == 4:
                    ok = ok & (tex[ty, tx, 3] >= 77)
                    if not ok.any():
                        continue
            else:
                col = np.full(gx.shape + (3,), 180, "u1")
            # 면 법선으로 가벼운 음영
            p3 = pos[f]
            nrm = np.cross(p3[1] - p3[0], p3[2] - p3[0])
            nl = np.linalg.norm(nrm)
            shade = 0.55 + 0.45 * abs(nrm[2] / nl) if nl > 0 else 1.0
            reg = img[y0:y1, x0:x1]
            reg[ok] = (col[ok] * shade).astype("u1")
            zb[ok] = z[ok]
    im = Image.fromarray(img)
    if out_png:
        im.save(out_png)
    return im


def absolute(ani, frame):
    """부모 쪽으로 곱해 올라가 뼈마다 모델 공간 행렬을 만든다 (행벡터: 자식 = 지역 * 부모)."""
    loc = ani.mats[:, frame % (ani.frames + 1)]
    out = [None] * ani.bones
    parent = [-1] * ani.bones
    for i, (_, ch) in enumerate(ani.links):
        for c in ch:
            parent[c] = i
    def get(i):
        if out[i] is None:
            out[i] = loc[i] if parent[i] < 0 else loc[i] @ get(parent[i])
        return out[i]
    return np.stack([get(i) for i in range(ani.bones)])


def local_pos(model, ani, frame):
    """정점이 뼈 지역 좌표라고 보고 뼈의 모델 공간 행렬을 곱한다."""
    verts, b0, b1, w0, w1, groups = model.parts[0]
    p = np.concatenate([verts["pos"], np.ones((len(verts), 1), "f4")], 1)
    cur = absolute(ani, frame)
    return np.einsum("ni,nij->nj", p, cur[np.minimum(b0, ani.bones - 1)])[:, :3]


def main(argv):
    cmd = argv[1]
    if cmd == "info":
        m = load_3do(int(argv[2]))
        print("kind", m.kind, "parts", len(m.parts), "bones", len(m.bone_pivots), "dyn", len(m.dynamics),
              "seqs", len(m.seqs), "box", m.has_box, "veranim", m.has_veranim, "rest", m.rest)
        if m.kind == 0:
            v = m.parts[0][0]
            print("verts", len(v), "bbox", v["pos"].min(0), v["pos"].max(0))
            for mat, f in m.parts[0][5]:
                print("  mat tex", mat.tex, "flag", mat.flag, "faces", len(f))
        for i, s in enumerate(m.seqs):
            print("  seq", i, s["a"], s["b"], s["c"], "loop", s["loop"], "keys", s["keys"], "cmds", len(s["cmds"]))
    elif cmd == "ani":
        a = load_ani(int(argv[2]))
        print("length", a.length, "step", a.step, "frames", a.frames, "bones", a.bones, a.x00, "rest", a.rest)
        print("links", a.links[:8])
    elif cmd == "shot":
        m = load_3do(int(argv[2]))
        seq = int(argv[4]) if len(argv) > 4 else -1
        frame = int(argv[5]) if len(argv) > 5 else 0
        if seq < 0:
            seq = 0
        ani_n, fr = m.seqs[seq]["keys"][frame % len(m.seqs[seq]["keys"])]
        render(m, argv[3], load_ani(ani_n), fr, yaw=np.pi)


if __name__ == "__main__":
    main(sys.argv)
