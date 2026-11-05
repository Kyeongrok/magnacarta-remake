"""마그나카르타 원본 세이브 MCxx.sav 읽개.

배치는 MCAlpha.dll 세이브 쓰기 0x5206fdac / 읽기(머리만) 0x5207021e / 읽기(전부) 0x520703f3,
캐릭터 묶음 쓰기 0x520b315f (읽기 0x520b2c5f) 를 그대로 옮긴 것. 분석: 옵시디안 분석-세이브.md

    python tools/mc/savefile.py            # MC00~MC03 요약
    python tools/mc/savefile.py 1          # MC01 자세히
"""
import os
import struct
import sys
import zlib

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")

# 캐릭터 묶음은 이 차례로 8명 (0x520b31be 갈래 표 → 0x520b3854(id))
CHAR_ORDER = [0, 3, 2, 1, 4, 6, 5, 20]


def _cstr(b):
    return b.split(b"\0")[0].decode("cp949", "replace")


def read_char(d, o):
    """ChrParam 한 명 281바이트 — 메모리 +4 부터 차례대로(쓰기 0x520b336c~0x520b37b9)."""
    c = {}
    c["id"], = struct.unpack_from("<i", d, o); o += 4                        # +0x04
    c["name"] = _cstr(d[o:o + 100]); o += 100                                # +0x08
    c["kind"] = d[o]; o += 1                                                 # +0x6d
    c["u6e"], = struct.unpack_from("<h", d, o); o += 2                       # +0x6e
    keys = ["tp", "wtp", "hp", "apocalypse", "str", "def", "int", "dex", "u90", "u94", "u98"]
    for k, v in zip(keys, struct.unpack_from("<11i", d, o)):                 # +0x70~+0x98
        c[k] = v
    o += 44
    c["amd"], c["face"], *c["u_a0"] = struct.unpack_from("<9h", d, o); o += 18   # +0x9c~+0xac
    c["u_ae"] = d[o:o + 6].hex(); o += 6                                     # +0xae
    vals = struct.unpack_from("<11h", d, o); o += 22                         # +0xb4~+0xc8
    c["u_b4"] = vals[:5]
    c["singi"] = vals[5]                                                     # +0xbe
    c["head"], c["breast"], c["abdomen"], c["arm"], c["leg"] = vals[6:11]    # +0xc0~+0xc8
    c["carta"] = list(struct.unpack_from("<7h", d, o)); o += 14              # +0xca 친화 기본
    c["abilities"] = [a for a in struct.unpack_from("<10i", d, o) if a >= 0]; o += 40   # +0xe8
    c["affinity"] = list(struct.unpack_from("<7h", d, o)); o += 14           # +0xd8 친화 런타임
    c["equip"] = list(struct.unpack_from("<4i", d, o)); o += 16              # +0x114~+0x120 몸·반지·목걸이·기타
    return c, o


def load(num):
    d = open(os.path.join(MC_DIR, "MC%02d.sav" % num), "rb").read()
    s = {"size": len(d)}
    s["version"], = struct.unpack_from("<I", d, 0)                           # 4 (읽을 때 4 가 아니면 오류)
    s["place"] = _cstr(d[4:0x16])                                            # 장면 +0x11dc 0x12 장소 이름
    o = 0x16
    s["field"], = struct.unpack_from("<I", d, o); o += 4                    # 장면 +0x10cc 필드 번호
    s["play_ms"], = struct.unpack_from("<I", d, o); o += 4                  # +0x40 (놀이 시간 ms 로 보임 — 추정)
    s["party_count"], = struct.unpack_from("<i", d, o); o += 4              # +0x44
    s["party"] = list(struct.unpack_from("<7i", d, o)); o += 0x1c           # +0x48 파티 차례(CharData id, −1 빈칸)
    s["u64"], = struct.unpack_from("<i", d, o); o += 4                      # +0x64
    s["battle"] = list(struct.unpack_from("<4i", d, o)); o += 0x10          # +0x68 전투에 나가는 칸(파티 차례 번호로 보임)
    s["u78"], = struct.unpack_from("<i", d, o); o += 4                      # +0x78 (모두 1000)
    s["u7c"] = list(struct.unpack_from("<7i", d, o)); o += 0x1c             # +0x7c 7칸(속성마다? — 뜻 모름)
    inv = struct.unpack_from("<1024i", d, o); o += 0x1000                   # +0x98 소지품 1024칸
    s["items"] = [i for i in inv if i >= 0]
    loc = struct.unpack_from("<2000i", d, o); o += 8000                     # 장면 +0x3c 객체의 +0x24 표(2000) — 지역 스위치로 보임
    glo = struct.unpack_from("<2000i", d, o); o += 8000                     # [0x520f2304]+0x24 표(2000) — 전역 스위치(사건 101/102)
    s["local_switches"] = {i: v for i, v in enumerate(loc) if v}
    s["global_switches"] = {i: v for i, v in enumerate(glo) if v}
    s["pos"] = struct.unpack_from("<3f", d, o); o += 12                     # 플레이어 자리 (0x5205ab10)
    s["dir"] = struct.unpack_from("<3f", d, o); o += 12                     # 바라보는 쪽 (0x520714c0)
    n, = struct.unpack_from("<I", d, o); o += 4                             # 캐릭터 수 8
    s["chars"] = []
    for _ in range(n):
        c, o = read_char(d, o)
        s["chars"].append(c)
    s["tail_zero"], = struct.unpack_from("<I", d, o); o += 4                # u32 0
    s["pad"] = len(d) - o                                                    # 0x400 (마지막 4바이트는 CRC)
    s["crc_ok"] = zlib.crc32(d[:-4]) == struct.unpack_from("<I", d, len(d) - 4)[0]   # CRC32(앞 size−4 바이트) = 끝 4바이트
    return s


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.path.insert(0, os.path.dirname(__file__))
    import items as itm
    import battle
    sg = battle.load_sgi()
    names = {r["id"]: r["name"] for r in itm.load_items()[0]}
    nums = [int(argv[1])] if len(argv) > 1 else range(4)
    for k in nums:
        s = load(k)
        party = [s["chars"][CHAR_ORDER.index(i)]["name"] if i in CHAR_ORDER else str(i) for i in s["party"][:s["party_count"]]]
        print("MC%02d  %s(필드 %d)  놀이 %.1f분  파티 %s  전투칸 %s  아이템 %d  전역스위치 %d  CRC %s  pos %s" % (
            k, s["place"] or "-", s["field"], s["play_ms"] / 60000, party, s["battle"], len(s["items"]),
            len(s["global_switches"]), s["crc_ok"], tuple(round(v, 1) for v in s["pos"])))
        if len(nums) == 1:
            for c in s["chars"]:
                print("   %-8s 신기 %-14s 체공방지민 %s 장비 %s 기술 %s" % (
                    c["name"], sg.get(c["singi"], {}).get("name", c["singi"]),
                    [c[x] for x in ("hp", "str", "def", "int", "dex")],
                    [names.get(e, "-") if e >= 0 else "-" for e in c["equip"]], c["abilities"]))
            print("   아이템", [names.get(i, i) for i in s["items"]])
            print("   전역 스위치", s["global_switches"])
            print("   u7c", s["u7c"], "u64", s["u64"])


if __name__ == "__main__":
    main(sys.argv)
