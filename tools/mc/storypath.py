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

def arith(cur, op, v):
    """명령 101/103 [스위치, 셈, 값] — 0 더하기 · 1 빼기 · 2 곱하기 · 3 나누기(패치판 FieldScene.Events 와 같음)."""
    return {0: cur + v, 1: cur - v, 2: cur * v, 3: cur // v if v else cur}.get(op, cur)


CMP = {0: lambda a, b: a == b, 1: lambda a, b: a < b, 2: lambda a, b: a <= b,
       3: lambda a, b: a > b, 4: lambda a, b: a >= b, 5: lambda a, b: a != b}


_FIELDS = None
_EXITS = {}


def fields():
    global _FIELDS
    if _FIELDS is None:
        _FIELDS = {n: evs for n, evs in story.all_events() if evs}
    return _FIELDS


def ok(conds, loc, glo):
    for c in conds:
        if c[0] == 0:
            continue
        if c[0] == 302:          # 전투 결과 — 이긴 것으로(a0 1 = 이김, 0 = 짐)
            if c[1] != 1:
                return False
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
                # 조건 없는 사건은 사건 0 의 CALL(명령 0)로 불리는 것 — 같이 돌린다(162 사건 1 이 전역 20 = 2 · 804)
                if ei in done or not ok(conds, loc, glo):
                    continue
                done.add(ei)
                progress = True
                for a in acts:
                    op = a[0]
                    if op == 100: loc[a[1]] = a[2]
                    elif op == 101: loc[a[1]] = arith(loc.get(a[1], 0), a[2], a[3])
                    elif op == 102: glo[a[1]] = a[2]
                    elif op == 103: glo[a[1]] = arith(glo.get(a[1], 0), a[2], a[3])
                    elif op == 9:
                        note = "사건 전투 %d(이긴 것으로)" % a[1]
                        goto = a[1]
                        break
                    elif op in (4, 5):
                        goto = a[1]
                        break
                if goto is not None:
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
        path.append((n, ("%s → %d" % (note, goto)) if note else "→ %d" % goto))
        n = goto
    return path, glo


CMPN = {"==": 0, "<": 1, "<=": 2, ">": 3, ">=": 4, "!=": 5}


def entry_locals(n, glo):
    """들 때 도는 사건(구역 조건 없는 것)으로 지역 스위치를 셈 — GOTO·전투는 무시."""
    loc, done = {}, set()
    evs = fields().get(n, [])
    changed = True
    while changed:
        changed = False
        for ei, (trig, conds, acts) in enumerate(evs):
            if ei in done or any(c[0] == 304 for c in conds) or not ok(conds, loc, glo):
                continue
            done.add(ei)
            changed = True
            g2 = dict(glo)
            for a in acts:
                if a[0] == 100: loc[a[1]] = a[2]
                elif a[0] == 101: loc[a[1]] = arith(loc.get(a[1], 0), a[2], a[3])
    return loc


def open_exits(n, glo):
    """구역 사건의 GOTO 중 지역(들 때 셈)·전역 조건이 맞는 것 — 540 → 293 은 지역 100 == 2."""
    loc = entry_locals(n, glo)
    out = []
    for trig, conds, acts in fields().get(n, []):
        if not any(c[0] == 304 for c in conds) or not ok([c for c in conds if c[0] != 304], loc, glo):
            continue
        out += [a[1] for a in acts if a[0] in (4, 5)]
    return out


def open_exits_old(n, glo):
    import towns
    if n not in _EXITS:
        try:
            _EXITS[n] = towns.exits(n, fields().get(n, []))
        except Exception:
            _EXITS[n] = []
    ex = _EXITS[n]
    return [e["to"] for e in ex if all(ok([(101, s, CMPN[op], v)], {}, glo) for s, op, v in e["switch"])]


def story_here(n, glo, evs_by):
    """이 필드에 들면 저절로 무언가(GOTO·전투·스위치 바꿈) 하는가."""
    p, g = run(n, 2, [], glo)
    return p and (not p[0][1].startswith("걸어야") or g != glo)


def region_battle(n, glo, evs_by, done_battles):
    """구역(304)에 들면 여는 사건 전투(명령 9) — 725 구역 1 → 0789 처럼."""
    for trig, conds, acts in evs_by.get(n, []):
        if not any(c[0] == 304 for c in conds) or not all(ok([c], {}, glo) for c in conds if c[0] == 101):
            continue
        for a in acts:
            if a[0] == 9 and a[1] not in done_battles:
                LAST_REGION[0] = next(c[2] for c in conds if c[0] == 304)
                return a[1]
    return None


LAST_REGION = [None]


def region_effects(n, glo, evs_by):
    """구역(304)에 들면 도는 사건 중 GOTO 없이 전역 스위치를 바꾸는 것 — 그 구역에 걸어 들어가는 일로 본다."""
    out = []
    for trig, conds, acts in evs_by.get(n, []):
        if not any(c[0] == 304 for c in conds) or any(a[0] in (4, 5) for a in acts):
            continue
        if not all(ok([c], {}, glo) for c in conds if c[0] == 101):
            continue
        new = dict(glo)
        for a in acts:
            if a[0] == 102: new[a[1]] = a[2]
            elif a[0] == 103: new[a[1]] = arith(new.get(a[1], 0), a[2], a[3])
        if new != glo:
            out.append(new)
            LAST_REGION[0] = next(c[2] for c in conds if c[0] == 304)
    return out


def auto(start=651, glo=None, max_steps=400):
    """걸어야 하는 곳에서 지금 스위치로 닿는 필드 중 이야기 사건이 있는 가장 가까운 곳으로 걸어간다(넓이 우선)."""
    from collections import deque
    evs_by = fields()
    glo = dict(glo or {})
    n = start
    log = []
    visited = set()
    done = set()   # 이야기가 한 번 돈 필드 — 원본 지역 스위치가 필드마다 남는다면 다시 안 돈다(못 푼 것), 목표에서 뺀다
    for _ in range(max_steps):
        p, glo2 = run(n, 60, [], glo)
        done.update(x for x, w in p if not w.startswith("걸어"))
        log.extend(p)
        glo = glo2
        last, what = p[-1]
        if "사건 없음" in what:
            log.append((last, "멈춤 — " + what))
            break
        key = (last, tuple(sorted(glo.items())))
        if key in visited:
            log.append((last, "같은 상태로 돌아옴 — 멈춤"))
            break
        visited.add(key)
        # 넓이 우선으로 이야기 필드 찾기
        prev = {last: None}
        q = deque([last])
        goal = None
        while q and goal is None:
            m = q.popleft()
            for t in open_exits(m, glo):
                if t in prev:
                    continue
                prev[t] = m
                if t not in done and story_here(t, glo, evs_by):
                    goal = t
                    break
                if region_effects(t, glo, evs_by) or region_battle(t, glo, evs_by, done) is not None:
                    goal = t
                    break
                q.append(t)
        if goal is None:
            log.append((last, "닿는 곳에 이야기 사건 없음 — 멈춤"))
            break
        path = []
        x = goal
        while x is not None:
            path.append(x)
            x = prev[x]
        path.reverse()
        log.append((last, "걸어서 " + " → ".join(map(str, path))))
        n = goal
        rb = region_battle(goal, glo, evs_by, done) if goal in done or not story_here(goal, glo, evs_by) else None
        if rb is not None:
            done.add(rb)
            region_battle(goal, glo, evs_by, done - {rb})
            log.append((goal, "구역 사건 전투 %d(이긴 것으로) @%s" % (rb, LAST_REGION[0])))
            n = rb
            continue
        eff = region_effects(goal, glo, evs_by) if goal in done or not story_here(goal, glo, evs_by) else []
        if eff:
            changed = {k: v for k, v in eff[0].items() if glo.get(k) != v}
            region_effects(goal, glo, evs_by)
            glo = eff[0]
            log.append((goal, "구역 사건 — 전역 %s @%s" % (changed, LAST_REGION[0])))
    return log, glo


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "auto":   # python tools/mc/storypath.py auto — 새 게임부터 끝까지 자동
        lg, g = auto(651, max_steps=800)
        order = []
        for n, w in lg:
            if not order or order[-1] != n:
                order.append(n)
        battles = [w.split("사건 전투 ")[1].split("(")[0] for n, w in lg if "사건 전투" in w]
        print("필드 차례(%d): %s" % (len(order), " ".join(map(str, order))))
        print("사건 전투(%d): %s" % (len(battles), " ".join(battles)))
        print("끝:", lg[-1])
        sys.exit(0)
    if len(sys.argv) > 2 and sys.argv[1] == "route":   # python tools/mc/storypath.py route 657 [끝 필드] — 패치판 MC_TEST_ROUTE
        start = int(sys.argv[2])
        stop = int(sys.argv[3]) if len(sys.argv) > 3 else None
        lg, g = auto(start, max_steps=800)
        pairs, seen_fields = [], []
        for n, w in lg:
            seen_fields.append(n)
            if w.startswith("걸어서 "):
                hops = [int(x) for x in w[4:].split(" → ")]
                pairs += ["%d>%d" % (a, b) for a, b in zip(hops, hops[1:])]
            if w.startswith("구역 사건") and " @" in w:
                pairs.append("%d@%s" % (n, w.rsplit(" @", 1)[1]))
            if stop is not None and n == stop:
                break
        print(",".join(pairs))
        sys.exit(0)
    s = int(sys.argv[1]) if len(sys.argv) > 1 else 651
    lim = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    w = [int(x) for x in sys.argv[3].split(",")] if len(sys.argv) > 3 else []
    p, g = run(s, lim, w)
    for n, what in p:
        print("%4d %s" % (n, what))
    print("전역 스위치:", dict(sorted(g.items())))
