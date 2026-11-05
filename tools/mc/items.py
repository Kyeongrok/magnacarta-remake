"""마그나카르타 아이템·신기(무기) 자료 읽개.

원본 폴더의 파일을 읽기만 한다(환경 변수 MC_DIR 로 바꿈). 풀어낸 것은 저장소에 넣지 않는다.

    python tools/mc/items.py items      # Itm/0000.itm 표 (마크다운)
    python tools/mc/items.py weapons    # Sgi/0000.sgi 신기 표 + 진화 나무
    python tools/mc/items.py chars      # CharData 주인공 장비·능력치 요약
    python tools/mc/items.py check      # 설명 글과 수치가 어긋난 아이템
    python tools/mc/items.py tabs OUT.png   # 아이템 창 분류 탭 그림 (BGR 0419~0422)

배치는 MCAlpha.dll 의 읽개(Itm 0x520b47e3, Sgi 0x520b4ef3, CharData 0x520b216c)에서 따왔다.
"""
import os
import struct
import sys

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")

ELEMENTS = {0: "", 1: "지", 2: "화", 3: "풍", 4: "암", 5: "수", 6: "전", 7: "광"}
# Itm +0x70 → 장비 칸 (CHChrParam +0x114/+0x118/+0x11c/+0x120). 4 이상은 장착 못 함.
SLOTS = {0: "몸(BODY)", 1: "반지(RING)", 2: "목걸이(NECK)", 3: "기타(ETC)", 4: "열쇠(장착 안 됨)"}
# Itm +0x74 (몸 장비 종류) → 입을 수 있는 캐릭터 id. 장비 함수 0x52081615 첫머리의 지역 표.
ARMOR_OWNER = {1: 0, 2: 3, 3: 2, 4: 1, 5: 4, 6: 6, 7: 5, 8: 20}
ARMOR_KIND = {1: "조끼", 2: "타이즈", 3: "문신", 4: "벨트", 5: "장신구(이빨·뿔·크로스)", 6: "셔츠", 7: "장갑", 8: "갑옷"}
# 상태 창이 보여 주는 배율 (0x52087a18): 체력×5, 공격력×3, 방어력×2, 지력×2, 민첩성×1
STAT_MUL = (("체력", 5), ("공격력", 3), ("방어력", 2), ("지력", 2), ("민첩성", 1))


def path(*p):
    return os.path.join(MC_DIR, *p)


def cstr(b):
    return b.split(b"\0")[0].decode("cp949", "replace")


# ── Itm: 머리 8바이트(개수, ?) + 기록 264바이트 ─────────────────────────────
# 파일 +0 id · +4 이름[100] · +0x68 정수 15개(메모리 +0x6c~+0xa4) · +0xa4 설명[100]
ITM_FIELDS = ("price", "slot", "armor", "hp_raw", "vit", "atk", "dfn", "int", "agi",
              "u90", "u94", "u98", "element", "affinity", "ua4")


def load_items():
    d = open(path("Itm", "0000.itm"), "rb").read()
    n, extra = struct.unpack_from("<2I", d)
    out = []
    for i in range(n):
        o = 8 + i * 264
        r = {"id": struct.unpack_from("<i", d, o)[0], "name": cstr(d[o + 4:o + 104])}
        r.update(zip(ITM_FIELDS, struct.unpack_from("<15i", d, o + 0x68)))
        r["desc"] = cstr(d[o + 0xa4:o + 0xa4 + 100])
        out.append(r)
    return out, extra


# ── Sgi(신기 神器): 머리 8바이트 + 기록 164바이트 ───────────────────────────
# 파일 +0 id · +4 이름[100] · +0x68 정수 15개(메모리 +0x6c~+0xa4)
SGI_FIELDS = ("model", "u70", "owner", "child1", "child2", "abi1", "abi2", "abi3",
              "vit", "atk", "dfn", "int", "agi", "element", "affinity")


def load_singi():
    d = open(path("Sgi", "0000.sgi"), "rb").read()
    n, extra = struct.unpack_from("<2I", d)
    out = []
    for i in range(n):
        o = 8 + i * 164
        r = {"id": struct.unpack_from("<i", d, o)[0], "name": cstr(d[o + 4:o + 104])}
        r.update(zip(SGI_FIELDS, struct.unpack_from("<15i", d, o + 0x68)))
        out.append(r)
    return out, extra


# ── Abi: 머리 8바이트 + 기록 344바이트 (id, 이름[100], …) — 이름만 쓴다 ──────
def load_abi_names():
    d = open(path("Abi", "0000.abi"), "rb").read()
    n = struct.unpack_from("<I", d)[0]
    return {struct.unpack_from("<i", d, 8 + i * 344)[0]: cstr(d[12 + i * 344:112 + i * 344]) for i in range(n)}


# ── CharData: 머리 8바이트 + 기록 267바이트 (읽개 0x520b216c 의 fread 차례) ──
CHA_LAYOUT = ([("id", "i"), ("name", "100s"), ("b6d", "B"), ("w6e", "h")]
              + [(f"i{o:x}", "i") for o in range(0x70, 0x9c, 4)]
              + [(f"w{o:x}", "h") for o in range(0x9c, 0xae, 2)] + [("r_ae", "6s")]
              + [(f"w{o:x}", "h") for o in range(0xb4, 0xca, 2)]
              + [("aff", "7h"), ("abi", "10i"), ("eq114", "i"), ("eq118", "i"), ("eq11c", "i"), ("eq120", "i")])
CHA_FMT = "<" + "".join(t for _, t in CHA_LAYOUT)
CHA_SIZE = struct.calcsize(CHA_FMT)  # 267


def load_chars():
    d = open(path("CharData", "0000.cha"), "rb").read()
    n = struct.unpack_from("<I", d)[0]
    out = []
    for i in range(n):
        vals = list(struct.unpack_from(CHA_FMT, d, 8 + i * CHA_SIZE))
        r, k = {}, 0
        for name, t in CHA_LAYOUT:
            cnt = int(t[:-1]) if t[:-1].isdigit() and t[-1] != "s" else 1
            r[name] = tuple(vals[k:k + cnt]) if cnt > 1 else vals[k]
            k += cnt
        r["name"] = cstr(r["name"])
        out.append(r)
    return out


def stat_text(r):
    parts = []
    for (label, mul), key in zip(STAT_MUL, ("vit", "atk", "dfn", "int", "agi")):
        if r[key]:
            parts.append(f"{label}+{r[key] * mul}")
    if r["element"] and r["affinity"]:
        parts.append(f"'{ELEMENTS.get(r['element'], '?')}'친화도+{r['affinity']}")
    return ", ".join(parts)


def cmd_items():
    items, _ = load_items()
    chars = {c["id"]: c["name"] for c in load_chars()}
    print("| id | 이름 | 칸 | 몸 종류 → 입는 이 | 값 | 체력 | 공격 | 방어 | 지력 | 민첩 | 속성·친화 | +0xa4 | 설명 |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for r in items:
        armor = f"{ARMOR_KIND.get(r['armor'], r['armor'])} → {chars.get(ARMOR_OWNER.get(r['armor']), '?')}" if r["armor"] else ""
        el = f"{ELEMENTS.get(r['element'])} +{r['affinity']}" if r["element"] else ""
        print(f"| {r['id']} | {r['name']} | {SLOTS.get(r['slot'], r['slot'])} | {armor} | {r['price'] or ''} | "
              f"{r['vit'] or ''} | {r['atk'] or ''} | {r['dfn'] or ''} | {r['int'] or ''} | {r['agi'] or ''} | {el} | "
              f"{r['ua4'] or ''} | {r['desc']} |")


def cmd_weapons():
    sg, _ = load_singi()
    abi = load_abi_names()
    chars = {c["id"]: c["name"] for c in load_chars()}
    byid = {r["id"]: r for r in sg}
    print("| id | 이름 | 주인 | 3DOBS 모델 | 다음1 | 다음2 | 배우는 기술 | 진화 때 오르는 값(체·공·방·지·민) | 속성 +친화 |")
    print("|---|---|---|---|---|---|---|---|---|")
    for r in sg:
        nx = lambda c: f"{c} {byid[c]['name']}" if c in byid else ("" if c < 0 else str(c))
        abis = ", ".join(f"{a} {abi.get(a, '?')}" for a in (r["abi1"], r["abi2"], r["abi3"]) if a >= 0)
        stats = "·".join(str(r[k]) for k in ("vit", "atk", "dfn", "int", "agi"))
        el = f"{ELEMENTS.get(r['element'])} +{r['affinity']}" if r["element"] else ""
        print(f"| {r['id']} | {r['name']} | {chars.get(r['owner'], r['owner'])} | {r['model'] if r['model'] >= 0 else ''} | "
              f"{nx(r['child1'])} | {nx(r['child2'])} | {abis} | {stats} | {el} |")


def cmd_chars():
    sg, _ = load_singi()
    byid = {r["id"]: r["name"] for r in sg}
    abi = load_abi_names()
    print("| id | 이름 | 체력 | 공격 | 방어 | 지력 | 민첩 | 무기 모델(+0xb6) | 신기(+0xbe) | 기술 | 친화도(지화풍암수전광) | 장비 4칸 |")
    print("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for c in load_chars()[:23]:
        abis = ", ".join(f"{a} {abi.get(a, '?')}" for a in c["abi"] if a >= 0)
        print(f"| {c['id']} | {c['name']} | {c['i78']} | {c['i80']} | {c['i84']} | {c['i88']} | {c['i8c']} | {c['wb6']} | "
              f"{c['wbe']} {byid.get(c['wbe'], '')} | {abis} | {'/'.join(map(str, c['aff']))} | "
              f"{c['eq114']},{c['eq118']},{c['eq11c']},{c['eq120']} |")


def cmd_check():
    items, _ = load_items()
    for r in items:
        calc = stat_text(r).replace(" ", "")
        desc = r["desc"].replace(" ", "")
        if r["slot"] == 4 or "DVD" in r["name"]:
            continue
        if calc != desc:
            print(f"{r['id']:3d} {r['name']}: 설명 [{r['desc']}]  수치 [{stat_text(r)}]")


def cmd_tabs(out):
    import numpy as np
    from PIL import Image
    sheet = Image.new("RGBA", (4 * 136, 40), (40, 40, 48, 255))
    for i, n in enumerate((419, 420, 421, 422)):
        d = open(path("BGR", f"{n:04d}.BGR"), "rb").read()
        w, h = struct.unpack_from("<2H", d, 0x24)
        a = np.frombuffer(d, np.uint8, w * h * 4, 0x2a).reshape(h, w, 4)[::-1]  # BGRA, 아래에서 위로
        im = Image.fromarray(np.ascontiguousarray(a[:, :, [2, 1, 0, 3]]), "RGBA")
        sheet.alpha_composite(im.crop((0, 0, 64, 16)).resize((128, 32), Image.NEAREST), (i * 136 + 4, 4))
    sheet.save(out)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    c = sys.argv[1] if len(sys.argv) > 1 else "items"
    if c == "tabs":
        cmd_tabs(sys.argv[2])
    else:
        {"items": cmd_items, "weapons": cmd_weapons, "chars": cmd_chars, "check": cmd_check}[c]()
