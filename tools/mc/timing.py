"""마그나카르타 틱 길이·필드 이동 속도 정적 분석 도우미 (MCAlpha.dll).

원본 폴더의 파일을 읽기만 한다. 분석 글: 옵시디안 mv/Project/Magnacarta/분석/분석-틱과이동.md

    python tools/mc/timing.py calls 0x520bd43c        # 그 IAT 칸(timeGetTime)을 부르는 곳
    python tools/mc/timing.py dis 0x5200c2d0 60       # 자리에서 명령 60개 풀기
    python tools/mc/timing.py refs 0x5200c2d0         # 그 값을 쓰는 명령(call/push/mov) 찾기
    python tools/mc/timing.py floats 0x52064440 0x52064a00   # 그 구간이 읽는 float 상수
"""
import os
import struct
import sys

import capstone
import pefile

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


class Pe:
    def __init__(self, name="MCAlpha.dll"):
        self.pe = pefile.PE(os.path.join(MC_DIR, name))
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = self.pe.get_memory_mapped_image()
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        self.md.skipdata = True
        text = next(s for s in self.pe.sections if s.Name.startswith(b".text"))
        self.text = (text.VirtualAddress, text.VirtualAddress + text.Misc_VirtualSize)

    def dis(self, va, n=40):
        off = va - self.base
        out = []
        for ins in self.md.disasm(self.img[off:off + n * 16], va):
            out.append(ins)
            if len(out) >= n:
                break
        return out

    def all_ins(self):
        a, b = self.text
        return self.md.disasm(self.img[a:b], self.base + a)

    def f32(self, va):
        return struct.unpack_from("<f", self.img, va - self.base)[0]

    def f64(self, va):
        return struct.unpack_from("<d", self.img, va - self.base)[0]

    def u32(self, va):
        return struct.unpack_from("<I", self.img, va - self.base)[0]


def refs(p, value):
    h = "0x%x" % value
    return [i for i in p.all_ins() if h in i.op_str]


def show(ins_list):
    for i in ins_list:
        print("%08x  %-8s %s" % (i.address, i.mnemonic, i.op_str))


def main(argv):
    p = Pe(os.environ.get("MC_PE", "MCAlpha.dll"))
    cmd = argv[1]
    if cmd == "dis":
        show(p.dis(int(argv[2], 16), int(argv[3]) if len(argv) > 3 else 40))
    elif cmd in ("calls", "refs"):
        show(refs(p, int(argv[2], 16)))
    elif cmd == "floats":
        lo, hi = int(argv[2], 16), int(argv[3], 16)
        import re
        for i in p.all_ins():
            if lo <= i.address < hi and i.mnemonic.startswith("f") and "dword ptr [0x" in i.op_str:
                a = int(re.search(r"\[0x([0-9a-f]+)\]", i.op_str).group(1), 16)
                print("%08x  %-6s %s  = %r" % (i.address, i.mnemonic, i.op_str, p.f32(a)))
            if lo <= i.address < hi and i.mnemonic.startswith("f") and "qword ptr [0x" in i.op_str:
                a = int(re.search(r"\[0x([0-9a-f]+)\]", i.op_str).group(1), 16)
                print("%08x  %-6s %s  = %r" % (i.address, i.mnemonic, i.op_str, p.f64(a)))


if __name__ == "__main__":
    main(sys.argv)
