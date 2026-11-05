"""마그나카르타 몬스터·던전 정리 — CharData(몬스터·보스) · Mon(조우 무리) · Pat · Field(조우 표·전투 필드·상자).

원본 폴더(환경 변수 MC_DIR)를 읽기만 한다. 다른 읽개(battle·render3d·stage·story·items)를 import 해 쓴다(고치지 않는다).
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-몬스터.md · 분석-던전.md

    python tools/mc/monsters.py list            # 몬스터·보스 표
    python tools/mc/monsters.py mon             # Mon 무리 139개
    python tools/mc/monsters.py pat             # Pat 16개 (파티 짜임 미리 정한 것)
    python tools/mc/monsters.py enc             # 조우가 있는 필드(던전) 표
    python tools/mc/monsters.py boss            # 사건 9(전투 시작)가 부르는 전투 필드와 적 배치
    python tools/mc/monsters.py chest           # 상자(종류 6·7) 목록
    python tools/mc/monsters.py graph 로웰숲    # 이름이 같은 필드끼리 이어짐(GOTO)
    python tools/mc/monsters.py shot out.png    # 몬스터 정면 그림 모음
    python tools/mc/monsters.py top 529 out.png # 던전 필드 하나 위에서 본 그림 + 상자·출구·조우 상자
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle      # noqa: E402
import story       # noqa: E402
import stage       # noqa: E402

EL = battle.ELEMENTS
KIND = {0: "몬스터", 1: "인물", 5: "보스"}

# ── 조우 (MCFldDealer, MCAlpha.dll) ──────────────────────────────────────────
# 필드 장면 객체 +0x10cc = 필드 자료 객체(fld 읽개 0x52065f90 의 둘째 인자) 의 +0.
# 조우 검사 0x5206e07d: 단계(fld 머리 +0x10, 장면 +0x10e0) → ValueData 0x2b~0x2f 주기 P.
ENC_PERIOD = {0: 1300, 1: 1100, 2: 800, 3: 400, 4: 300}   # 5 = 조우 없음 (점프표 0x5206e22e)
ENC_DIV = 3                                                # ValueData 0x30


def cp(b):
    return b.split(b"\0")[0].decode("cp949", "replace")


def chars_by_id():
    return {c["id"]: c for c in battle.load_chars()}


def field_raw(n):
    return story.read("Field", "%04d.fld" % n)


def encounter(n):
    """fld 머리의 조우 자료.

    (이름 0xfc 0x12 바이트 CP949)
    0x00 wrd, 0x04 음악, 0x08 Fpb, 0x0c 조우 켬(1), 0x10 조우 단계(0~4, 5 없음), 0x14 기본 전투 무대(MCBLevel 번호, −1 = 조우 안 함)
    0x18 5 × {i32 전투 무대, i32 E상자 id ×5}  구역별 무대 (플레이어가 그 E 상자 안이면 그 무대) — 0x5206def1
    0xc0 i32 ×5  Mon 무리 번호 (−1 빈칸, 빈칸 빼고 고르게 하나)                  — 0x5206de43
    0xd4 i32 ×5  전투 필드 번호 (rand % 5, 빈칸 검사 없음)                       — 0x5206b787
    """
    d = field_raw(n)
    h = struct.unpack_from("<6i", d, 0)
    zones = [struct.unpack_from("<6i", d, 0x18 + 24 * i) for i in range(5)]
    mons = list(struct.unpack_from("<5i", d, 0xc0))
    bfs = list(struct.unpack_from("<5i", d, 0xd4))
    return dict(wrd=h[0], music=h[1], fpb=h[2], on=h[3], level=h[4], arena=h[5],
                zones=[(z[0], [b for b in z[1:] if b != -1]) for z in zones if z[0] != -1],
                mons=mons, bfs=bfs, name=cp(d[0xfc:0x10e]))


def field_name(n):
    try:
        return cp(field_raw(n)[0xfc:0x10e])
    except OSError:
        return "없는 파일"


def load_mon(num):
    """Mon\\NNNN.mon: u32 8, u32 1, u32 ?, i32 ×8 (CharData id). 원본은 앞 5칸만 쓴다(0x5209dbcb), −1 빈칸."""
    d = story.read("Mon", "%04d.mon" % num)
    v = struct.unpack("<11i", d)
    return list(v[3:8]), list(v[8:])


def mon_numbers():
    return sorted(int(f[:4]) for f in os.listdir(story.path("Mon")))


def load_pat(num):
    """Pat\\NNNN.pat (96바이트) — 파티 짜임 미리 정한 것. 읽개 0x520b3d34 (DM +0x1c 인원 수를 셈).
    u32 21, u32 1, u32 0, i32×7 파티원 CharData id(+0x20), i32 (+0x3c), i32×4 진형 칸(+0x40), i32 (+0x50), i32×7 (+0x54)."""
    d = story.read("Pat", "%04d.pat" % num)
    v = struct.unpack("<24i", d)
    return dict(members=[x for x in v[3:10] if x != -1], x3c=v[10], formation=list(v[11:15]), x50=v[15], x54=list(v[16:23]),
                tail=v[23:])


def battle_fields():
    """사건 9(전투 시작 a0 = 전투 필드) 를 부르는 필드 → 전투 필드."""
    out = []
    for n, evs in story.all_events():
        for _t, _c, acts in evs or []:
            for a in acts:
                if a[0] == 9:
                    out.append((n, a[1]))
    return out


def battle_actors(n):
    """전투 필드의 B 묶음: (CharData id, 편 word). 파일 꼬리 첫 word(메모리 +0x28)를 0x5209dade 가 0→1 · 1→2 · 2→0 으로 바꾼다.
    파일 값 2 = 아군 자리(칼린츠 일행), 0 = 적. 1 은 이 판 전투 필드에 없다."""
    d = field_raw(n)
    f = stage.Fld(n)
    out = []
    for _bid, cid, p, _q, tail in f.b:
        side = struct.unpack_from("<h", tail, 0)[0]
        out.append((cid, side, p))
    return f, out


def chests():
    """fld C 묶음 종류 6(상자) · 7(잠긴 상자): 꼬리 word0 아이템 id, word1 ?, word2 열쇠 id (분석-아이템 §3)."""
    out = []
    for fn in sorted(os.listdir(stage.game_path("Field"))):
        n = int(fn[:4])
        try:
            f = stage.Fld(n)
        except Exception:
            continue
        for oid, model, kind, p, _q, tail in f.c:
            if kind in (6, 7):
                w = struct.unpack_from("<3h", tail)
                out.append(dict(field=n, id=oid, model=model, kind=kind, item=w[0], w1=w[1], key=w[2] if kind == 7 else -1, pos=p))
    return out


def gotos():
    g = collections.defaultdict(set)
    for n, evs in story.all_events():
        for _t, _c, acts in evs or []:
            for a in acts:
                if a[0] in (4, 5):
                    g[n].add(a[1])
    return g


# ── 명령 ─────────────────────────────────────────────────────────────────────

def cmd_list():
    abi = battle.load_abi()
    import render3d
    for c in battle.load_chars():
        if c["kind"] not in (0, 5):
            continue
        d = battle.derived(c)
        try:
            _, slots = render3d.load_amd(c["amd"])
            m = slots[0][1]
        except Exception:
            m = -1
        parts = "".join(EL[c[k]] for k in ("head", "breast", "abdomen", "arm", "leg"))
        names = [abi[a]["name"] for a in c["abilities"] if a in abi]
        print("%3d %-14s %s WTP %3d HP %7d(최대 %8d) 공 %4d 방 %4d 민 %3d 부위 %s 3do %4d 기술 %s" % (
            c["id"], c["name"], KIND[c["kind"]], c["wtp"], c["hp"], d["max_hp"], d["p_atk"], d["ac"], d["dex"], parts, m, names))


def cmd_mon():
    ch = chars_by_id()
    for n in mon_numbers():
        used, rest = load_mon(n)
        names = [ch[x]["name"] if x in ch else "?%d" % x for x in used if x != -1]
        extra = [x for x in rest if x not in (-1,)]
        print("%04d %d마리 %s%s" % (n, len(names), " · ".join(names), "  (6~8칸 %s — 안 씀)" % extra if extra else ""))


def cmd_pat():
    ch = chars_by_id()
    for n in range(16):
        p = load_pat(n)
        print("%04d" % n, [ch[x]["name"] if x in ch else x for x in p["members"]], p)


def cmd_enc():
    mon_used = collections.Counter()
    for fn in sorted(os.listdir(story.path("Field"))):
        n = int(fn[:4])
        try:
            stage.Fld(n)          # 옛 판 머리(22개)는 건너뛴다
            e = encounter(n)
        except Exception:
            continue
        if e["arena"] == -1 and not e["zones"]:
            continue
        if not (0 <= e["level"] <= 5):
            continue
        ms = [m for m in e["mons"] if m != -1]
        mon_used.update(ms)
        print("%04d %-10s wrd %4d 단계 %d(P %s) 무대 %d 구역 %s Mon %s 전투필드 %s" % (
            n, e["name"], e["wrd"], e["level"], ENC_PERIOD.get(e["level"], "없음"), e["arena"], e["zones"], ms, e["bfs"]))
    print("Mon 쓰임", len(mon_used))


def cmd_boss():
    ch = chars_by_id()
    for src, bf in battle_fields():
        f, acts = battle_actors(bf)
        foes = [ch[c]["name"] for c, s, _ in acts if s == 0 and c in ch]
        party = [ch[c]["name"] for c, s, _ in acts if s != 0 and c in ch]
        print("%04d(%s) → 전투필드 %04d wrd %d 음악 %d  적 %s  아군 %s" % (src, field_name(src), bf, f.h[0], f.h[1], foes, party))


def cmd_chest():
    import items
    names = {i["id"]: i["name"] for i in items.load_items()[0]}
    for c in chests():
        print("%04d %-10s C%-3d %s 아이템 %3d %-14s w1 %4d 열쇠 %s 자리 %s" % (
            c["field"], field_name(c["field"]), c["id"], "잠김" if c["kind"] == 7 else "보통", c["item"],
            names.get(c["item"], "?"), c["w1"], names.get(c["key"], "-"), [round(v) for v in c["pos"]]))


def cmd_graph(name):
    g = gotos()
    nodes = [int(f[:4]) for f in sorted(os.listdir(story.path("Field"))) if field_name(int(f[:4])) == name]
    for n in nodes:
        e = encounter(n)
        print("%04d wrd %d 단계 %d → %s" % (n, e["wrd"], e["level"],
              ["%04d%s" % (m, "" if m in nodes else "(%s)" % field_name(m)) for m in sorted(g.get(n, ()))]))


def cmd_shot(out, cell=200):
    import numpy as np
    from PIL import Image, ImageDraw
    import render3d
    seen, cards = set(), []
    for c in battle.load_chars():
        if c["kind"] not in (0, 5) or c["amd"] in seen or c["amd"] < 0:
            continue
        seen.add(c["amd"])
        try:
            _, slots = render3d.load_amd(c["amd"])
            m = render3d.load_3do(slots[0][1])
            if m.kind != 0:
                continue
            s = next((s for s in m.seqs if s["a"] == 0 and s["keys"]), None) or next(s for s in m.seqs if s["keys"])
            ani, fr = s["keys"][0]
            im = render3d.render(m, None, render3d.load_ani(ani), fr, size=cell, yaw=np.pi)
            cards.append((c["name"], slots[0][1], im))
        except Exception as e:      # noqa: BLE001 — 그림 하나 실패는 건너뛴다
            print("건너뜀", c["name"], e)
    cols = 8
    rows = (len(cards) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell, rows * (cell + 18)), (30, 32, 38))
    dr = ImageDraw.Draw(sheet)
    try:
        from PIL import ImageFont
        font = ImageFont.truetype("malgun.ttf", 13)
    except Exception:
        font = None
    for i, (name, num, im) in enumerate(cards):
        x, y = (i % cols) * cell, (i // cols) * (cell + 18)
        sheet.paste(im, (x, y))
        dr.text((x + 4, y + cell + 1), "%s (3do %d)" % (name, num), fill=(230, 220, 190), font=font)
    sheet.save(out)
    print(len(cards), "장")


def cmd_top(n, out):
    f = stage.Fld(n)
    marks = []
    for c in chests():
        if c["field"] == n:
            marks.append(("pt", [c["pos"][:2]], (255, 215, 0) if c["kind"] == 6 else (255, 120, 40), "chest%d" % c["item"]))
    for _bid, cid, p, _q, _ in f.b:
        marks.append(("pt", [p[:2]], (255, 60, 60), "B%d" % cid))
    for oid, model, kind, p, _q, _t in f.c:
        if kind == 4:
            marks.append(("pt", [p[:2]], (80, 220, 255), "obj4"))
    for p, x, box in f.e:
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))], (255, 120, 220), "E%d" % x))
    for x0, p, box, *ints in f.d:
        xs = [b[0] for b in box]
        ys = [b[1] for b in box]
        marks.append(("box", [(min(xs), min(ys)), (max(xs), min(ys)), (max(xs), max(ys)), (min(xs), max(ys))], (120, 255, 120), "D%d%s" % (x0, ints)))
    print(stage.render_top(f.h[0], out, size=1100, marks=marks))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    c = sys.argv[1] if len(sys.argv) > 1 else "list"
    {"list": cmd_list, "mon": cmd_mon, "pat": cmd_pat, "enc": cmd_enc, "boss": cmd_boss, "chest": cmd_chest}.get(c, lambda: None)()
    if c == "graph":
        cmd_graph(sys.argv[2])
    elif c == "shot":
        cmd_shot(sys.argv[2])
    elif c == "top":
        cmd_top(int(sys.argv[2]), sys.argv[3])
