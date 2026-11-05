"""마그나카르타 .3do 시퀀스 명령(모션에 붙은 명령) 읽개.

배치(로더 MCAlpha.dll 0x5202ef3d~, 시퀀스마다 u32 명령 수 뒤 60바이트씩):
  i32 key      열쇠틀 차례(이 틀에 들어설 때 돈다). −1 이면 시퀀스 전체에 붙는 목록(seq +0x24)
  i32 unused   읽고 버림
  u32 type     명령 갈래(레코드 +0) — 처리 MCVObs3Obj 갈래 표 0x520476d7(0~11)
  u32 b[5]     레코드 +8..+0x18
  u32 c        레코드 +0x1c
  i32 t[6]     정수! tx, ty, tz(÷20) · rx, ry, rz(SetRotateX/Y/Z 정수 인자) → 레코드 +0x20 행렬
render3d.py 는 이 꼬리를 (kind, arg, body) 로 읽는데 kind = key, arg = unused, body[0] = type 이다.
분석: 옵시디안 분석-모션명령.md

    python tools/mc/seqcmd.py stats          # 갈래별 개수
    python tools/mc/seqcmd.py dump 101 100   # 3do 101 의 시퀀스 100 명령
"""
import os
import struct
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(__file__))
import render3d  # noqa: E402

TYPES = {
    0: "3D 물체 붙이기(b0 3do?, b1 모션, b2 뼈)",
    1: "부품 값 A 바꾸기(b0 부품|전부, b1, b2)",
    2: "부품 값 B 바꾸기(b0 부품|전부, b1, b2)",
    3: "텍스처 바꾸기(b0 재질, b1 TXRS)",
    4: "검광(ASRSwordLightColor)",
    5: "보이기 깃발(b0 == 0)",
    6: "물리 행렬 넣기",
    7: "물리 멈춤",
    8: "(없음)",
    9: "전투 때림(메시지 0x403 → 0x401)",
    10: "효과음(MCSound, b0 SED, 뼈 자리)",
    11: "발소리(메시지 0x404, b0 = 바닥 소리 표 칸)",
}


def records(model):
    """3do 하나의 (시퀀스 id, key, type, b[5], c, t[6]) 를 낸다."""
    m = render3d.load_3do(model)
    for s in m.seqs:
        for kind, arg, body in s["cmds"]:
            a, b, c, f6 = body
            ints = [struct.unpack("<i", struct.pack("<f", v))[0] for v in f6]
            yield s["a"], kind, a, b, c, ints


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[1] == "stats":
        cnt = Counter()
        for fn in sorted(os.listdir(os.path.join(render3d.MC_DIR, "3DOBS"))):
            try:
                for rec in records(int(fn[:4])):
                    cnt[(rec[2], rec[1] < 0)] += 1
            except Exception:
                pass
        for (t, whole), n in sorted(cnt.items()):
            print("type %2d %s %5d  %s" % (t, "전체" if whole else "틀  ", n, TYPES.get(t, "?")))
    elif argv[1] == "dump":
        model, seq = int(argv[2]), int(argv[3]) if len(argv) > 3 else None
        for sid, key, t, b, c, ints in records(model):
            if seq is None or sid == seq:
                print("seq %3d key %3d type %2d %-34s b %s c %d t %s" % (sid, key, t, TYPES.get(t, "?")[:34], list(b), c, ints))


if __name__ == "__main__":
    main(sys.argv)
