"""마그나카르타 도시·장소 정리 — fld 이름 칸 · 장소 제목(사건 701) · 필드 이어짐(사건 4/5) · 인물 · 상자 · 마을 사람 · 카메라 구역.

원본 폴더(환경 변수 MC_DIR)를 읽기만 한다. 다른 읽개(stage·story·battle·items·render3d)를 import 해 쓴다.
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-도시.md

    python tools/mc/towns.py places          # 이름 있는 장소 → fld · wrd · 장소 제목
    python tools/mc/towns.py links           # 장소 사이 이어짐 (GOTO 4/5)
    python tools/mc/towns.py town 594        # 필드 하나: 인물 · 물체 · 상자 · 적 · 나가는 길
    python tools/mc/towns.py chests          # 모든 상자(갈래 6 보통 · 7 자물쇠) → 아이템 · 열쇠
    python tools/mc/towns.py npcs            # 말 거는 마을 사람(갈래 3) 목록
    python tools/mc/towns.py shot 594 out.png  # 위에서 본 그림 + 인물·상자·출구
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle      # noqa: E402
import items       # noqa: E402
import stage       # noqa: E402
import story       # noqa: E402

PLAYER = (10000, 10004)        # 사건 명령에서 「지금 조작하는 캐릭터」


def fld_raw(n):
    return open(stage.game_path("Field", "%04d.fld" % n), "rb").read()


def fld_numbers():
    return sorted(int(fn[:4]) for fn in os.listdir(stage.game_path("Field")))


def place_name(n):
    """fld 파일 +0xfc 의 0x12 바이트(메모리 +0x110) = 장소 이름. 세이브 +4 에 그대로 적힌다."""
    d = fld_raw(n)
    if len(d) < 0x10e:
        return ""
    return d[0xfc:0xfc + 0x12].split(b"\0")[0].decode("cp949", "replace")


# ── fld C 묶음(물체) — 읽개 0x5206683b: 파일 첫 값 → +4(모델), 둘째 → +0(id), 셋째 → +8(갈래),
#    +0xc 자리, +0x18 돌림, +0x24 20바이트 그대로. 쓰임은 MCFldDealer 0x5206bbbb(갈래 3·6·7). ──

# 갈래 3 = 말 거는 마을 사람: +0x24 줄 아이템(−1 없음), +0x26 준 표시 전역스위치, +0x28·+0x2a·+0x2c Fpb 대사 id 차례(−1 끝),
#          +0x2e 값(50 등, 뜻 모름). 모델 = 3do 번호(시퀀스 100 서기·101 걷기가 있는 캐릭터 몸).
# 갈래 6 = 보물상자(모델 581·582): +0x24 아이템, +0x26 연 표시 전역스위치.
# 갈래 7 = 자물쇠 상자(모델 657 은 · 656 금): 6 과 같고 +0x28 열쇠 아이템 id(43 은열쇠 · 44 금열쇠). 0 도 그대로 아이템 0 으로 찾는다.
# 갈래 11 = 돌아다니는 인물·몬스터(추정, 모델이 캐릭터 몸). 0 = 자리 표지(사건 202·GOTO 5 의 들어갈 자리).
KIND = {0: "표지(자리)", 3: "마을 사람", 4: "?4", 6: "보물상자", 7: "자물쇠 상자", 9: "?9", 10: "?10", 11: "돌아다니는 몸", 2: "?2"}


def objects(f):
    out = []
    for oid, model, kind, p, q, raw in f.c:
        w = struct.unpack("<10h", raw)
        out.append({"id": oid, "model": model, "kind": kind, "pos": p, "rot": q, "w": w})
    return out


def item_names():
    its, _ = items.load_items()
    return {r["id"]: r["name"] for r in its}


def char_names():
    return {c["id"]: c["name"] for c in battle.load_chars()}


def events(n):
    d = fld_raw(n)
    off = story.find_events(d)
    return story.parse_events(d, off)[0] if off is not None else []


def exits(n, evs=None):
    """(조건 구역 번호들, 스위치 조건, 갈 필드, 들어갈 자리 a1·a2, 명령 4/5) 목록."""
    out = []
    for trig, conds, acts in evs if evs is not None else events(n):
        regions = [c[2] for c in conds if c[0] == 304]
        sw = [(c[1], story.CMP.get(c[2], c[2]), c[3]) for c in conds if c[0] in (100, 101)]
        for a in acts:
            if a[0] in (4, 5):
                out.append({"regions": regions, "switch": sw, "to": a[1], "entry": a[2:4] if a[0] == 5 else None, "op": a[0]})
    return out


def titles(n, evs=None):
    """사건 701 의 a0 = 그 필드 Fpb 의 줄 id = 화면에 띄우는 장소 제목(예: 「로웰숲 남동부」)."""
    lines = None
    out = []
    for trig, conds, acts in evs if evs is not None else events(n):
        for a in acts:
            if a[0] == 701 and a[1] >= 0:
                if lines is None:
                    try:
                        lines = dict(story.fpb(n - 25))
                    except OSError:
                        lines = {}
                out.append(lines.get(a[1], "?%d" % a[1]))
    return out


def all_fields():
    for n in fld_numbers():
        try:
            f = stage.Fld(n)
        except Exception:
            f = None
        yield n, f


# ── 명령 ─────────────────────────────────────────────────────────────────────

def cmd_places():
    by = collections.defaultdict(list)
    for n in fld_numbers():
        nm = place_name(n)
        if nm and all(0xac00 <= ord(ch) <= 0xd7a3 or ch == " " for ch in nm):
            by[nm].append(n)
    for nm, ns in sorted(by.items(), key=lambda x: x[1][0]):
        wr = sorted({struct.unpack_from("<i", fld_raw(n))[0] for n in ns})
        tl = []
        for n in ns:
            for t in titles(n):
                if t not in tl:
                    tl.append(t)
        print("%-12s fld %s  wrd %s  제목 %s" % (nm, ns, wr, tl))


def cmd_links():
    names = {n: place_name(n) for n in fld_numbers()}
    edges = collections.Counter()
    for n in fld_numbers():
        a = names[n] or "(%04d)" % n
        for e in exits(n):
            b = names.get(e["to"]) or "(%04d)" % e["to"]
            if a != b and names[n] and names.get(e["to"]):
                edges[(a, b)] += 1
    for (a, b), k in sorted(edges.items()):
        print("%s -> %s  (%d)" % (a, b, k))


def cmd_town(n):
    f = stage.Fld(n)
    cn, inames = char_names(), item_names()
    evs = events(n)
    lines = None
    print("Field %04d  이름 %r  wrd %d  Fpb %04d  제목 %s" % (n, place_name(n), f.h[0], n - 25, titles(n, evs)))
    for _bid, cid, p, q, _ in f.b:
        print("  인물 CharData %3d %-10s 자리 %s" % (cid, cn.get(cid, "?"), [round(v) for v in p]))
    for o in objects(f):
        k = o["kind"]
        extra = ""
        if k in (6, 7):
            extra = "아이템 %d %s, 연 표시 전역스위치 %d" % (o["w"][0], inames.get(o["w"][0], "?"), o["w"][1])
            if k == 7:
                extra += ", 열쇠 %d %s" % (o["w"][2], inames.get(o["w"][2], "?"))
        elif k == 3:
            if lines is None:
                lines = dict(story.fpb(n - 25))
            talk = [lines.get(i, "?") for i in o["w"][2:5] if i >= 0]
            extra = "대사 %s / 선물 %s 스위치 %d / 값 %d" % (talk, inames.get(o["w"][0], "-") if o["w"][0] >= 0 else "-", o["w"][1], o["w"][5])
        print("  물체 id %3d 갈래 %2d %-8s 모델 %4d 자리 %s %s" % (o["id"], k, KIND.get(k, "?"), o["model"], [round(v) for v in o["pos"]], extra))
    for x0, p, box, *ints in f.d:
        print("  카메라 구역 D(CCTV) id %d 가운데 %s 값 %s" % (x0, [round(v) for v in p], ints))
    for p, x, box in f.e:
        print("  사건 구역 E id %d 가운데 %s" % (x, [round(v) for v in p]))
    for e in exits(n, evs):
        print("  나감 구역 %s 조건 %s → fld %04d %r 자리 %s" % (e["regions"], e["switch"], e["to"], place_name(e["to"]) if os.path.exists(stage.game_path("Field", "%04d.fld" % e["to"])) else "?", e["entry"]))


def cmd_chests():
    inames = item_names()
    n_all = 0
    for n, f in all_fields():
        if not f:
            continue
        for o in objects(f):
            if o["kind"] in (6, 7):
                n_all += 1
                key = " 열쇠 %s" % inames.get(o["w"][2], o["w"][2]) if o["kind"] == 7 else ""
                print("%04d %-12s 상자 id %2d 모델 %d  %s(%d)  스위치 %d%s" % (
                    n, place_name(n), o["id"], o["model"], inames.get(o["w"][0], "?"), o["w"][0], o["w"][1], key))
    print("합", n_all)


def cmd_npcs():
    inames = item_names()
    for n, f in all_fields():
        if not f:
            continue
        for o in objects(f):
            if o["kind"] == 3:
                w = o["w"]
                print("%04d %-12s id %2d 모델 %d 대사 %s 값 %d 선물 %s 스위치 %d" % (
                    n, place_name(n), o["id"], o["model"], [i for i in w[2:5] if i >= 0], w[5], inames.get(w[0], "-") if w[0] >= 0 else "-", w[1]))


def cmd_shot(n, out):
    f = stage.Fld(n)
    marks = []
    for _bid, cid, p, q, _ in f.b:
        marks.append(("pt", [p[:2]], (255, 70, 70), "PC%d" % cid))
    for o in objects(f):
        col = {6: (255, 220, 0), 7: (255, 140, 0), 3: (200, 80, 255)}.get(o["kind"])
        if col:
            marks.append(("pt", [o["pos"][:2]], col, {6: "BOX", 7: "LOCK", 3: "NPC%d" % o["id"]}[o["kind"]]))
        elif o["kind"] == 0:
            marks.append(("pt", [o["pos"][:2]], (90, 220, 255), "#%d" % o["id"]))
    for x0, p, box, *ints in f.d:
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))], (120, 255, 120), "CAM%d" % x0))
    for p, x, box in f.e:   # E = 사건 구역(조건 304) — 출구 등
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        to = [e["to"] for e in exits(n) if x in e["regions"]]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))], (255, 90, 200), "E%d%s" % (x, "->%s" % to if to else "")))
    print(stage.render_top(f.h[0], out, size=1400, marks=marks))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    c = sys.argv[1] if len(sys.argv) > 1 else "places"
    if c == "places":
        cmd_places()
    elif c == "links":
        cmd_links()
    elif c == "town":
        cmd_town(int(sys.argv[2]))
    elif c == "chests":
        cmd_chests()
    elif c == "npcs":
        cmd_npcs()
    elif c == "shot":
        cmd_shot(int(sys.argv[2]), sys.argv[3])
