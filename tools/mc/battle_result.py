"""마그나카르타 전투 결과(Carta Result) 읽개·그림.

MCAlpha.dll 정적 분석으로 옮긴 것 — 분석: 옵시디안 Project/Magnacarta/분석/분석-전투결과.md
  적이 쓰러질 때 카르타 얻기     0x5208ce9f~0x5208d27b (갈래 = 적 ChrParam +0x6d, 배우 +0x21d)
  결과 칸에 더하기               0x520a01d6 (dealer +0x10c8[속성], 0~6)
  결과 칸 비우기                 0x520a0196
  이긴 뒤 묶음에 더하기          0x520a0224 (dealer +0x88[i] += +0x10c8[i])
  결과 창                         0x520ac2b9 (FrameData 245, 줄마다 "%4d" 를 (128, 62 + 24·i))
  Carta 쪽 ◀▶                    0x52081ec3 (명령 0xb9~0xbf / 0xc0~0xc6)

    python tools/mc/battle_result.py table            # 적 갈래별 카르타 얻는 법
    python tools/mc/battle_result.py shot OUT.png     # 결과 창 짜 맞춤(보기 값)
    python tools/mc/battle_result.py sim 0032 1000    # 무리 하나를 1000 번 이겼을 때 평균 카르타
"""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(__file__))
import battle  # noqa: E402

ELEMENTS = ["地", "火", "風", "暗", "水", "電", "光"]
W, H = 800, 600
BASE = (0, H - 480)
RIGHT = (W - 640, 0)


def rule(kind, values):
    """적 갈래(ChrParam +0x6d) → (확률, 양 고르기). 속성은 늘 rand()%7 (0x5208cf1e 등).
    점프 표 0x5208d2f0: 0 → 0x5208cee6 · 1,3 → 0x5208cfb3 · 2 → 0x5208d056 · 4 → 0x5208d0f9 · 5 → 0x5208d1b1."""
    v = values
    if kind == 0:
        return 0.5, lambda: random.randrange(v[52]) + 1          # rand()%2==0 이면, 1 ~ Value52(5)
    if kind in (1, 3):
        return 1.0, lambda: v[50]                                # Value50 = 1
    if kind == 2:
        return 1.0, lambda: v[49]                                # Value49 = 1
    if kind == 4:
        return 0.1, lambda: v[51]                                # rand()%10==0 이면 Value51 = 1
    if kind == 5:
        return 1.0, lambda: v[53]                                # Value53 = 10 (보스)
    return 0.0, lambda: 0


def table():
    sys.stdout.reconfigure(encoding="utf-8")
    v = battle.load_values()
    from collections import Counter
    cnt = Counter(c["kind"] for c in battle.load_chars())
    for k in range(6):
        pr, amt = rule(k, v)
        print("갈래 %d (CharData %d명): 확률 %.0f%%  양 %s" % (k, cnt.get(k, 0), pr * 100,
              {0: "1~%d" % v[52], 1: v[50], 2: v[49], 3: v[50], 4: v[51], 5: v[53]}[k]))


def simulate(group, n=1000):
    sys.stdout.reconfigure(encoding="utf-8")
    v = battle.load_values()
    chars = {c["id"]: c for c in battle.load_chars()}
    ids = battle.load_mon(group)[:5]
    tot = [0] * 7
    for _ in range(n):
        for i in ids:
            pr, amt = rule(chars[i]["kind"], v)
            if random.random() < pr:
                tot[random.randrange(7)] += amt()
    print("Mon %04d %s → 한 번 이길 때 평균" % (group, [chars[i]["name"] for i in ids]),
          {ELEMENTS[k]: round(tot[k] / n, 2) for k in range(7)}, "합", round(sum(tot) / n, 2))


def shot(out, values=(3, 0, 10, 0, 5, 0, 1)):
    import render2d as r2
    from PIL import Image
    im = Image.new("RGBA", (W, H), (34, 30, 28, 255))
    f = r2.read_fdi(245)
    x, y = f["pos"][0] + BASE[0] + RIGHT[0], f["pos"][1] + BASE[1] + RIGHT[1]
    im.alpha_composite(r2.render_fdi(245), (x, y))
    font = r2.Font(0)
    for i, val in enumerate(values):
        font.draw(im, x + 128, y + 62 + 24 * i, "%4d" % val, (255, 255, 255))
    im.convert("RGB").save(out)
    print("FrameData 245 자리", (x, y), "크기", f["size"])


if __name__ == "__main__":
    a = sys.argv
    if len(a) > 1 and a[1] == "shot":
        shot(a[2])
    elif len(a) > 1 and a[1] == "sim":
        simulate(int(a[2]), int(a[3]) if len(a) > 3 else 1000)
    else:
        table()
