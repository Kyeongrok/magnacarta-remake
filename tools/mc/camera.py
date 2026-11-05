"""마그나카르타 필드 탐색 카메라(CCTV) 자료 — fld D 묶음 풀기.

    python tools/mc/camera.py 531 592 546      # 필드마다 D 구역 표
    python tools/mc/camera.py stats            # 모든 fld 의 a·b·c 값 분포

분석 글: 옵시디안 Project/Magnacarta/분석/분석-필드카메라.md
D 기록 0x7c: i32 id, f32[3] 카메라 처음 자리(+0x04), f32[3]×8 구역 상자 꼭짓점(+0x10), i32 a(+0x70), i32 b(+0x74), i32 c(+0x78).
필드 읽개 0x5206a11e~0x5206a1fc 가 MCObserver::AddCCTV(0x5205197f)에 {id, 자리, b, a×10.0, c} 와 꼭짓점을 넘긴다.
"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(__file__))
import stage  # noqa: E402

FOLLOW_DIV = {0: 10.0, 1: 20.0, 2: 40.0, 3: 60.0, 4: 80.0}   # 0x520532f0: b → 나눗수(그 밖 8)


def regions(num):
    f = stage.Fld(num)
    out = []
    for rid, pos, box, a, b, c in f.d:
        xs = [v[0] for v in box]; ys = [v[1] for v in box]; zs = [v[2] for v in box]
        out.append(dict(id=rid, cam=pos, lo=(min(xs), min(ys), min(zs)), hi=(max(xs), max(ys), max(zs)),
                        a=a, b=b, c=c, tether=a * 10.0, div=FOLLOW_DIV.get(b, 8.0)))
    start = next((p for x0, x1, p, q, _ in f.b if x1 == 0), None)
    return out, start


def inside(r, p):
    return all(r["lo"][k] <= p[k] <= r["hi"][k] for k in range(3))


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[1:] == ["stats"]:
        ca, cb, cc, n = Counter(), Counter(), Counter(), 0
        for fn in sorted(os.listdir(stage.game_path("Field"))):
            try:
                rs, _ = regions(int(fn[:4]))
            except Exception:
                continue
            for r in rs:
                ca[r["a"]] += 1; cb[r["b"]] += 1; cc[r["c"]] += 1; n += 1
        print("구역", n)
        print("a", ca.most_common(12))
        print("b", cb.most_common(12))
        print("c", cc.most_common(12))
        return
    for num in map(int, argv[1:]):
        rs, start = regions(num)
        print("fld %d  구역 %d  칼린츠 시작 %s" % (num, len(rs), [round(v, 1) for v in start] if start else None))
        for r in rs:
            cam = r["cam"]
            d = ((cam[0] - start[0]) ** 2 + (cam[1] - start[1]) ** 2 + (cam[2] - start[2]) ** 2) ** 0.5 if start else 0
            print("  D%-3d 카메라(%.0f, %.0f, %.0f)  상자 x %.0f~%.0f y %.0f~%.0f z %.0f~%.0f  a=%d(줄 %.0f) b=%d(÷%.0f) c=%d  시작과 거리 %.0f %s" % (
                r["id"], *cam, r["lo"][0], r["hi"][0], r["lo"][1], r["hi"][1], r["lo"][2], r["hi"][2],
                r["a"], r["tether"], r["b"], r["div"], r["c"], d, "← 시작 자리가 안에 듦" if start and inside(r, start) else ""))


if __name__ == "__main__":
    main(sys.argv)
