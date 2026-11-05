using System.Diagnostics;
using MagnaCarta.Native;
using MagnaCarta.Render;
using MagnaCarta.Scenes;

namespace MagnaCarta;

/// <summary>Win32 창 하나, 메시지 루프, 장면 바꾸기. 확인용 <c>--script</c> 도 여기서 돌린다.</summary>
internal sealed class GameWindow : IDisposable
{
    private const string ClassName = "MagnaCartaPatch";
    private readonly Win32.WndProc _proc;
    private readonly IntPtr _hwnd;
    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly Input _input = new();
    private IScene? _scene;
    private bool _running = true;
    private int _lastX, _lastY;
    private readonly Queue<string> _script;
    private float _scriptWait;
    private readonly List<(int Vk, float Left)> _held = [];

    public GameWindow(string? script, bool battle = false, int field = -1)
    {
        _script = new Queue<string>((script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _proc = WndProc;
        var instance = Win32.GetModuleHandleW(null);
        var wc = new Win32.WndClassEx
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WndClassEx>(),
            Style = Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            WindowProc = _proc,
            Instance = instance,
            Cursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),
            ClassName = ClassName,
        };
        Win32.RegisterClassExW(ref wc);
        var rect = new Win32.Rect { Right = 1200, Bottom = 900 };
        Win32.AdjustWindowRect(ref rect, Win32.WS_OVERLAPPEDWINDOW, false);
        _hwnd = Win32.CreateWindowExW(0, ClassName, "마그나카르타 — 패치판", Win32.WS_OVERLAPPEDWINDOW | Win32.WS_VISIBLE,
            Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, rect.Width, rect.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        var ico = Path.Combine(Data.McFiles.Dir, "mc.ico");
        if (File.Exists(ico))
        {
            var h = Win32.LoadImageW(IntPtr.Zero, ico, 1, 0, 0, 0x10);
            if (h != IntPtr.Zero) { Win32.SendMessageW(_hwnd, Win32.WM_SETICON, 0, h); Win32.SendMessageW(_hwnd, Win32.WM_SETICON, 1, h); }
        }
        Win32.GetClientRect(_hwnd, out var client);
        _gfx = new Gfx(_hwnd, client.Width, client.Height);
        _screen = new ScreenImage(_gfx);
        // 확인용: --field 로 바로 들어갈 때 MC_TEST_PARTY=0,1,2 면 파티를 그렇게(조우 전투 이기기 확인용)
        if (field >= 0 && Environment.GetEnvironmentVariable("MC_TEST_PARTY") is { Length: > 0 } tp)
        { Scenes.GameState.Party.Clear(); Scenes.GameState.Party.AddRange(tp.Split(',').Select(int.Parse)); }
        _scene = battle ? new BattleScene(_gfx, _screen) : field >= 0 ? new FieldScene(_gfx, _screen, field)
            // 원본 상태 51: 로고 0023 → 오프닝 0010 → 타이틀(분석-컷씬). 동영상은 x86 빌드에서만(binkw32 가 32비트)
            : int.TryParse(Environment.GetEnvironmentVariable("MC_TEST_MOVIE"), out int tmv) ? new Scenes.MovieScene(_gfx, _screen, tmv, () => new TitleScene(_gfx, _screen))   // 확인용
            : Scenes.MovieScene.Available ? new Scenes.MovieScene(_gfx, _screen, 23, () => new Scenes.MovieScene(_gfx, _screen, 10, () => new TitleScene(_gfx, _screen)))
            : new TitleScene(_gfx, _screen);
    }

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (_running)
        {
            while (Win32.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.Message == Win32.WM_QUIT) { _running = false; break; }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            if (!_running || _scene == null) break;
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(now - last, 0.1);
            last = now;

            string? shot = RunScript(dt);
            var next = _scene.Update(dt, _input);
            _input.EndFrame();
            if (next != _scene)
            {
                // 무작위 조우 전투는 필드를 쥐고 있다가 이기면 그대로 돌려준다 — 그때는 버리지 않는다
                if (!(next is BattleScene b && b.ReturnTo == _scene) && !(next is Scenes.MovieScene mv && mv.ReturnTo == _scene)) _scene.Dispose();
                _scene = next;
                // 맵 읽기에 걸린 시간은 틀 시간에 넣지 않는다
                last = clock.Elapsed.TotalSeconds;
                if (_scene == null) break;
            }
            _scene.Draw();
            if (shot != null) _gfx.Capture(shot);
            _gfx.Present();
        }
        Win32.DestroyWindow(_hwnd);
    }

    /// <summary>
    /// 확인용 대본: <c>wait:초</c> · <c>key:VK</c>(한 번 누름) · <c>hold:VK:초</c> · <c>shot:경로</c> · <c>quit</c>.
    /// VK 는 글자(W) 또는 이름(enter, up, down, left, right, shift, esc) 또는 수.
    /// </summary>
    private string? RunScript(float dt)
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var (vk, left) = _held[i];
            if ((left -= dt) <= 0) { _input.KeyUp(vk); _held.RemoveAt(i); }
            else _held[i] = (vk, left);
        }
        if (_scriptWait > 0) { _scriptWait -= dt; return null; }
        while (_script.Count > 0)
        {
            var parts = _script.Dequeue().Split(':');
            switch (parts[0])
            {
                case "wait": _scriptWait = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); return null;
                case "key": { int vk = Vk(parts[1]); _input.KeyDown(vk); _held.Add((vk, 0.05f)); break; }
                case "hold":
                    foreach (var k in parts[1].Split('+'))
                    {
                        int vk = Vk(k);
                        _input.KeyDown(vk);
                        _held.Add((vk, float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    break;
                case "drag": _input.RightDown = true; _input.DragX = int.Parse(parts[1]); _input.DragY = parts.Length > 2 ? int.Parse(parts[2]) : 0; return null;
                case "undrag": _input.RightDown = false; break;
                case "shot": return string.Join(':', parts[1..]);
                case "quit": _running = false; return null;
            }
        }
        return null;
    }

    private static int Vk(string name) => name.ToLowerInvariant() switch
    {
        "enter" => Input.Enter, "up" => Input.Up, "down" => Input.DownKey, "left" => Input.Left, "right" => Input.Right,
        "shift" => Input.Shift, "esc" => Input.Escape, "space" => Input.Space,
        { Length: 1 } s => char.ToUpperInvariant(s[0]),
        var s => int.Parse(s),
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_DESTROY: Win32.PostQuitMessage(0); _running = false; return IntPtr.Zero;
            case Win32.WM_ERASEBKGND: return 1;
            case Win32.WM_SIZE:
                _gfx?.Resize(Win32.LowWord(lParam) & 0xFFFF, Win32.HighWord(lParam) & 0xFFFF);
                return IntPtr.Zero;
            case Win32.WM_KILLFOCUS: _input.ReleaseAll(); break;
            case Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN:
                if ((int)wParam == Input.F12 && _gfx != null)
                {
                    Directory.CreateDirectory("shots");
                    _scene?.Draw();
                    _gfx.Capture(Path.Combine("shots", $"{DateTime.Now:yyyyMMdd-HHmmss}.png"));
                }
                _input.KeyDown((int)wParam);
                return IntPtr.Zero;
            case Win32.WM_KEYUP or Win32.WM_SYSKEYUP: _input.KeyUp((int)wParam); return IntPtr.Zero;
            case Win32.WM_MOUSEMOVE:
            {
                int x = Win32.LowWord(lParam), y = Win32.HighWord(lParam);
                if (_input.RightDown) { _input.DragX += x - _lastX; _input.DragY += y - _lastY; }
                _lastX = x; _lastY = y;
                _input.MouseX = x; _input.MouseY = y;
                return IntPtr.Zero;
            }
            case Win32.WM_LBUTTONDOWN: _input.LeftClicked = true; return IntPtr.Zero;
            case Win32.WM_RBUTTONDOWN: _input.RightDown = true; Win32.SetCapture(hWnd); return IntPtr.Zero;
            case Win32.WM_RBUTTONUP: _input.RightDown = false; Win32.ReleaseCapture(); return IntPtr.Zero;
            case Win32.WM_MOUSEWHEEL: _input.Wheel += Win32.HighWord(wParam) / 120f; return IntPtr.Zero;
        }
        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Audio.Music.Stop();
        _scene?.Dispose();
        _screen.Dispose();
        _gfx.Dispose();
    }
}
