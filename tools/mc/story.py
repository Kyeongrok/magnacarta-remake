"""마그나카르타 — 스토리·컷씬 자료 읽개 (분석용).

원본 폴더(환경 변수 MC_DIR)의 파일을 읽기만 한다. 풀어낸 자료는 저장소에 넣지 않는다.
python -I tools/mc/story.py fpb 0100      # 대사 묶음 한 개
python -I tools/mc/story.py fpball         # 모든 대사 묶음 요약
python -I tools/mc/story.py field 625      # 필드 이벤트 스크립트(명령 이름·대사 붙여서)
정적 분석 도우미(Pe, calls_to, switch_cases …)는 pefile·capstone 이 있어야 한다(-I 없이 실행).
"""
import os
import struct
import sys

MC_DIR = os.environ.get("MC_DIR", r"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store")


def path(*p):
    return os.path.join(MC_DIR, *p)


def read(*p):
    with open(path(*p), "rb") as f:
        return f.read()


# ── FPB: 대사 묶음 ───────────────────────────────────────────────────────────
# u32 n, n × {u32 id, u32 off, u32 len}, u32 pool_size, pool(CP949, 각 줄 NUL 끝)

def fpb(num):
    d = read("Fpb", "%04d.fpb" % num)
    n, = struct.unpack_from("<I", d, 0)
    base = 4 + 12 * n + 4
    out = []
    for i in range(n):
        sid, off, ln = struct.unpack_from("<III", d, 4 + 12 * i)
        raw = d[base + off: base + off + ln]
        out.append((sid, raw.rstrip(b"\0").decode("cp949", "replace")))
    return out


def main(argv):
    cmd = argv[1] if len(argv) > 1 else "fpball"
    if cmd == "field":
        dump_field(int(argv[2]))
    elif cmd == "fpb":
        for sid, s in fpb(int(argv[2])):
            print(sid, s)
    elif cmd == "fpball":
        for fn in sorted(os.listdir(path("Fpb"))):
            num = int(fn[:4])
            lines = fpb(num)
            print("%04d %3d  %s" % (num, len(lines), " / ".join(s for _, s in lines[:3])[:100]))



# ── 정적 분석 도우미 (MCAlpha.dll) ───────────────────────────────────────────
class Pe:
    def __init__(self, name="MCAlpha.dll"):
        import pefile
        self.pe = pefile.PE(path(name))
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = self.pe.get_memory_mapped_image()

    def u32(self, va):
        return struct.unpack_from("<I", self.img, va - self.base)[0]

    def cstr(self, va, enc="cp949"):
        o = va - self.base
        e = self.img.index(b"\0", o)
        return self.img[o:e].decode(enc, "replace")

    def find_str(self, s):
        """문자열의 VA 목록."""
        b = s.encode("cp949") + b"\0"
        res, i = [], 0
        while True:
            i = self.img.find(b, i)
            if i < 0:
                return res
            res.append(self.base + i)
            i += 1

    def xrefs(self, va):
        """va 를 imm32 로 담은 자리(대충 push/mov) 목록."""
        b = struct.pack("<I", va)
        res, i = [], 0
        while True:
            i = self.img.find(b, i)
            if i < 0:
                return res
            res.append(self.base + i)
            i += 1

    def dis(self, va, n=60, stop_ret=True):
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        out = []
        for ins in md.disasm(self.img[va - self.base: va - self.base + n * 8], va):
            out.append("%08x  %-8s %s" % (ins.address, ins.mnemonic, ins.op_str))
            if len(out) >= n or (stop_ret and ins.mnemonic == "ret"):
                break
        return "\n".join(out)

    def func_start(self, va):
        """va 앞쪽에서 가장 가까운 함수 머리(CC/90 채움 뒤)를 찾는다."""
        o = va - self.base
        while o > 0:
            if self.img[o - 1] in (0xCC, 0x90, 0xC3) and self.img[o] in (0x55, 0x6A, 0x83, 0x8B, 0x53, 0x56, 0x57, 0x64, 0x81, 0xA1, 0x51, 0xB8):
                if self.img[o - 1] != 0xC3 or self.img[o - 2] in (0xCC, 0x90):
                    return self.base + o
            o -= 1
        return None


def calls_to(p, target):
    """target 을 부르는 call rel32 자리."""
    res = []
    img, base = p.img, p.base
    text_end = 0xbd000
    for o in range(0x1000, text_end - 5):
        if img[o] == 0xE8:
            rel = struct.unpack_from("<i", img, o + 1)[0]
            if base + o + 5 + rel == target:
                res.append(base + o)
    return res


def rtti_name(p, vtbl):
    """vtable 앞의 RTTI CompleteObjectLocator → 클래스 이름."""
    try:
        col = p.u32(vtbl - 4)
        td = p.u32(col + 12)
        return p.cstr(td + 8, "latin-1")
    except Exception:
        return None


# ── 이벤트 블록 (.fld 꼬리, MCEvent::Load 0x5204ab93) ───────────────────────
# u32 n, n × { u32 trig, u32 nIf, nIf × CMD, u32 nThen, nThen × CMD }
# CMD = 0x3c 바이트: u32 op + 0x38 바이트 인자(u32 14개)

def parse_events(d, off):
    o = off
    n, = struct.unpack_from("<I", d, o); o += 4
    if not (0 < n < 2000):
        return None
    evs = []
    for _ in range(n):
        if o + 8 > len(d):
            return None
        trig, ni = struct.unpack_from("<Ii", d, o); o += 8
        if not (0 <= ni < 200):
            return None
        conds = []
        for _ in range(ni):
            if o + 60 > len(d):
                return None
            conds.append(struct.unpack_from("<I14i", d, o)); o += 60
        if o + 4 > len(d):
            return None
        nt, = struct.unpack_from("<i", d, o); o += 4
        if not (0 <= nt < 2000):
            return None
        acts = []
        for _ in range(nt):
            if o + 60 > len(d):
                return None
            acts.append(struct.unpack_from("<I14i", d, o)); o += 60
        evs.append((trig, conds, acts))
    return evs, o


def _plausible(evs):
    """조건 갈래 0·100·101·302·304, 명령 0~999 — 가짜 짝(468 앞 후보처럼 실수 값이 조건 자리에)을 거른다."""
    return all(all(c[0] in (0, 100, 101, 302, 304) for c in cc) and all(0 <= a[0] < 1000 for a in aa) for t, cc, aa in evs)


def find_events(d):
    """파일 끝에서 정확히 끝나는 이벤트 블록 중 그럴듯한 것의 가장 앞 자리(없으면 가장 앞 후보).
    뒤에서 처음 맞는 자리만 쓰면 29 개 필드(161·655·771 …)에서 묶음 꼬리의 가짜 짝을 잡았다(2026-11-06)."""
    best = anyoff = None
    for off in range(len(d) - 8, 0, -4):
        r = parse_events(d, off)
        if r and r[1] == len(d):
            anyoff = off
            if _plausible(r[0]):
                best = off
    return best if best is not None else anyoff


def scan(p, pred, start=0x1000, end=0xbd000):
    """.text 전체를 선형으로 풀어 pred(ins) 가 참인 명령을 돌려준다."""
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.skipdata = True
    out = []
    for ins in md.disasm(p.img[start:end], p.base + start):
        if pred(ins):
            out.append(ins)
    return out


def switch_cases(p, start, var, values=range(0, 3000), maxsteps=200):
    """MSVC 디버그 빌드 switch 를 값마다 따라가 갈래 자리를 모은다.

    start: switch 첫 명령(cmp [var], K) 자리.  var: '[ebp - 0x10]' 같은 지역 변수.
    돌려줌: {갈래 자리: [값 …]}
    """
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)

    def ins_at(va):
        return next(md.disasm(p.img[va - p.base: va - p.base + 16], va))

    def imm(s):
        return int(s, 0)

    res = {}
    for v in values:
        mem, regs, flags, pc = v, {}, None, start
        for _ in range(maxsteps):
            i = ins_at(pc)
            m, ops = i.mnemonic, [x.strip() for x in i.op_str.split(",")]
            nxt = pc + i.size
            if m == "cmp" and ops[0].endswith(var):
                flags = (mem, imm(ops[1]))
            elif m == "cmp" and ops[0] in regs:
                flags = (regs[ops[0]], imm(ops[1]))
            elif m == "mov" and ops[1].endswith(var):
                regs[ops[0]] = mem
            elif m == "mov" and ops[0].endswith(var) and ops[1] in regs:
                mem = regs[ops[1]]
            elif m in ("sub", "add") and ops[0] in regs:
                regs[ops[0]] = regs[ops[0]] - imm(ops[1]) if m == "sub" else regs[ops[0]] + imm(ops[1])
            elif m in ("xor",) and ops[0] == ops[1]:
                regs[ops[0]] = 0
            elif m in ("mov", "movzx") and "byte ptr [" in ops[1]:
                inner = ops[1].split("[")[1].rstrip("]")
                reg, _, disp = inner.partition(" + ")
                regs[ops[0]] = p.img[regs[reg] + imm(disp) - p.base] if reg in regs else None
                if ops[0] in ("al", "bl", "cl", "dl"):
                    regs["e" + ops[0][0] + "x"] = regs[ops[0]]
            elif m == "jmp" and "dword ptr [" in ops[0]:
                inner = ops[0].split("[")[1].rstrip("]")
                regpart, _, disp = inner.partition(" + ")
                reg = regpart.split("*")[0]
                nxt = p.u32(imm(disp) + regs[reg] * 4)
            elif m == "jmp":
                nxt = imm(ops[0])
            elif m.startswith("j") and flags:
                a, b = flags
                ua, ub = a & 0xffffffff, b & 0xffffffff
                t = {"je": a == b, "jne": a != b, "jg": a > b, "jge": a >= b, "jl": a < b, "jle": a <= b,
                     "ja": ua > ub, "jae": ua >= ub, "jb": ua < ub, "jbe": ua <= ub}.get(m)
                if t is None:
                    break
                if t:
                    nxt = imm(ops[0])
            else:
                res.setdefault(pc, []).append(v)
                break
            pc = nxt
    return res


def first_call(p, va, n=40):
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    for k, i in enumerate(md.disasm(p.img[va - p.base: va - p.base + 400], va)):
        if i.mnemonic == "call":
            return i.op_str
        if k > n or i.mnemonic in ("ret", "jmp"):
            return None


def all_events():
    """모든 .fld 의 (번호, 이벤트 목록)."""
    for fn in sorted(os.listdir(path("Field"))):
        d = read("Field", fn)
        off = find_events(d)
        if off is None:
            yield int(fn[:4]), None
            continue
        yield int(fn[:4]), parse_events(d, off)[0]


# ── 이벤트 명령 이름 (MCAlpha.dll 0x52071b9b 갈래 표 + 처리 함수 단언문으로 붙임) ──
# 확실성: ● 처리 코드로 확정, ○ 쓰임새로 짐작
ACT = {
    0: "CALL 이벤트●", 1: "SYNC 앞 명령들 끝날 때까지●", 2: "WAIT 프레임●", 3: "END 이벤트●",
    4: "GOTO 필드●", 5: "GOTO 필드+자리●", 7: "입력 잠금●", 8: "입력 풀기●", 9: "전투 시작●",
    15: "필드물체 조작○", 16: "타이틀로●",
    100: "지역스위치 =●", 101: "지역스위치 연산○", 102: "전역스위치 =●", 103: "전역스위치 연산○",
    200: "모션●", 201: "모션(변형)○", 202: "물체쪽으로 이동●", 203: "이동(변형)○", 204: "프레임 걸려 이동●",
    205: "이동○", 206: "경로 이동●", 207: "회전●", 208: "흔들기○", 209: "○", 210: "○",
    211: "캐릭터 보이기○", 212: "필드물체 보이기○", 213: "캐릭터 숨기기○", 214: "캐릭터 상태○",
    215: "○", 216: "○", 217: "물체 바라보기○", 218: "○", 219: "투명도○", 220: "투명도○", 221: "○",
    223: "○", 224: "립싱크(LS\%04d.lsn)●", 225: "축 회전●", 226: "○",
    300: "3D루프물체 켜기●", 301: "3D루프물체 재생●", 302: "3D루프물체 멈춤●", 303: "3D루프물체○",
    304: "3D루프물체 이동●", 305: "○", 306: "3D루프물체 경로●", 307: "3D루프물체 축회전●",
    308: "○", 309: "○", 310: "○", 311: "○",
    400: "2D물체 켜기●", 401: "2D물체○", 402: "2D물체○", 403: "2D물체○", 404: "2D물체 이동●",
    408: "○", 409: "○", 410: "○", 411: "○",
    500: "카메라 컷●", 501: "카메라 따라가기●", 502: "화면전환 들어오기●", 503: "화면전환 나가기●",
    504: "카메라 되돌리기○", 505: "카메라 이동기(pMov)●", 506: "커튼 걷기(페이드인)●",
    507: "커튼 치기(페이드아웃)●", 508: "카메라 캐릭터 보기○", 509: "카메라 물체 보기○", 510: "카메라○",
    511: "안개 농도●", 512: "○", 513: "○", 514: "○", 515: "화면전환 들어오기(기본)●",
    516: "화면전환 나가기(기본)●", 518: "프레임(FrameData) 띄우기●", 519: "프레임 닫기○",
    600: "소리물체 켜기●", 601: "소리물체 끄기●", 602: "효과음(캐릭터/물체 자리)●", 603: "효과음●",
    604: "효과음 반복○", 605: "음악 0번 채널 재생●", 606: "음악 0번 채널 끄기●",
    607: "음악 1번 채널 재생●", 608: "음악 1번 채널 끄기●", 609: "○", 610: "○", 611: "○",
    612: "○", 613: "○", 614: "○", 616: "루프 소리○", 618: "소리물체 이동●",
    700: "대사●", 701: "말상자 닫기○", 702: "선택지 띄우기●", 703: "선택지 결과●",
    704: "대사(카메라)●", 705: "말상자○",
    800: "캐릭터 능력치○", 801: "캐릭터 능력치○", 804: "파티 넣기●", 805: "파티 빼기●",
    807: "카르타(시안) 고르기●", 808: "○", 809: "파티원 확인●", 810: "○", 900: "○",
}
COND = {0: "늘(다른 이벤트 없을 때)●", 100: "지역스위치 비교●", 101: "전역스위치 비교●",
        302: "○", 304: "캐릭터가 구역 안●"}
CMP = {0: "==", 1: "<", 2: "<=", 3: ">", 4: ">=", 5: "!="}


def fmt_cmd(c, lines=None, cond=False):
    op, a = c[0], list(c[1:])
    while a and a[-1] == 0:
        a.pop()
    name = (COND if cond else ACT).get(op, "?")
    s = "%4d %-22s %s" % (op, name, a)
    if cond and op in (100, 101) and len(c) > 3:
        s = "%4d %-22s sw[%d] %s %d" % (op, name, c[1], CMP.get(c[2], c[2]), c[3])
    if not cond and op in (700, 704) and lines:
        txt = dict(lines).get(c[7])          # 대사 번호는 차례가 아니라 Fpb 의 id (0x520716e0)
        if txt is not None:
            s += "  「%s」" % txt.replace("$n", " / ")
    return s


def field_header(num):
    d = read("Field", "%04d.fld" % num)
    return struct.unpack_from("<6i", d)


def dump_field(num, out=print):
    hdr = field_header(num)
    out("## Field %04d  (머리 %s, 대사 Fpb %04d)" % (num, hdr, hdr[2]))
    try:
        lines = fpb(hdr[2])
    except OSError:
        lines = None
    d = read("Field", "%04d.fld" % num)
    off = find_events(d)
    if off is None:
        out("  (이벤트 블록 못 찾음)")
        return []
    evs, _ = parse_events(d, off)
    gotos = []
    for i, (t, conds, acts) in enumerate(evs):
        out("  ev%d 최대실행=%d" % (i, t))
        for c in conds:
            out("    IF " + fmt_cmd(c, cond=True))
        for c in acts:
            out("    DO " + fmt_cmd(c, lines))
            if c[0] in (4, 5, 9, 16):
                gotos.append((c[0], c[1]))
    return gotos


# ── SED / SMD / SVD: 소리 상자 ───────────────────────────────────────────────
# u32 0x25129319, [긴 머리만: "SoftMax(C) 2001, SED File V0.1a, Dev#4 han-kue, Park" 0x34 바이트],
# 0x34 바이트 머리 {u32 ?, u32 종류(0=WAV, 1=MP3), u32 0x12, 0 …}, 그 뒤 알맹이(RIFF WAVE 또는 MP3)

def sound_payload(sub, num):
    ext = {"SED": "SED", "SMD": "SMD", "SVD": "SVD"}[sub]
    d = read(sub, "%04d.%s" % (num, ext))
    if d[:4] != bytes.fromhex("19931225"):   # 깨진 머리(SED 0006): u32 0x12 + 쓰레기 0x28, RIFF 는 0x2c
        o = -8
    else:
        o = 4 + (0x34 if d[4:11] == b"SoftMax" else 0)
    if o < 0:
        i = d.find(b"RIFF", 0, 0x80)
        return ("wav" if i >= 0 else "?"), d[max(i, 0):]
    _, kind, tag = struct.unpack_from("<3I", d, o)
    return ("wav" if kind == 0 else "mp3"), d[o + 0x34:]


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main(sys.argv)
