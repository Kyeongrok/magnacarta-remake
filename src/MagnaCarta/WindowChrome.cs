using System.Numerics;
using MagnaCarta.Data;
using MagnaCarta.Native;
using MagnaCarta.Render;
using Vortice.Direct3D11;

namespace MagnaCarta;

/// <summary>
/// 패치판 제목 줄 — 창세기전·대항해시대 패치판처럼 테두리 없는 창의 맨 위를 직접 그린다(사용자 요청 2026-11-06).
/// 왼쪽 ☰(차림 → 설정 · 시작 화면으로 · 끝내기), 가운데 제목, 오른쪽 내리기·닫기. 나머지 줄은 잡고 끌어 창을 옮긴다.
/// 설정 창에서 해상도(게임 화면 크기)와 전체 화면을 고르고, 고른 것은 %LOCALAPPDATA%\MagnaCartaPatch\display.txt 에 남긴다.
/// </summary>
internal sealed class WindowChrome : IDisposable
{
    /// <summary>
    /// 모니터 확대 배율(윈도 설정 DPI ÷ 96, 정수로 반올림 — 비트맵 글꼴이 뭉개지지 않게) — 제목 줄·차림·설정은 기본 크기로 그려 이만큼 늘린다(사용자 요청: 4K 에서 글이 작다).
    /// </summary>
    public static readonly int S = Math.Clamp((int)Math.Round(Dpi() / 96.0), 1, 4);
    private const int BarH = 28, MenuW = 200, ItemH = 30, ButtonW = 44;
    /// <summary>제목 줄 높이(화소).</summary>
    public static int Height => BarH * S;

    private static uint Dpi()
    {
        try { return Win32.GetDpiForSystem(); } catch (EntryPointNotFoundException) { return 96; }
    }

    /// <summary>고를 수 있는 게임 화면 크기 — 원본 800×600 과 같은 4:3.</summary>
    public static readonly (int W, int H)[] Sizes = [(800, 600), (1024, 768), (1200, 900), (1280, 960), (1440, 1080), (1600, 1200), (1920, 1440), (2560, 1920)];

    private readonly IntPtr _hwnd;
    private readonly Gfx _gfx;
    private readonly ScreenImage _screen;
    private readonly BitmapFont _font = new(0);
    private Image32 _bar = new(1, 1), _panel = new(1, 1);
    private ID3D11Texture2D? _barTex, _panelTex;
    private ID3D11ShaderResourceView? _barView, _panelView;
    private int _mx = -1, _my = -1;

    public bool MenuOpen { get; private set; }
    public bool SettingsOpen { get; private set; }
    /// <summary>차림이나 설정이 떠 있으면 게임은 멈춘다(입력도 안 넘긴다).</summary>
    public bool Blocking => MenuOpen || SettingsOpen;
    public Action? BackToTitle;

    public WindowChrome(IntPtr hwnd, Gfx gfx, ScreenImage screen)
    {
        _hwnd = hwnd; _gfx = gfx; _screen = screen;
    }

    // ── 저장 ──
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch", "display.txt");

    public static (int W, int H, bool Full) LoadSetting()
    {
        try
        {
            var p = File.ReadAllText(FilePath).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 3 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h) && w >= 640 && h >= 480) return (w, h, p[2] == "1");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (1200, 900, false);
    }

    private static void SaveSetting(int w, int h, bool full)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, $"{w} {h} {(full ? 1 : 0)}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Win32.Rect WorkArea()
    {
        if (!Win32.SystemParametersInfoW(Win32.SPI_GETWORKAREA, 0, out var r, 0)) r = new Win32.Rect { Right = 1920, Bottom = 1080 };
        return r;
    }

    /// <summary>작업 영역에 들어가는 크기들(제목 줄 포함).</summary>
    public static IEnumerable<(int W, int H)> FittingSizes()
    {
        var wa = WorkArea();
        return Sizes.Where(s => s.W <= wa.Width && s.H + Height <= wa.Height);
    }

    /// <summary>게임 화면을 w×h 로(창은 제목 줄만큼 더 큼), 작업 영역 가운데에. 전체 화면이면 모니터 전체.</summary>
    public void Apply(int w, int h, bool full, bool save = true)
    {
        if (full)
        {
            int sw = Win32.GetSystemMetrics(0), sh = Win32.GetSystemMetrics(1);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, sw, sh, Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        }
        else
        {
            var wa = WorkArea();
            int ww = Math.Min(w, wa.Width), wh = Math.Min(h + Height, wa.Height);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, wa.Left + Math.Max(0, (wa.Width - ww) / 2), wa.Top + Math.Max(0, (wa.Height - wh) / 2), ww, wh,
                Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
        }
        if (save) SaveSetting(w, h, full);
    }

    // ── 맞히기 ──
    public int HitTest(int x, int y)
    {
        x /= S; y /= S;
        if (y >= BarH) return Win32.HTCLIENT;
        int w = _gfx.BufferWidth / S;
        if (x < ButtonW || x >= w - ButtonW * 2) return Win32.HTCLIENT;   // ☰ · 내리기 · 닫기
        return Win32.HTCAPTION;
    }

    public void MouseMove(int x, int y) { _mx = x / S; _my = y / S; }

    /// <summary>창 안쪽 좌표의 왼쪽 누름 — 차림·설정·단추가 받으면 true(게임으로 안 넘김).</summary>
    public bool Click(int x, int y)
    {
        x /= S; y /= S;
        int w = _gfx.BufferWidth / S;
        if (y < BarH)
        {
            if (x < ButtonW) { MenuOpen = !MenuOpen; SettingsOpen = false; return true; }
            if (x >= w - ButtonW) { Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, 0, 0); return true; }
            if (x >= w - ButtonW * 2) { Win32.ShowWindow(_hwnd, Win32.SW_MINIMIZE); return true; }
            return true;
        }
        int gy = y - BarH;
        if (MenuOpen)
        {
            MenuOpen = false;
            if (x < MenuW && gy < MenuItems.Length * ItemH)
                switch (gy / ItemH)
                {
                    case 0: SettingsOpen = true; break;
                    case 1: BackToTitle?.Invoke(); break;
                    case 2: Win32.PostMessageW(_hwnd, Win32.WM_CLOSE, 0, 0); break;
                }
            return true;
        }
        if (SettingsOpen)
        {
            var (px, py, pw, ph) = PanelRect();
            if (x < px || x >= px + pw || gy < py || gy >= py + ph) { SettingsOpen = false; return true; }
            var rows = SettingRows();
            int row = (gy - py - 44) / ItemH;
            if (row >= 0 && row < rows.Count) { var r = rows[row]; Apply(r.W, r.H, r.Full); }
            else if (gy >= py + ph - 40) SettingsOpen = false;   // 닫기
            return true;
        }
        return false;
    }

    public bool Escape()
    {
        if (!Blocking) return false;
        MenuOpen = SettingsOpen = false;
        return true;
    }

    private static readonly string[] MenuItems = ["설정 (해상도)", "시작 화면으로", "끝내기"];

    private List<(string Label, int W, int H, bool Full)> SettingRows()
    {
        var rows = FittingSizes().Select(s => ($"{s.W} x {s.H}", s.W, s.H, false)).ToList();
        int sw = Win32.GetSystemMetrics(0), sh = Win32.GetSystemMetrics(1);
        rows.Add(($"전체 화면 ({sw} x {sh})", sw, sh - Height, true));
        return rows;
    }

    private (int X, int Y, int W, int H) PanelRect()
    {
        int rows = SettingRows().Count;
        int pw = 300, ph = 44 + rows * ItemH + 48;
        return ((_gfx.Width / S - pw) / 2, Math.Max(8, (_gfx.Height / S - ph) / 2), pw, ph);
    }

    // ── 그리기 ──
    public void Draw()
    {
        int bw = Math.Max(1, _gfx.BufferWidth / S);
        // 제목 줄 — 기본 크기(bw × 28)로 그려 S 배로 늘린다
        if (_bar.Width != bw || _bar.Height != BarH)
        {
            _barView?.Dispose(); _barTex?.Dispose();
            _bar = new Image32(bw, BarH);
            _barTex = _gfx.DynamicTexture(bw, BarH, out _barView);
        }
        _bar.Fill(0, 0, bw, BarH, 24, 21, 18);
        _bar.Fill(0, BarH - 1, bw, 1, 169, 146, 109);
        bool overTitle = _my is >= 0 and < BarH;
        if (overTitle && _mx < ButtonW || MenuOpen) _bar.Fill(0, 0, ButtonW, BarH - 1, 70, 60, 46);
        if (overTitle && _mx >= bw - ButtonW * 2 && _mx < bw - ButtonW) _bar.Fill(bw - ButtonW * 2, 0, ButtonW, BarH - 1, 70, 60, 46);
        if (overTitle && _mx >= bw - ButtonW) _bar.Fill(bw - ButtonW, 0, ButtonW, BarH - 1, 170, 40, 40);
        for (int k = 0; k < 3; k++) _bar.Fill(14, 8 + k * 5, 16, 2, 235, 225, 200);            // ☰
        _bar.Fill(bw - ButtonW * 2 + 15, 14, 14, 2, 235, 225, 200);                            // 내리기 —
        for (int k = 0; k < 10; k++)                                                          // 닫기 ×
        {
            _bar.Fill(bw - ButtonW + 17 + k, 9 + k, 2, 1, 235, 225, 200);
            _bar.Fill(bw - ButtonW + 26 - k, 9 + k, 2, 1, 235, 225, 200);
        }
        _font.Draw(_bar, ButtonW + 10, 7, "마그나카르타 패치판", 235, 225, 200);
        _gfx.UploadTexture(_barTex!, _bar);
        _gfx.BeginChrome();
        _screen.Draw(_barView!, 0, 0, bw * S, BarH * S, Vector4.One, point: true);
        _gfx.EndChrome();

        if (!Blocking) return;
        // 차림·설정 — 게임 화면 위에(게임 화면 좌표)
        int gw = Math.Max(1, _gfx.Width / S), gh = Math.Max(1, _gfx.Height / S);
        if (_panel.Width != gw || _panel.Height != gh)
        {
            _panelView?.Dispose(); _panelTex?.Dispose();
            _panel = new Image32(gw, gh);
            _panelTex = _gfx.DynamicTexture(gw, gh, out _panelView);
        }
        Array.Clear(_panel.Bgra);
        int my = _my - BarH;
        if (MenuOpen)
        {
            _panel.Fill(0, 0, MenuW, MenuItems.Length * ItemH + 2, 169, 146, 109);
            for (int i = 0; i < MenuItems.Length; i++)
            {
                bool hot = _mx is >= 0 and < MenuW && my >= i * ItemH && my < (i + 1) * ItemH;
                _panel.Fill(1, i * ItemH + 1, MenuW - 2, ItemH - 1, (byte)(hot ? 70 : 30), (byte)(hot ? 60 : 27), (byte)(hot ? 46 : 23), 245);
                _font.Draw(_panel, 14, i * ItemH + 8, MenuItems[i], 235, 225, 200);
            }
        }
        if (SettingsOpen)
        {
            _panel.Fill(0, 0, gw, gh, 0, 0, 0, 120);
            var (px, py, pw, ph) = PanelRect();
            _panel.Fill(px - 1, py - 1, pw + 2, ph + 2, 169, 146, 109);
            _panel.Fill(px, py, pw, ph, 28, 25, 21, 250);
            _font.Draw(_panel, px + 16, py + 14, "설정 - 해상도(게임 화면 크기)", 255, 220, 140);
            var cur = LoadSetting();
            var rows = SettingRows();
            for (int i = 0; i < rows.Count; i++)
            {
                int ry = py + 44 + i * ItemH;
                bool hot = _mx >= px && _mx < px + pw && my >= ry && my < ry + ItemH;
                bool on = rows[i].Full ? cur.Full : !cur.Full && rows[i].W == cur.W && rows[i].H == cur.H;
                if (hot) _panel.Fill(px + 8, ry, pw - 16, ItemH - 2, 70, 60, 46);
                _font.Draw(_panel, px + 20, ry + 8, (on ? "> " : "  ") + rows[i].Label, (byte)(on ? 255 : 235), (byte)(on ? 220 : 225), (byte)(on ? 140 : 200));
            }
            _font.Draw(_panel, px + pw - 60, py + ph - 30, "닫기", 235, 225, 200);
        }
        _gfx.UploadTexture(_panelTex!, _panel);
        _screen.Draw(_panelView!, 0, 0, gw * S, gh * S, Vector4.One, point: true);
    }

    public void Dispose() { _barView?.Dispose(); _barTex?.Dispose(); _panelView?.Dispose(); _panelTex?.Dispose(); }
}
