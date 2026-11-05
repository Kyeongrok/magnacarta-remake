"""마그나카르타 전투 자료 읽개 — CharData/Mon/Att/Abi/Sgi/ValueData/Pat/AmdData.

게임 폴더의 파일을 읽기만 한다(환경 변수 MC_DIR). 배치는 MCAlpha.dll 의 MCData.cpp 읽개
(fread 차례)를 그대로 옮긴 것이고, 공식은 MCBChr.cpp 의 함수를 옮긴 것이다.
분석 글: Obsidian mv 볼트 Project/Magnacarta/분석/분석-전투.md

    python tools/mc/battle.py            # 칼린츠·첫 전투 표
    python tools/mc/battle.py chars      # 캐릭터 전부
    python tools/mc/battle.py abi        # 어빌리티 전부
"""
import os
import struct
import sys

GAME_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")

# 카르타 속성 — 1부터 센다(Sgi nSoksung, CharData 부위 속성). 0 = 없음
ELEMENTS = ["-", "地", "火", "風", "暗", "水", "電", "光"]


def _read(rel):
    with open(os.path.join(GAME_DIR, rel), "rb") as f:
        return f.read()


def _name(b):
    return b.split(b"\0")[0].decode("cp949", "replace")


# ── CharData\0000.cha ─────────────────────────────────────────────────────────
# 머리: u32 개수, u32 판(=0x3a). 레코드 267바이트(꾸러미, 맞춤 없음).
# 키 = 메모리 구조체(0x12c) 오프셋. 전투 캐릭터(MCBChr)는 이 구조체를 +0x1b0 에 품는다.
CHA_LAYOUT = (
    [(0x04, "I", "id"), (0x08, "100s", "name"), (0x6d, "B", "kind"), (0x6e, "h", "u6e")]
    + [(0x70 + 4 * i, "i", n) for i, n in enumerate(
        ["tp", "wtp", "hp", "apocalypse", "str", "def", "int", "dex", "u90", "u94", "u98"])]
    + [(0x9c + 2 * i, "h", n) for i, n in enumerate(
        ["amd", "u9e", "ua0", "ua2", "ua4", "ua6", "ua8", "uaa", "uac"])]
    + [(0xae, "6s", "uae")]
    + [(0xb4 + 2 * i, "h", n) for i, n in enumerate(
        ["ub4", "ub6", "ub8", "uba", "ubc", "singi",
         "head", "breast", "abdomen", "arm", "leg"])]
    + [(0xca, "7h", "carta"), (0xe8, "10i", "abilities")]
    + [(0x114 + 4 * i, "i", "u%x" % (0x114 + 4 * i)) for i in range(4)]
)


def load_chars():
    d = _read(r"CharData\0000.cha")
    n, _ver = struct.unpack_from("<II", d, 0)
    p, out = 8, []
    for _ in range(n):
        r = {}
        for _off, fmt, key in CHA_LAYOUT:
            v = struct.unpack_from("<" + fmt, d, p)
            p += struct.calcsize("<" + fmt)
            r[key] = v if len(v) > 1 else v[0]
        r["name"] = _name(r["name"])
        r["abilities"] = [a for a in r["abilities"] if a >= 0]
        out.append(r)
    assert p == len(d), (p, len(d))
    return out


PARTY_IDS = set(range(7)) | {20}   # MCData 0x520b2837: 이 id 만 부위 속성으로 친화도를 세운다


def init_affinity(c):
    """MCData 0x520b2866 — 읽은 뒤 런타임 친화도(+0xd8) 세우기."""
    if c["id"] in PARTY_IDS:
        parts = [c["head"], c["breast"], c["abdomen"], c["arm"], c["leg"]]
        return [int(c["carta"][i] * 0.33) if (i + 1) in parts else 0 for i in range(7)]
    return [int(v / 2) for v in c["carta"]]   # cdq/sar — 0 쪽으로 버림


def derived(c, aff=None):
    """MCBChr 의 파생 능력치 (함수 주소는 분석 노트에)."""
    a = aff or init_affinity(c)
    el = lambda slot: a[slot - 1]
    max_hp = (c["hp"] + el(c["breast"])) * 5                 # 0x5208e244
    p_atk = (c["str"] + el(c["arm"])) * 3                    # 0x5208e2a5 nStr
    m_atk = (c["str"] + el(c["arm"])) + (c["int"] + el(c["head"])) * 2  # 0x5208e309
    ac = (c["def"] + el(c["abdomen"])) * 2                   # 0x5208e3a1 nAC
    # 0x5208e404 nInt — 피해 계산에서는 안 쓴다
    n_int = int(c["int"] * el(c["head"]) / (int((el(c["abdomen"]) - int(el(c["head"]) / 100)) / 10) + 10))
    dex = c["dex"] + el(c["leg"])                            # 0x5208e4ad nDex
    return dict(max_hp=max_hp, p_atk=p_atk, m_atk=m_atk, ac=ac, int=n_int, dex=dex, aff=a)


# ── Att\0000.att  (m_pTarget, 0x50바이트 = i32 ×20) ───────────────────────────
ATT_FIELDS = ["id", "u04", "u08", "kind", "u10", "range", "rate", "u1c", "affect", "use_tp",
              "u28", "u2c", "u30", "u34", "u38", "u3c", "u40", "u44", "u48", "u4c"]


def load_att():
    d = _read(r"Att\0000.att")
    n, _ver = struct.unpack_from("<II", d, 0)
    return {r[0]: dict(zip(ATT_FIELDS, r))
            for r in (struct.unpack_from("<20i", d, 8 + i * 80) for i in range(n))}


# ── Abi\0000.abi  (344바이트) ────────────────────────────────────────────────
def load_abi():
    d = _read(r"Abi\0000.abi")
    n, _ver = struct.unpack_from("<II", d, 0)
    out = {}
    for i in range(n):
        r = d[8 + i * 344: 8 + (i + 1) * 344]
        aid = struct.unpack_from("<i", r, 0)[0]
        tgt, l1, l2, l3, apo = struct.unpack_from("<5i", r, 68)
        out[aid] = dict(id=aid, name=_name(r[4:68]), target=tgt,
                        links=[x for x in (l1, l2, l3) if x >= 0], apocalypse=apo,
                        desc=_name(r[88:344]))
    return out


# ── Sgi\0000.sgi  신기(카르타 무기) 164바이트 ─────────────────────────────────
SGI_FIELDS = ["model", "u70", "owner", "child1", "child2", "ability", "u84", "u88",
              "hp", "str", "def", "int", "dex", "element", "affinity"]


def load_sgi():
    d = _read(r"Sgi\0000.sgi")
    n, _ver = struct.unpack_from("<II", d, 0)
    out = {}
    for i in range(n):
        r = d[8 + i * 164: 8 + (i + 1) * 164]
        sid = struct.unpack_from("<i", r, 0)[0]
        out[sid] = dict(id=sid, name=_name(r[4:104]), **dict(zip(SGI_FIELDS, struct.unpack_from("<15i", r, 104))))
    return out


# ── Mon\NNNN.mon  조우 편성: u32 8, u32 1, u32 ?, i32 ×8 (CharData id, -1 = 빈칸) ─
def load_mon(num):
    d = _read(r"Mon\%04d.mon" % num)
    v = struct.unpack("<11i", d)
    return [x for x in v[3:] if x >= 0]


# ── ValueData\0001.val  (id, 값) 짝 — g_pDM->GetValue(id) 0x520b5b0e ──────────
def load_values():
    d = _read(r"ValueData\0001.val")
    n, _ver = struct.unpack_from("<II", d, 0)
    return dict(struct.unpack_from("<2i", d, 8 + 8 * i) for i in range(n))


def damage(atk_power, att, target_ac):
    """BChr::CalcDamage 0x5208e810 — (갈래, 값). 갈래 0 = 피해, 1 = 회복."""
    k = att["kind"]
    if k == 3:                       # 회복: 공격력 대신 rate 그대로
        return 1, att["rate"]
    if k == 0:
        v = atk_power - target_ac
    else:                            # 1, 2 (기술·마법)
        v = atk_power * att["rate"] // 100 - target_ac
    if v <= 0:
        v = 10                       # GetValue(38) = 10 (최소 피해)
    return 0, v                      # 치명타 판정 0x5208ea3c 는 늘 0 → ×4 길은 죽어 있다


def main(argv):
    chars, att, abi, sgi = load_chars(), load_att(), load_abi(), load_sgi()
    mode = argv[1] if len(argv) > 1 else "summary"
    if mode == "chars":
        for c in chars:
            print(c["id"], c["name"], c["kind"], "WTP", c["wtp"], "HP", c["hp"], c["str"], c["def"], c["int"], c["dex"],
                  "carta", c["carta"], "abi", c["abilities"])
        return
    if mode == "abi":
        for a in abi.values():
            t = att.get(a["target"], {})
            print(a["id"], a["name"], "att", a["target"], "kind", t.get("kind"), "rate", t.get("rate"),
                  "tp", t.get("use_tp"), "range", t.get("range"), "links", a["links"], "apo", a["apocalypse"])
        return
    vals = load_values()
    kal = chars[0]
    dk = derived(kal)
    print("== 칼린츠", {k: kal[k] for k in ("wtp", "tp", "hp", "str", "def", "int", "dex", "singi")})
    print("  부위 속성: 머리 %s 가슴 %s 배 %s 팔 %s 다리 %s" % tuple(
        ELEMENTS[kal[k]] for k in ("head", "breast", "abdomen", "arm", "leg")))
    print("  카르타 기본", dict(zip(ELEMENTS[1:], kal["carta"])), "→ 런타임", dict(zip(ELEMENTS[1:], dk["aff"])))
    print("  파생", {k: v for k, v in dk.items() if k != "aff"})
    print("  신기", sgi[kal["singi"]]["name"])
    for a in kal["abilities"]:
        ab = abi[a]
        print("  기술", a, ab["name"], "→ 연결", [abi[x]["name"] for x in ab["links"]],
              "필살", abi[ab["apocalypse"]]["name"] if ab["apocalypse"] in abi else "-")
    print("== Mon 0000", [chars[m]["name"] for m in load_mon(0)])
    for m in sorted(set(load_mon(0))):
        c = chars[m]
        d = derived(c)
        print("  ", c["name"], "WTP", c["wtp"], {k: v for k, v in d.items() if k != "aff"},
              "기술", [abi[x]["name"] for x in c["abilities"] if x in abi])
        _, dmg = damage(dk["p_atk"], att[abi[13]["target"]], d["ac"])
        print("     칼린츠 일반공격 1타 →", dmg)
        _, back = damage(d["p_atk"], att[abi[c["abilities"][0]]["target"]], dk["ac"]) if c["abilities"] else (0, 0)
        print("     %s 첫 기술 1타 → 칼린츠 %d" % (c["name"], back))
    print("== Value", {k: v for k, v in vals.items() if v != -1})


if __name__ == "__main__":
    main(sys.argv)
