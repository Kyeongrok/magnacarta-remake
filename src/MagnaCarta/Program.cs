namespace MagnaCarta;

internal static class Program
{
    // DPI 인식(PerMonitorV2)은 app.manifest 가 켠다.
    [STAThread]
    private static void Main(string[] args)
    {
        // --script "wait:1;key:enter;wait:3;hold:w:2;shot:a.png;quit" — 사람 손 없이 돌려 보고 화면을 찍는다(확인용)
        string? script = null;
        int field = -1;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--script") script = args[i + 1];
            if (args[i] == "--field") field = int.Parse(args[i + 1]);   // 그 필드에서 바로 시작(확인용)
        }
        if (!Directory.Exists(Data.McFiles.Dir))
        {
            System.Windows.Forms.MessageBox.Show($"원본 게임 폴더가 없습니다:\n{Data.McFiles.Dir}\n\nMagnaCarta.exe 를 원본 마그나카르타 폴더(3DOBS·MAP 등이 있는 곳)에 넣고 실행하거나,\n환경 변수 MC_DIR 로 그 폴더를 알려 주세요.", "마그나카르타 패치판");
            return;
        }
        try
        {
            using var window = new GameWindow(script, args.Contains("--battle"), field);
            window.Run();
        }
        catch (Exception e)
        {
            // 창 프로그램이라 콘솔이 없다 — 실행 폴더에 남기고 알린다
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.txt"), e.ToString());
            if (script == null)
                System.Windows.Forms.MessageBox.Show(e.Message + Environment.NewLine + Environment.NewLine + Path.Combine(AppContext.BaseDirectory, "crash.txt"), "마그나카르타 패치판 — 오류");
        }
    }
}
