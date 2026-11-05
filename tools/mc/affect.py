"""마그나카르타 전투 연출(Affect) 읽개 — EffScr\\%04d.efs.

Att +0x20(Affect 번호) → EffScr\\%04d.efs. 읽개 MCAlpha.dll 0x52092200 부근(MCBtlAffect.cpp):
  자원 표 6개 { u32 n, u32 ?, n × (u32 차례, u32 번호) }   → 객체 +0x8c/+0x90 … +0xb4/+0xb8
  u32 n, u32 ?, n × 7 u32 (+0x1c~0x24 는 0 으로)          → +0xbc/+0xc0
  사건 묶음(MCEvent::Load 0x5204ab93 — 필드 사건과 같은 60바이트 명령 꼴)
분석: 옵시디안 분석-전투연출.md

    python tools/mc/affect.py check          # 205개가 끝까지 읽히는지
    python tools/mc/affect.py dump 162       # 한 개 풀기
"""
import os
import struct
import sys

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


def path(*p):
    return os.path.join(MC_DIR, *p)


def load(num):
    d = open(path("EffScr", "%04d.efs" % num), "rb").read()
    o = 0
    tables = []
    for _ in range(6):
        n, x = struct.unpack_from("<Ii", d, o); o += 8
        rows = [struct.unpack_from("<Ii", d, o + 8 * i) for i in range(n)]
        o += 8 * n
        tables.append((x, rows))
    n, x = struct.unpack_from("<Ii", d, o); o += 8
    items = [struct.unpack_from("<7i", d, o + 28 * i) for i in range(n)]
    o += 28 * n
    # 사건 묶음: u32 n, n × { u32 깃발, i32 조건 수, 조건 60×, i32 명령 수, 명령 60× }
    ne, = struct.unpack_from("<I", d, o); o += 4
    events = []
    for _ in range(ne):
        trig, ni = struct.unpack_from("<Ii", d, o); o += 8
        conds = [struct.unpack_from("<I14i", d, o + 60 * i) for i in range(ni)]; o += 60 * ni
        nt, = struct.unpack_from("<i", d, o); o += 4
        acts = [struct.unpack_from("<I14i", d, o + 60 * i) for i in range(nt)]; o += 60 * nt
        events.append((trig, conds, acts))
    return dict(tables=tables, items=items, items_x=x, events=events, end=o, size=len(d))


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[1] == "check":
        ok = bad = 0
        for fn in sorted(os.listdir(path("EffScr"))):
            try:
                e = load(int(fn[:4]))
                if e["end"] == e["size"]:
                    ok += 1
                else:
                    bad += 1; print(fn, "남음", e["size"] - e["end"])
            except Exception as ex:
                bad += 1; print(fn, "실패", ex)
        print("ok", ok, "bad", bad)
    elif argv[1] == "dump":
        e = load(int(argv[2]))
        for i, (x, rows) in enumerate(e["tables"]):
            print("표%d x=%d" % (i, x), rows)
        print("묶음 x=%d" % e["items_x"], e["items"])
        for k, (t, co, ac) in enumerate(e["events"]):
            print("사건", k, "깃발", t, "조건", [list(c[:5]) for c in co])
            for a in ac:
                v = list(a)
                while len(v) > 1 and v[-1] == 0:
                    v.pop()
                print("    ", v)
    elif argv[1] == "ops":
        from collections import Counter
        c = Counter()
        for fn in sorted(os.listdir(path("EffScr"))):
            try:
                evs = load(int(fn[:4]))["events"]
            except struct.error:
                continue
            for t, co, ac in evs:
                for a in ac:
                    c[a[0]] += 1
        print(sorted(c.items()))


if __name__ == "__main__":
    main(sys.argv)
