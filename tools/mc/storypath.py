"""새 게임부터 이야기 사건이 저절로 어디까지 이어지는지 자료만으로 따라가기.

    python tools/mc/storypath.py [시작 필드=651] [최대 필드 수=40] [걸어갈 필드들 593,592,…]

필드에 들면 지역 스위치를 0 으로 두고, 조건이 0(늘)·100(지역)·101(전역) 뿐인 사건을 차례로 돌린다
(명령 100/102 = 대입, 101/103 = 더하기로 셈, 4/5 = GOTO). 구역(304)·전투(9 → 302) 사건은 사람이
걸어야 하는 자리로 보고 멈추며, 그 필드의 출구(구역 사건의 GOTO)를 함께 적는다. 원본 조건 판정
(0x52071af6)의 비교: 0 ==, 1 <, 2 <=, 3 >, 4 >=, 5 !=. 사건 0 조건(0 갈래)은 「다른 사건이 없을 때」지만
여기서는 늘 참으로 본다 — 대략의 길잡이.
"""
import sys

import story

CMP = {0: lambda a, b: a == b, 1: lambda a, b: a < b, 2: lambda a, b: a <= b,
       3: lambda a, b: a > b, 4: lambda a, b: a >= b, 5: lambda a, b: a != b}


def fields():
    return {n: evs for n, evs in story.all_events() if evs}


def ok(conds, loc, glo):
    for c in conds:
        if c[0] == 0:
            continue
        if c[0] in (100, 101):
            sw = loc if c[0] == 100 else glo
            if not CMP.get(c[2], lambda a, b: False)(sw.get(c[1], 0), c[3]):
                return False
        else:
            return False
    return True


def run(start=651, limit=40, walk=(), glo=None):
    evs_by = fields()
    glo = dict(glo or {})
    walk = list(walk)
    n = start
    path = []
    for _ in range(limit):
        evs = evs_by.get(n)
        if evs is None:
            path.append((n, "사건 없음"))
            break
        loc, done, goto, note = {}, set(), None, ""
        progress = True
        while progress and goto is None:
            progress = False
            for ei, (trig, conds, acts) in enumerate(evs):
                if ei in done or not conds and ei != 0 or not ok(conds, loc, glo):
                    continue
                done.add(ei)
                progress = True
                for a in acts:
                    op = a[0]
                    if op == 100: loc[a[1]] = a[2]
                    elif op == 101: loc[a[1]] = loc.get(a[1], 0) + a[2]
                    elif op == 102: glo[a[1]] = a[2]
                    elif op == 103: glo[a[1]] = glo.get(a[1], 0) + a[2]
                    elif op == 9: note = "사건 전투 %d" % a[1]
                    elif op in (4, 5):
                        goto = a[1]
                        break
                if goto is not None or note:
                    break
        if goto is None:
            exits = []
            for trig, conds, acts in evs:
                if any(c[0] == 304 for c in conds):
                    sw_ok = all(ok([c], {}, glo) for c in conds if c[0] == 101)
                    for a in acts:
                        if a[0] in (4, 5) and sw_ok:
                            exits.append(a[1])
            if walk and walk[0] in exits and not note:
                nxt = walk.pop(0)
                path.append((n, "걸어서 → %d (열린 출구 %s)" % (nxt, exits)))
                n = nxt
                continue
            path.append((n, (note + " · " if note else "") + "걸어야 함 — 열린 출구 %s" % exits))
            break
        path.append((n, "→ %d" % goto))
        n = goto
    return path, glo


if __name__ == "__main__":
    s = int(sys.argv[1]) if len(sys.argv) > 1 else 651
    lim = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    w = [int(x) for x in sys.argv[3].split(",")] if len(sys.argv) > 3 else []
    p, g = run(s, lim, w)
    for n, what in p:
        print("%4d %s" % (n, what))
    print("전역 스위치:", dict(sorted(g.items())))
