# MagnaCarta — 마그나카르타 패치판

원본(마그나카르타: 진홍의 성흔, Softmax 2001)의 게임 폴더(`MC_DIR`, 기본 `C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store`)를
그 자리에서 읽어 다시 짓는다. 게임 자료는 저장소에 넣지 않는다.

```powershell
dotnet run --project src\MagnaCarta                 # 시작 화면부터
dotnet run --project src\MagnaCarta -- --field 592  # 그 필드에서 바로(확인용)
dotnet run --project src\MagnaCarta -- --battle     # 지하수로 캐노펀 시험 전투
# 사람 손 없이 돌려 보고 화면을 찍는다
dotnet run --project src\MagnaCarta -- --script "wait:2.2;key:enter;wait:3;hold:w:2;shot:a.png;quit"
```

| 폴더 | 내용 |
|---|---|
| `Data` | 원본 파일 읽개 — BGR·TXR(TGA/BMP/JPEG/GIF)·FrameData·WinDiData·글꼴, .3do(캐릭터·강체)·.ani·.amd, .wrd·.fld(인물·물체·구역·카메라 컷·감시 카메라·사건), Fpb 대사, CharData·Att·Abi·Sgi·Mon·Itm |
| `Render` | D3D11 — 맵(텍스처 × 라이트맵), 캐릭터(CPU 스키닝), 화면 그림, 2D 판 |
| `Scenes` | 시작 화면, 필드(+ 사건 돌리개), 전투, 상태·세이브 |
| `Audio` | 배경음(SMD → MP3, MCI)·목소리(SVD)·효과음(SED → WAV, PlaySound) |

## 되는 것

- 시작 화면(원본 그림·단추·판 번호·BGM 17). Start game → 651 → 626(프롤로그 글) → 625 → 114 → 531 원본 사건대로(대사·얼굴·목소리·카메라 컷).
- 필드: 원본 감시 카메라(V 로 뒤따르는 카메라), 출구로 필드 이동, 무작위 조우 → 전투 → 복귀, 마을 사람 대화·아이템, 보물상자, 사건 전투, 파티 넣기, 신기 진화.
- 전투: 원본 공식(능력치·피해·턴·TP·연결공격·오버크래쉬), F9 자동 전투.
- 상태 창(I/Tab) — 능력치·신기·아이템·장비 끼기. F5 저장, 시작 화면 Load game/Continue.

## 조작

필드: 방향키/WASD 걷기 · Shift 달리기 · Space 말 걸기/열기/대사 넘기기 · I/Tab 상태 · V 시점 · F5 저장 · B 시험 전투 · Esc 시작 화면 · F12 화면 찍기(`shots\`).
전투: Z 공격(때맞춰 연타 = 연결) · X 기술 · C 오버크래쉬 · A/D 대상 · Space 턴 끝 · F9 자동.

## 확인용 환경 변수

| 변수 | 뜻 |
|---|---|
| `MC_DIR` | 원본 폴더 |
| `MC_MUTE=1` | 소리 끄기 |
| `MC_EVENT_LOG=파일` | 돌린 사건 명령을 한 줄씩 |
| `MC_BATTLE_LOG=파일` | 전투 상태를 반 초마다 |
| `MC_TEST_EXIT=k` · `MC_TEST_NPC=k\|chest` · `MC_TEST_ENCOUNTER=1` · `MC_TEST_EVOLVE=id` | 출구·마을 사람·상자 앞으로 옮기기, 조우 주기 30틱, 진화 사건 흉내 |

오류가 나면 실행 폴더에 `crash.txt` 가 남는다. 세이브는 `%LOCALAPPDATA%\MagnaCartaPatch\save0.json`.

원본에서 못 딴 값·고친 원본 버그·고를 것은 옵시디안 `Project\Magnacarta\작업\패치목록.md`, 분석은 `분석\`. 분석용 파이썬은 `tools\mc\`.

## 버그 리포트

어느 화면에서나 **F12** — 글 쓰는 창이 뜨고, 보내면 통계 worker(`war-of-genesis-stats` 의 `POST /v1/bug?game=mc`)로 간다(대시보드 「마그나카르타」 탭).
같이 가는 것: 판 번호 · 장면(필드 번호 · 전투 …) · OS · 32/64비트 · 화면 크기 · `crash.txt` 끝(사용자 이름은 지움). 못 보내면 `%LOCALAPPDATA%\MagnaCartaPatchug-queue` 에 두었다가 다음에 켤 때 보낸다.
시험(`--script`)은 `MC_STATS_URL` 을 줄 때만 보내고, `MC_BUGREPORT_TEXT` 가 있으면 창 없이 그 글을 보낸다.

## 동영상(Bink)

원본 동영상(MOV\*.BMC)은 원본 폴더의 `binkw32.dll`(32비트)로 튼다 — **x86 으로 빌드했을 때만** 나온다:
`dotnet build -r win-x86 --self-contained false -p:PlatformTarget=x86` — `-r win-x86` 을 꼭 줄 것(안 주면 AnyCPU 빌드와 obj 를 같이 써서 64비트 실행 파일이 나올 때가 있다). 64비트로 돌면 지금처럼 건너뜀. 시작할 때 로고 0023 → 오프닝 0010 → 타이틀, Esc·Enter·Space 로 건너뜀.
`MC_NO_MOVIE=1` 이면 끄고, `MC_MOVIE_LOG=파일` 에 연 동영상·크기·틀 수를 적는다. `MC_TEST_MOVIE=번호` 는 그 동영상 하나를 틀고 타이틀로.
MOV.pak 안의 0013~0015 는 처음 틀 때 `%LOCALAPPDATA%\MagnaCartaPatch\mov` 에 잘라 둔다(원본 파일은 건드리지 않음).
