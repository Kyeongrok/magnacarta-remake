using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace MagnaCarta.Scenes;

/// <summary>
/// 버그 리포트 — 어느 화면에서나 F12 로 글 쓰는 창을 띄우고, 쓴 글을 통계 worker(<c>war-of-genesis-stats</c> 의 <c>POST /v1/bug?game=mc</c>)로 보낸다.
/// 대시보드의 「마그나카르타」 탭에서 본다. 창세기전3 리메이크(duel-dx/System/BugReport.cs)와 같은 꼴을 줄인 것.
/// </summary>
/// <remarks>
/// 창은 WinForms 여러 줄 글 칸이라 한글 IME · 붙여 넣기가 그대로 된다. 게임이 붙이는 것: 판 번호 · OS · 32/64비트 · 화면 크기 · 지금 장면 · 오류 기록 끝.
/// 사용자 이름 · 전체 경로는 싣지 않는다. 못 보내면 줄(<see cref="QueueFolder"/>)에 두었다가 다음에 켤 때 다시 보낸다.
/// 시험(--script)은 <c>MC_STATS_URL</c> 을 줄 때만 실제로 보내고, <c>MC_BUGREPORT_TEXT</c> 가 있으면 창 없이 그 글을 보낸다.
/// </remarks>
internal static class BugReport
{
    internal const string DefaultUrl = "https://war-of-genesis-stats.oceanfog1.workers.dev";
    private const int MaxText = 8000, LogTailBytes = 16 * 1024;

    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch");
    internal static string QueueFolder => Path.Combine(DataFolder, "bug-queue");

    /// <summary>판 번호 — 릴리즈 빌드가 태그로 넣은 것(InformationalVersion), 없으면 0.0.0.</summary>
    internal static string AppVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>설치마다 한 번 만드는 무작위 번호(32 글자 16진) — 받는 쪽이 설치 하나의 수를 센다.</summary>
    internal static string InstallId()
    {
        var file = Path.Combine(DataFolder, "install.txt");
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: 32 } id) return id;
            Directory.CreateDirectory(DataFolder);
            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, fresh);
            return fresh;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Guid.NewGuid().ToString("N"); }
    }

    private static string Url(bool test)
    {
        var over = Environment.GetEnvironmentVariable("MC_STATS_URL")?.Trim() ?? "";
        if (over.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || over.StartsWith("http://127.0.0.1") || over.StartsWith("http://localhost")) return over;
        return test ? "" : DefaultUrl;   // 시험이 진짜 받는 곳에 글을 남기지 않게
    }

    /// <summary>F12 — 글 쓰는 창. <paramref name="where"/> 는 지금 장면(「필드 594」 등).</summary>
    public static void Open(IntPtr owner, string where, int width, int height, bool test)
    {
        var context = Context(where, width, height);
        if (Environment.GetEnvironmentVariable("MC_BUGREPORT_TEXT") is { Length: > 0 } scripted) { Submit(scripted, context, test); return; }
        if (test) return;
        var win = new NativeWindow();
        win.AssignHandle(owner);
        try { if (Ask(win, Summary(context)) is { } text) Submit(text, context, test); }
        finally { win.ReleaseHandle(); }
    }

    private static Dictionary<string, object?> Context(string where, int width, int height)
    {
        var c = new Dictionary<string, object?>
        {
            ["where"] = where,
            ["os"] = Environment.OSVersion.VersionString,
            ["bits"] = Environment.Is64BitProcess ? 64 : 32,
            ["screen"] = $"{width}x{height}",
            ["movies"] = !Environment.Is64BitProcess,
        };
        // 오류 기록의 끝 — 사용자 폴더 경로는 지운다
        var crash = Path.Combine(AppContext.BaseDirectory, "crash.txt");
        try
        {
            if (File.Exists(crash))
            {
                var bytes = File.ReadAllBytes(crash);
                var tail = Encoding.UTF8.GetString(bytes, Math.Max(0, bytes.Length - LogTailBytes), Math.Min(bytes.Length, LogTailBytes));
                c["crash"] = tail.Replace(Environment.UserName, "<user>");
            }
        }
        catch (IOException) { }
        return c;
    }

    private static string Summary(Dictionary<string, object?> c) =>
        $"같이 보내는 것: 판 {AppVersion} · {c["where"]} · {c["bits"]}비트 · 화면 {c["screen"]}" + (c.ContainsKey("crash") ? " · 오류 기록 끝" : "");

    private static string? Ask(IWin32Window owner, string summary)
    {
        using var form = new Form
        {
            Text = "마그나카르타 패치판 — 버그 리포트", Width = 560, Height = 380, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
        };
        var label = new Label { Text = "무엇이 잘못됐는지 적어 주세요(어디서 · 무엇을 하다가 · 어떻게 됐는지).", Left = 12, Top = 10, Width = 520 };
        var box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Left = 12, Top = 34, Width = 520, Height = 220, MaxLength = MaxText, AcceptsReturn = true };
        var info = new Label { Text = summary, Left = 12, Top = 262, Width = 520, Height = 34, ForeColor = System.Drawing.Color.DimGray };
        var send = new Button { Text = "보내기", Left = 352, Top = 300, Width = 85, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", Left = 447, Top = 300, Width = 85, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([label, box, info, send, cancel]);
        form.AcceptButton = null; form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }

    /// <summary>글을 줄에 세우고 뒤에서 보낸다.</summary>
    private static void Submit(string text, Dictionary<string, object?> context, bool test)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxText) text = text[..MaxText];
        try
        {
            Directory.CreateDirectory(QueueFolder);
            var body = new { v = 1, install = InstallId(), version = AppVersion, text, context };
            File.WriteAllText(Path.Combine(QueueFolder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(body));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        UploadQueued(test);
    }

    private static int _uploading;

    /// <summary>줄에 선 리포트를 뒤에서 보낸다(켤 때와 보낸 뒤). 받은 것 · 받는 쪽이 못 쓴다고 한 것(4xx, 429 빼고)은 지우고 나머지는 다음에.</summary>
    public static void UploadQueued(bool test)
    {
        var url = Url(test);
        if (url.Length == 0 || !Directory.Exists(QueueFolder)) return;
        if (Interlocked.Exchange(ref _uploading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                foreach (var file in Directory.GetFiles(QueueFolder, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        using var content = new StringContent(File.ReadAllText(file), Encoding.UTF8, "application/json");
                        using var res = await http.PostAsync(url.TrimEnd('/') + "/v1/bug?game=mc", content);
                        int code = (int)res.StatusCode;
                        if (res.IsSuccessStatusCode || (code >= 400 && code < 500 && code != 429)) File.Delete(file);
                        else break;
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { break; }
                }
            }
            finally { Interlocked.Exchange(ref _uploading, 0); }
        });
    }
}
