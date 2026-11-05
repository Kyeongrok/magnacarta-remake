"""사건 명령 200번대(캐릭터) 처리 함수 찾기 · 206 풀이 · fld G 묶음 훑기.

원본 폴더의 파일을 읽기만 한다(환경 변수 MC_DIR). 분석 글: 옵시디안 Project/Magnacarta/분석/분석-경로이동.md

    python tools/mc/paths.py ops        # 200~226 → 처리 함수 (갈래 표 0x52071b9b)
    python tools/mc/paths.py uses 206   # 모든 fld 에서 그 명령의 인자 모음
    python tools/mc/paths.py g          # fld G 묶음 전부 (게임 전체에 하나뿐)
"""
import struct
import sys
from collections import Counter

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
import stage   # noqa: E402
import story   # noqa: E402
import towns   # noqa: E402

DISPATCH = 0x52071bc2       # switch(op) 첫 cmp — 변수 [ebp - 0x10]


def ops(values=range(200, 227)):
    import capstone
    p = story.Pe()
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    res = story.switch_cases(p, DISPATCH, "[ebp - 0x10]", values=list(values))
    out = {}
    for pc, vals in res.items():
        for i in md.disasm(p.img[pc - p.base: pc - p.base + 80], pc):
            if i.mnemonic == "call":
                for v in vals:
                    out[v] = i.op_str
                break
            if i.mnemonic in ("jmp", "ret"):
                break
    return out


def uses(op):
    c = Counter()
    for n in towns.fld_numbers():
        try:
            evs = towns.events(n)
        except Exception:
            continue
        for _t, _co, acts in evs or []:
            for a in acts:
                if a[0] == op:
                    args = list(a[1:6])
                    c[(args[0] >= 10000, tuple(args[1:4]))] += 1
    return c


def survey_g():
    out = []
    for n in towns.fld_numbers():
        try:
            f = stage.Fld(n)
        except Exception:
            continue
        for g in f.g:
            out.append((n, g["a"], g["b"], g["c"], g["kind"],
                        [round(x, 1) for x in struct.unpack("<8f", g["keys"][0][0])] if g["b"] and g["c"] else None))
    return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = sys.argv[1] if len(sys.argv) > 1 else "ops"
    if cmd == "ops":
        for k, v in sorted(ops().items()):
            print(k, v)
    elif cmd == "uses":
        for k, v in uses(int(sys.argv[2])).most_common(30):
            print(v, k)
    elif cmd == "g":
        for row in survey_g():
            print(row)
