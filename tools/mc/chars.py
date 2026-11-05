"""마그나카르타 캐릭터 정리 — CharData · AmdData · 3DOBS · Sgi · Abi · Field(B 인물) · 사건 804/805.

원본 폴더(환경 변수 MC_DIR)를 읽기만 한다. 다른 읽개(battle·render3d·stage·story)를 import 해 쓴다.
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-캐릭터.md

    python tools/mc/chars.py table              # 주요 인물 표 (능력치·속성·신기·기술·모델)
    python tools/mc/chars.py all                # CharData 전부 (id · 이름 · 종류 · amd)
    python tools/mc/chars.py where              # 인물마다 나오는 필드 · 파티 넣기/빼기 필드 · 이야기 차례
    python tools/mc/chars.py shot out.png       # 주요 인물 정면 그림 모음 (서기 시퀀스 100 첫 틀)
    python tools/mc/chars.py faces out.png      # 주요 인물 대화 얼굴(BGR) 모음
"""
import collections
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import battle      # noqa: E402
import render3d    # noqa: E402
import stage       # noqa: E402
import story       # noqa: E402

EL = battle.ELEMENTS
KIND = {0: "몬스터", 1: "인물", 5: "보스"}       # CharData +0x6d (전투 쪽 뜻은 분석-전투)

# 파티원(능력치가 있는 아군) + 주요 인물. 차례 = CharData id
MAIN = [0, 3, 1, 2, 4, 5, 6, 20, 21, 18, 19, 16, 17, 22, 23, 24, 57, 25, 31, 32, 33, 34, 41, 49, 50, 55, 56]


def chars_by_id():
    return {c["id"]: c for c in battle.load_chars()}


def models(c):
    """amd 칸 → (3do 번호, 모델) 목록. 3do 0 = 빈 칸."""
    _, slots = render3d.load_amd(c["amd"])
    out = []
    for slot, num in slots:
        if num == 0:
            continue
        try:
            out.append((slot, num, render3d.load_3do(num)))
        except Exception:
            out.append((slot, num, None))
    return out


def textures(m):
    return sorted({g[0].tex for g in m.parts[0][5]}) if m and m.kind == 0 else []


def seq(m, sid):
    for s in m.seqs:
        if s["a"] == sid:
            return s
    return None


# ── 이야기 차례: 651(새 게임) 에서 GOTO(4·5) 를 따라간 거리 ───────────────────

def field_graph():
    gotos, adds = collections.defaultdict(set), collections.defaultdict(list)
    for n, evs in story.all_events():
        for _trig, _conds, acts in evs or []:
            for a in acts:
                if a[0] in (4, 5):
                    gotos[n].add(a[1])
                elif a[0] in (804, 805):
                    adds[a[1]].append((n, "넣기" if a[0] == 804 else "빼기"))
    return gotos, adds


def story_depth(gotos, start=651):
    depth, q = {start: 0}, collections.deque([start])
    while q:
        n = q.popleft()
        for m in sorted(gotos.get(n, ())):
            if m not in depth:
                depth[m] = depth[n] + 1
                q.append(m)
    return depth


def placements():
    """fld B 묶음: CharData id → 그 인물을 세우는 필드 목록. B 의 첫 값은 차례가 아니라 id 다."""
    out = collections.defaultdict(list)
    for fn in sorted(os.listdir(stage.game_path("Field"))):
        n = int(fn[:4])
        try:
            f = stage.Fld(n)
        except Exception:
            continue
        for _bid, cid, _p, _q, _ in f.b:
            out[cid].append(n)
    return out


# ── 명령 ─────────────────────────────────────────────────────────────────────

def cmd_table():
    cs, abi, sgi = chars_by_id(), battle.load_abi(), battle.load_sgi()
    for cid in MAIN:
        c = cs[cid]
        parts = "/".join(EL[c[k]] for k in ("head", "breast", "abdomen", "arm", "leg"))
        d = battle.derived(c)
        w = sgi.get(c["singi"])
        print("%3d %-10s %s WTP %d  체%d 공%d 방%d 지%d 민%d  HP%d 물공%d 방어%d  부위 %s" % (
            cid, c["name"], KIND.get(c["kind"], c["kind"]), c["wtp"], c["hp"], c["str"], c["def"], c["int"], c["dex"],
            d["max_hp"], d["p_atk"], d["ac"], parts))
        print("      카르타", dict(zip(EL[1:], c["carta"])), "u9e", c["u9e"], "ua0", c["ua0"])
        if w:
            print("      신기 %d %s (3do %d, %s)" % (w["id"], w["name"], w["model"], EL[w["element"]] if 0 <= w["element"] < 8 else w["element"]))
        print("      기술", [(a, abi[a]["name"]) for a in c["abilities"] if a in abi])
        for slot, num, m in models(c):
            if m is None:
                print("      칸 %d → 3do %d (못 읽음)" % (slot, num))
                continue
            have = [s for s in (100, 101, 102) if seq(m, s)]
            print("      칸 %2d → 3do %4d 뼈 %d 정점 %d 시퀀스 %d %s 텍스처 %s" % (
                slot, num, len(m.bone_pivots), len(m.parts[0][0]) if m.kind == 0 else -1, len(m.seqs), have, textures(m)))


def cmd_all():
    for i, c in enumerate(battle.load_chars()):
        print("%3d id %3d %-14s %-4s amd %3d 신기 %3d 능력 %s" % (
            i, c["id"], c["name"], KIND.get(c["kind"], c["kind"]), c["amd"], c["singi"], c["abilities"]))


def cmd_where():
    cs = chars_by_id()
    gotos, adds = field_graph()
    depth = story_depth(gotos)
    place = placements()
    for cid in MAIN:
        fs = sorted(set(place.get(cid, [])), key=lambda n: (depth.get(n, 9999), n))
        reach = [(n, depth[n]) for n in fs if n in depth]
        print("%3d %-10s 필드 %3d곳  651에서 가장 가까운 등장 %s" % (cid, cs[cid]["name"], len(fs), reach[:4]))
        if cid in adds:
            print("      파티", sorted(adds[cid], key=lambda x: (depth.get(x[0], 9999), x[0])),
                  "거리", {n: depth.get(n) for n, _ in adds[cid]})


def cmd_shot(out, ids=None, cell=240):
    from PIL import Image, ImageDraw, ImageFont
    cs = chars_by_id()
    ids = ids or MAIN
    cols = 7
    rows = (len(ids) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell, rows * (cell + 22)), (40, 44, 52))
    dr = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("malgun.ttf", 15)
    except OSError:
        font = None
    for k, cid in enumerate(ids):
        c = cs[cid]
        ms = models(c)
        if not ms or ms[0][2] is None:
            continue
        slot, num, m = ms[0]
        s = seq(m, 100) or m.seqs[0]
        ani_n, fr = s["keys"][0]
        im = render3d.render(m, None, render3d.load_ani(ani_n), fr, size=cell, yaw=np.pi)
        x, y = (k % cols) * cell, (k // cols) * (cell + 22)
        sheet.paste(im, (x, y))
        dr.text((x + 4, y + cell + 2), "%d %s · 3do %d" % (cid, c["name"], num), fill=(230, 230, 230), font=font)
    sheet.save(out)


def cmd_faces(out, cell=160):
    """CharData +0x9e = 대화창 얼굴 BGR 번호 (-1 = 없음). 그림 안에 영문 이름이 찍혀 있다."""
    import render2d
    from PIL import Image, ImageDraw, ImageFont
    cs = chars_by_id()
    ids = [cid for cid in MAIN if cs[cid]["u9e"] >= 0]
    cols = 7
    sheet = Image.new("RGB", (cols * cell, ((len(ids) + cols - 1) // cols) * (cell + 20)), (0, 0, 0))
    dr = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("malgun.ttf", 13)
    except OSError:
        font = None
    for k, cid in enumerate(ids):
        im = render2d.read_bgr(cs[cid]["u9e"])["image"].convert("RGB").resize((cell, cell))
        x, y = (k % cols) * cell, (k // cols) * (cell + 20)
        sheet.paste(im, (x, y))
        dr.text((x + 3, y + cell + 1), "%d %s · BGR %d" % (cid, cs[cid]["name"], cs[cid]["u9e"]), fill=(230, 230, 230), font=font)
    sheet.save(out)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    c = sys.argv[1] if len(sys.argv) > 1 else "table"
    if c == "table":
        cmd_table()
    elif c == "all":
        cmd_all()
    elif c == "where":
        cmd_where()
    elif c == "faces":
        cmd_faces(sys.argv[2])
    elif c == "shot":
        cmd_shot(sys.argv[2], [int(x) for x in sys.argv[3:]] or None)
