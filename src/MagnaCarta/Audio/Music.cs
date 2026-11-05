using System.Runtime.InteropServices;
using System.Text;
using MagnaCarta.Data;

namespace MagnaCarta.Audio;

/// <summary>
/// 배경음 — SMD\%04d.SMD 는 Softmax 소리 상자(머리 19 93 12 25 [+ "SoftMax(C) 2001, SED File…" 0x34] + 0x34) 안의 MP3 다(분석-컷씬 §3.1).
/// 알맹이를 임시 폴더에 .mp3 로 풀어 MCI(winmm)로 되풀이해 튼다. 없는 번호는 조용히 건너뛴다(원본은 없는 음악을 부르는 필드가 있다).
/// </summary>
internal static class Music
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, StringBuilder? ret, int retLen, IntPtr callback);

    private static int _playing = -1;
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "MagnaCartaPatch");

    public static bool Muted { get; set; } = Environment.GetEnvironmentVariable("MC_MUTE") is { Length: > 0 };

    /// <summary>소리 상자에서 알맹이(WAV/MP3) 자리. 긴 머리면 0x6C, 짧으면 0x38.</summary>
    public static int PayloadOffset(byte[] d) =>
        d.Length > 0x40 && d.AsSpan(4, 7).SequenceEqual("SoftMax"u8) ? 0x6C : 0x38;

    public static void Play(int number)
    {
        if (number == _playing) return;
        Stop();
        if (Muted || number < 0 || !McFiles.Exists("SMD", number, "SMD")) return;
        try
        {
            Directory.CreateDirectory(Cache);
            string mp3 = Path.Combine(Cache, $"smd{number:D4}.mp3");
            if (!File.Exists(mp3))
            {
                var d = McFiles.Read("SMD", number, "SMD");
                int at = PayloadOffset(d);
                File.WriteAllBytes(mp3, d.AsSpan(at).ToArray());
            }
            if (mciSendStringW($"open \"{mp3}\" type mpegvideo alias bgm", null, 0, IntPtr.Zero) != 0) return;
            mciSendStringW("play bgm repeat", null, 0, IntPtr.Zero);
            _playing = number;
        }
        catch (IOException) { }
    }

    private static bool _voice;

    /// <summary>목소리 SVD\%04d.SVD (같은 소리 상자, MP3) — 한 번 튼다.</summary>
    public static void Voice(int number)
    {
        StopVoice();
        if (Muted || number < 0 || !McFiles.Exists("SVD", number, "SVD")) return;
        try
        {
            Directory.CreateDirectory(Cache);
            string mp3 = Path.Combine(Cache, $"svd{number:D4}.mp3");
            if (!File.Exists(mp3))
            {
                var d = McFiles.Read("SVD", number, "SVD");
                File.WriteAllBytes(mp3, d.AsSpan(PayloadOffset(d)).ToArray());
            }
            if (mciSendStringW($"open \"{mp3}\" type mpegvideo alias voice", null, 0, IntPtr.Zero) != 0) return;
            mciSendStringW("play voice", null, 0, IntPtr.Zero);
            _voice = true;
        }
        catch (IOException) { }
    }

    public static void StopVoice()
    {
        if (!_voice) return;
        mciSendStringW("close voice", null, 0, IntPtr.Zero);
        _voice = false;
    }

    public static void Stop()
    {
        if (_playing < 0) return;
        mciSendStringW("close bgm", null, 0, IntPtr.Zero);
        _playing = -1;
    }
}
