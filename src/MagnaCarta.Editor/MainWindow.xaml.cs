using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MagnaCarta.Editor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Info.Text = $"원본 자료: {Data.McFiles.Dir}\n게임: {GameExe() ?? "(못 찾음 — 게임을 먼저 빌드)"}";
    }

    private void OpenCutscene(object sender, RoutedEventArgs e) => new CutsceneWindow { Owner = this }.Show();

    private void OpenEvents(object sender, RoutedEventArgs e) => new EventsWindow { Owner = this }.Show();

    private void RunGame(object sender, RoutedEventArgs e) => Launch(null);

    /// <summary>게임 실행 파일 — 편집기와 같은 폴더(프로젝트 참조로 같이 나옴) 또는 저장소의 게임 bin.</summary>
    internal static string? GameExe()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "MagnaCarta.exe");
        if (File.Exists(here)) return here;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "MagnaCarta"))) dir = dir.Parent;
        var bin = dir == null ? null : Path.Combine(dir.FullName, "src", "MagnaCarta", "bin", "Debug", "net8.0-windows", "MagnaCarta.exe");
        return bin != null && File.Exists(bin) ? bin : null;
    }

    internal static void Launch(string? args, Dictionary<string, string>? env = null)
    {
        if (GameExe() is not { } exe) { MessageBox.Show("게임 실행 파일을 못 찾았습니다. 게임을 먼저 빌드하세요."); return; }
        var psi = new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        Process.Start(psi);
    }
}
