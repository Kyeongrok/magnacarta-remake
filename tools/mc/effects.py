"""마그나카르타 전투 효과 읽개 — EffScr 의 300(3D 효과 물체)·500(카메라) 를 풀어 본다.

분석: 옵시디안 분석-전투효과.md. 읽기만 한다(원본 폴더 MC_DIR).

    python tools/mc/effects.py dis 0x52096e67 200   # MCAlpha.dll 함수 풀기(문자열 주석)
    python tools/mc/effects.py fx 17                 # 한 연출의 300/310/306/500 줄과 자원
    python tools/mc/effects.py shot 443 out.png      # 3D 효과 물체(3DOBS) 하나 그리기
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


def pe(name="MCAlpha.dll"):
    import pefile
    p = pefile.PE(os.path.join(MC_DIR, name))
    return p, p.get_memory_mapped_image(), p.OPTIONAL_HEADER.ImageBase


def dis(va, n=150, name="MCAlpha.dll"):
    import capstone
    p, img, base = pe(name)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    out = []
    for i, ins in enumerate(md.disasm(img[va - base:va - base + 6000], va)):
        note = ""
        for tok in ins.op_str.replace(",", " ").replace("[", " ").replace("]", " ").split():
            if tok.startswith("0x5") and len(tok) == 10:
                v = int(tok, 16) - base
                if 0 <= v < len(img):
                    b = img[v:v + 80].split(b"\0")[0]
                    if len(b) >= 4 and all(c >= 32 for c in b):
                        note = " ; " + b.decode("cp949", "replace")[:70]
        out.append("%x %s %s%s" % (ins.address, ins.mnemonic, ins.op_str, note))
        if i >= n:
            break
    return out


def f32(i):
    return struct.unpack("<f", struct.pack("<i", i))[0]


def fx(num):
    import affect
    e = affect.load(num)
    print("3D 효과(표1):", e["tables"][1][1], " 카메라(표5):", e["tables"][5][1], " 배우(표4):", e["tables"][4][1])
    for it in e["items"]:
        print("  자리", it[0], "기준", it[1], it[2:5], "오프셋?", [round(f32(v), 2) for v in it[2:7]])
    for k, (t, co, ac) in enumerate(e["events"]):
        tick = 0
        for a in ac:
            if a[0] == 2:
                tick += a[1]
            if a[0] in (300, 301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 500, 501, 502, 503, 504, 505):
                v = list(a)
                while len(v) > 1 and v[-1] == 0:
                    v.pop()
                print("  사건%d @%d" % (k, tick), v)


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[1] == "dis":
        print("\n".join(dis(int(argv[2], 16), int(argv[3]) if len(argv) > 3 else 150, argv[4] if len(argv) > 4 else "MCAlpha.dll")))
    elif argv[1] == "fx":
        fx(int(argv[2]))
    elif argv[1] == "shot":
        shot(int(argv[2]), argv[3])


def shot(num, out, size=420):
    """3DOBS 강체(갈래 1) 효과 물체를 텍스처 입혀 위·옆에서 그린다(시퀀스 0 첫 틀, 정점은 뼈 지역 좌표)."""
    import numpy as np
    from PIL import Image
    import render3d as r
    m = r.load_3do(num)
    tris, uvs, texs = [], [], []
    ani = None
    if m.seqs and m.seqs[0]["keys"]:
        try:
            ani = r.load_ani(m.seqs[0]["keys"][0][0])
        except Exception:
            ani = None
    mats = r.absolute(ani, 0) if ani is not None and ani.bones == len(m.bone_pivots) else None
    if m.kind != 1:
        raise SystemExit("갈래 0(캐릭터) — render3d.py shot 을 쓸 것")
    for part in m.parts:
        if part["stride"] < 32:
            continue
        vb = np.frombuffer(part["vb"], dtype=r.VERT)
        p = np.concatenate([vb["pos"], np.ones((len(vb), 1), "f4")], 1)
        if mats is not None:
            p = p @ mats[part["bone"]]
        for mat, faces in part["groups"]:
            for f in faces:
                tris.append(p[f, :3]); uvs.append(vb["uv"][f]); texs.append((mat.tex, mat.flag))
    imgs = []
    for view in ("side", "top"):
        img = np.zeros((size, size, 3), "u1"); img[:] = (30, 30, 40)
        zb = np.full((size, size), np.inf, "f4")
        allp = np.concatenate(tris) if tris else np.zeros((1, 3))
        if view == "side":
            proj = lambda q: np.stack([q[:, 0], q[:, 2], -q[:, 1]], 1)
        else:
            proj = lambda q: np.stack([q[:, 0], -q[:, 1], -q[:, 2]], 1)
        P = proj(allp)
        lo, hi = P.min(0), P.max(0)
        sc = size * 0.9 / max(hi[0] - lo[0], hi[1] - lo[1], 1e-3)
        cache = {}
        for tri, uv, (tx, flag) in zip(tris, uvs, texs):
            q = proj(tri)
            sx = (q[:, 0] - (lo[0] + hi[0]) / 2) * sc + size / 2
            sy = size / 2 - (q[:, 1] - (lo[1] + hi[1]) / 2) * sc
            x0, x1 = int(max(sx.min(), 0)), int(min(sx.max() + 1, size))
            y0, y1 = int(max(sy.min(), 0)), int(min(sy.max() + 1, size))
            if x0 >= x1 or y0 >= y1:
                continue
            d = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (sy[1] - sy[0])
            if abs(d) < 1e-9:
                continue
            gx, gy = np.meshgrid(np.arange(x0, x1) + .5, np.arange(y0, y1) + .5)
            l1 = ((sx[2] - gx) * (sy[0] - gy) - (sx[0] - gx) * (sy[2] - gy)) / d
            l2 = ((sx[0] - gx) * (sy[1] - gy) - (sx[1] - gx) * (sy[0] - gy)) / d
            l0 = 1 - l1 - l2
            ins = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
            z = l0 * q[0, 2] + l1 * q[1, 2] + l2 * q[2, 2]
            ok = ins & (z < zb[y0:y1, x0:x1])
            if not ok.any():
                continue
            if tx not in cache:
                try:
                    cache[tx] = r.load_txr(tx)[0] if tx >= 0 else None
                except Exception:
                    cache[tx] = None
            t = cache[tx]
            if t is not None:
                u = l0 * uv[0, 0] + l1 * uv[1, 0] + l2 * uv[2, 0]
                v = l0 * uv[0, 1] + l1 * uv[1, 1] + l2 * uv[2, 1]
                th, tw = t.shape[:2]
                c = t[(np.mod(v, 1) * th).astype(int).clip(0, th - 1), (np.mod(u, 1) * tw).astype(int).clip(0, tw - 1)]
                col = c[..., :3].astype(float)
                if t.shape[2] == 4:
                    a = c[..., 3:4] / 255.0
                    col = col * a + img[y0:y1, x0:x1].astype(float) * (1 - a)
                    ok &= c[..., 3] > 8
            else:
                col = np.full(gx.shape + (3,), 200.0)
            reg = img[y0:y1, x0:x1]
            reg[ok] = col[ok].astype("u1")
            zb[y0:y1, x0:x1][ok] = z[ok]
        imgs.append(Image.fromarray(img))
    out_img = Image.new("RGB", (size * 2, size))
    out_img.paste(imgs[0], (0, 0)); out_img.paste(imgs[1], (size, 0))
    out_img.save(out)
    print("3do", num, "갈래", m.kind, "조각", len(m.parts), "뼈", len(m.bone_pivots), "시퀀스", [(s["a"], s["keys"][:2], s["loop"]) for s in m.seqs][:6],
          "재질", sorted({(mt.tex, mt.flag) for pt in m.parts for mt, _ in pt["groups"]}))


if __name__ == "__main__":
    main(sys.argv)
