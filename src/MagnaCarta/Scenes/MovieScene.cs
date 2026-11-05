using System.Numerics;
using System.Runtime.InteropServices;
using MagnaCarta.Data;
using MagnaCarta.Render;
using Vortice.Direct3D11;

namespace MagnaCarta.Scenes;

/// <summary>
/// 원본 Bink 동영상(MOV\%04d.BMC = Bink 1) — 원본 폴더의 binkw32.dll(x86)을 그대로 부른다(분석-컷씬 §1.0).
/// 64비트로 돌 때는 DLL 을 못 불러 곧바로 다음 장면으로 넘어간다. Esc·Enter·Space·왼쪽 누름으로 건너뜀.
/// </summary>
internal sealed class MovieScene : IScene
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr OpenFn([MarshalAs(UnmanagedType.LPStr)] string name, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OneFn(IntPtr bink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VoidFn(IntPtr bink);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyFn(IntPtr bink, IntPtr dest, int pitch, uint height, uint x, uint y, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SoundFn(IntPtr open, uint param);

    private static IntPtr _lib;
    private static OpenFn? _open;
    private static OneFn? _doFrame, _wait;
    private static VoidFn? _next, _close;
    private static CopyFn? _copy;

    private const uint Surface32 = 3, CopyAll = 0x80000000;

    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly Func<IScene> _next2;
    private readonly IntPtr _bink;
    private readonly int _w, _h, _frames;
    private readonly Image32? _img;
    private readonly ID3D11Texture2D? _tex;
    private readonly ID3D11ShaderResourceView? _view;
    private readonly GCHandle _pin;
    private bool _done;

    /// <summary>끝나면 돌아갈 장면(사건 505 — 필드를 버리지 않는다).</summary>
    public IScene? ReturnTo { get; init; }

    /// <summary>binkw32 를 부를 수 있는가(32비트 프로세스 + 파일 있음).</summary>
    public static bool Available
    {
        get
        {
            if (Environment.Is64BitProcess) { Log("64비트 — 동영상 없음"); return false; }
            if (Environment.GetEnvironmentVariable("MC_NO_MOVIE") is not null) return false;
            bool ok = Load();
            if (!ok) Log("Load 실패");
            return ok;
        }
    }

    private static void Log(string m)
    {
        if (Environment.GetEnvironmentVariable("MC_MOVIE_LOG") is { Length: > 0 } f) File.AppendAllText(f, m + Environment.NewLine);
    }

    private static bool Load()
    {
        if (_lib != IntPtr.Zero) return true;
        var path = Path.Combine(McFiles.Dir, "binkw32.dll");
        if (!File.Exists(path)) { Log($"binkw32 없음 {path}"); return false; }
        try { _lib = NativeLibrary.Load(path); }
        catch (Exception e) { Log($"binkw32 읽기 실패: {e.Message}"); _lib = IntPtr.Zero; return false; }
        T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
        try
        {
            _open = F<OpenFn>("_BinkOpen@8"); _doFrame = F<OneFn>("_BinkDoFrame@4"); _wait = F<OneFn>("_BinkWait@4");
            _next = F<VoidFn>("_BinkNextFrame@4"); _close = F<VoidFn>("_BinkClose@4"); _copy = F<CopyFn>("_BinkCopyToBuffer@28");
            // 소리: waveOut 길(창 핸들이 필요 없다). 원본은 Miles(Mss32) 를 쓰는 것으로 보이나 패치판은 단순한 쪽
            if (!Audio.Music.Muted && NativeLibrary.TryGetExport(_lib, "_BinkOpenWaveOut@4", out var waveOut))
                F<SoundFn>("_BinkSetSoundSystem@8")(waveOut, 0);
            return true;
        }
        catch (Exception e) { Log($"binkw32 함수 못 찾음: {e.GetType().Name} {e.Message}"); _lib = IntPtr.Zero; return false; }
    }

    /// <summary>
    /// 동영상 파일: MOV\%04d.BMC, 없으면 MOV.pak 안(0013·0014·0015 가 Bink 머리 크기로 이어 붙어 있음, 분석-컷씬)을
    /// %LOCALAPPDATA%\MagnaCartaPatch\mov 에 한 번 잘라 두고 그것을 쓴다. 없으면 null.
    /// </summary>
    public static string? FileFor(int number)
    {
        var plain = Path.Combine(McFiles.Dir, "MOV", $"{number:D4}.BMC");
        if (File.Exists(plain)) return plain;
        if (number is < 13 or > 15) return null;
        var pak = Path.Combine(McFiles.Dir, "MOV", "MOV.pak");
        if (!File.Exists(pak)) return null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch", "mov");
        var cached = Path.Combine(dir, $"{number:D4}.BMC");
        if (File.Exists(cached)) return cached;
        try
        {
            using var f = File.OpenRead(pak);
            var head = new byte[8];
            long at = 0;
            for (int k = 13; k <= number; k++)
            {
                f.Position = at;
                if (f.Read(head, 0, 8) != 8 || head[0] != (byte)'B' || head[1] != (byte)'I' || head[2] != (byte)'K') return null;
                long size = BitConverter.ToUInt32(head, 4) + 8L;
                if (k == number)
                {
                    Directory.CreateDirectory(dir);
                    var tmp = cached + ".part";
                    using (var o = File.Create(tmp))
                    {
                        f.Position = at;
                        var buf = new byte[1 << 20];
                        for (long left = size; left > 0;)
                        {
                            int n = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;
                            o.Write(buf, 0, n); left -= n;
                        }
                    }
                    File.Move(tmp, cached, true);
                    return cached;
                }
                at += size;
            }
        }
        catch (IOException) { }
        return null;
    }

    public MovieScene(Gfx gfx, ScreenImage screen, int number, Func<IScene> next)
    {
        _gfx = gfx; _screen = screen; _next2 = next;
        var file = FileFor(number) ?? Path.Combine(McFiles.Dir, "MOV", $"{number:D4}.BMC");
        if (!Available || !File.Exists(file)) { Log($"건너뜀 {file} 있음={File.Exists(file)}"); _done = true; return; }
        _bink = _open!(file, 0);
        if (_bink == IntPtr.Zero) { Log($"BinkOpen 실패 {file}"); _done = true; return; }
        Log($"열림 {file} {Marshal.ReadInt32(_bink, 0)}x{Marshal.ReadInt32(_bink, 4)} 틀 {Marshal.ReadInt32(_bink, 8)}");
        _w = Marshal.ReadInt32(_bink, 0); _h = Marshal.ReadInt32(_bink, 4); _frames = Marshal.ReadInt32(_bink, 8);
        if (_w <= 0 || _h <= 0 || _w > 4096 || _h > 4096) { _close!(_bink); _bink = IntPtr.Zero; _done = true; return; }
        _img = new Image32(_w, _h);
        _pin = GCHandle.Alloc(_img.Bgra, GCHandleType.Pinned);
        _tex = gfx.DynamicTexture(_w, _h, out _view);
        Decode();
    }

    private void Decode()
    {
        _doFrame!(_bink);
        _copy!(_bink, _pin.AddrOfPinnedObject(), _w * 4, (uint)_h, 0, 0, Surface32 | CopyAll);
        // BINKSURFACE32 의 넷째 바이트는 쓰레기 — 불투명으로
        var b = _img!.Bgra;
        for (int i = 3; i < b.Length; i += 4) b[i] = 255;
        _gfx.UploadTexture(_tex!, _img);
    }

    public IScene? Update(float seconds, Input input)
    {
        if (!_done && (input.Pressed(Input.Escape) || input.Pressed(Input.Enter) || input.Pressed(Input.Space) || input.LeftClicked)) _done = true;
        if (!_done && _bink != IntPtr.Zero && _wait!(_bink) == 0)
        {
            int frame = Marshal.ReadInt32(_bink, 12);
            if (frame >= _frames) _done = true;
            else { _next!(_bink); Decode(); }
        }
        return _done ? (ReturnTo ?? _next2()) : this;
    }

    /// <summary>
    /// 전투 위에 겹쳐 그리기(전투 명령 508 — 분석-전투연출): 원본은 섞기 방식 a1(0~5 → 3~8, 반투명 줄)로 2D 그리기에 넘긴다.
    /// 패치판은 방식 뜻을 다 못 풀어 더하기로 겹친다.
    /// </summary>
    public void DrawOverlay()
    {
        if (_view == null || _done) return;
        float s = MathF.Min(_gfx.Width / (float)_w, _gfx.Height / (float)_h);
        float dw = _w * s, dh = _h * s;
        _screen.Draw(_view, (_gfx.Width - dw) / 2, (_gfx.Height - dh) / 2, dw, dh, Vector4.One, additive: true);
    }

    public bool Finished => _done;

    public void Draw()
    {
        _gfx.Begin(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        if (_view == null) return;
        // 화면 비율을 지켜 가운데에
        float s = MathF.Min(_gfx.Width / (float)_w, _gfx.Height / (float)_h);
        float dw = _w * s, dh = _h * s;
        _screen.Draw(_view, (_gfx.Width - dw) / 2, (_gfx.Height - dh) / 2, dw, dh, Vector4.One);
    }

    public void Dispose()
    {
        if (_bink != IntPtr.Zero) _close!(_bink);
        if (_pin.IsAllocated) _pin.Free();
        _view?.Dispose(); _tex?.Dispose();
    }
}
