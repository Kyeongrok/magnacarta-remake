"""마그나카르타 전투 기본 카메라(Follow Camera)·전투 진형 분석 도구.

    python tools/mc/battle_cam.py dis 0x5209e27f [n]     # MCAlpha.dll 풀기
    python tools/mc/battle_cam.py str <글>               # 문자열 자리
    python tools/mc/battle_cam.py xref 0x....            # 그 값을 쓰는 명령
분석: 옵시디안 분석-전투카메라.md
"""
import os
import struct
import sys

import capstone
import pefile

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")
_PE = {}


def pe(name="MCAlpha.dll"):
    if name not in _PE:
        p = pefile.PE(os.path.join(MC_DIR, name))
        _PE[name] = (p, p.get_memory_mapped_image(), p.OPTIONAL_HEADER.ImageBase)
    return _PE[name]


def dis(va, n=120, name="MCAlpha.dll", stop_ret=True):
    p, img, base = pe(name)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    out = []
    for i, ins in enumerate(md.disasm(img[va - base:va - base + 8 * n], va)):
        out.append(ins)
        if i >= n or (stop_ret and ins.mnemonic == "ret"):
            break
    return out


def f32_at(va, name="MCAlpha.dll"):
    p, img, base = pe(name)
    return struct.unpack_from("<f", img, va - base)[0]


def find_bytes(b, name="MCAlpha.dll"):
    p, img, base = pe(name)
    res, i = [], 0
    while True:
        i = img.find(b, i)
        if i < 0:
            return res
        res.append(base + i)
        i += 1


def xref(val, name="MCAlpha.dll"):
    """.text 를 선형으로 풀어 op_str 에 값이 나오는 명령."""
    p, img, base = pe(name)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.skipdata = True
    s = hex(val)
    out = []
    for sec in p.sections:
        if sec.Characteristics & 0x20000000:
            st = sec.VirtualAddress
            for ins in md.disasm(img[st:st + sec.Misc_VirtualSize], base + st):
                if s in ins.op_str:
                    out.append(ins)
    return out


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    cmd = argv[1]
    if cmd == "dis":
        va = int(argv[2], 16)
        n = int(argv[3]) if len(argv) > 3 else 120
        name = argv[4] if len(argv) > 4 else "MCAlpha.dll"
        for ins in dis(va, n, name, stop_ret=False):
            extra = ""
            if "dword ptr [0x" in ins.op_str and ins.mnemonic.startswith("f"):
                try:
                    a = int(ins.op_str.split("[")[1].split("]")[0], 16)
                    extra = "   ; %g" % f32_at(a, name)
                except Exception:
                    pass
            print(hex(ins.address), ins.mnemonic, ins.op_str, extra)
    elif cmd == "str":
        name = argv[3] if len(argv) > 3 else "MCAlpha.dll"
        for a in find_bytes(argv[2].encode("cp949"), name):
            print(hex(a))
    elif cmd == "xref":
        name = argv[3] if len(argv) > 3 else "MCAlpha.dll"
        for ins in xref(int(argv[2], 16), name):
            print(hex(ins.address), ins.mnemonic, ins.op_str)


if __name__ == "__main__":
    main(sys.argv)
