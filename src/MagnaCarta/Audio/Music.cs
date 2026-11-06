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

    /// <summary>사건 610/611 의 배경음 페이드 배율(0~1) — 원본 소리 체계 +4(0x52004f3e, +8 이하로). 새 곡을 틀면 1 로 되돌린다(패치판 — 원본이 언제 되돌리는지는 못 봄).</summary>
    public static float Fade { get; private set; } = 1;

    public static void SetFade(float v)
    {
        Fade = Math.Clamp(v, 0, 1);
        if (_playing >= 0) mciSendStringW($"setaudio bgm volume to {(int)(Volume.Bgm * Fade)}", null, 0, IntPtr.Zero);
    }

    public static void Play(int number)
    {
        // 같은 곡이 이미 울리면 다시 틀지 않되, 611/613 으로 줄인 페이드는 되돌린다(같은 곡 필드로 옮길 때 소리가 안 돌아오는 일을 막음)
        if (number == _playing) { if (Fade < 1) SetFade(1); return; }
        Stop();
        Fade = 1;
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
            mciSendStringW($"setaudio bgm volume to {(int)(Volume.Bgm * Fade)}", null, 0, IntPtr.Zero);
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
            mciSendStringW($"setaudio voice volume to {Volume.Voice}", null, 0, IntPtr.Zero);
            mciSendStringW("play voice", null, 0, IntPtr.Zero);
            _voice = true;
        }
        catch (IOException) { }
    }

    /// <summary>AUDIO 창에서 바꾼 크기를 울리는 중인 배경음 · 목소리에 건다.</summary>
    public static void ApplyVolume()
    {
        if (_playing >= 0) mciSendStringW($"setaudio bgm volume to {(int)(Volume.Bgm * Fade)}", null, 0, IntPtr.Zero);
        if (_voice) mciSendStringW($"setaudio voice volume to {Volume.Voice}", null, 0, IntPtr.Zero);
    }

    public static void StopVoice()
    {
        if (!_voice) return;
        mciSendStringW("close voice", null, 0, IntPtr.Zero);
        _voice = false;
    }

    private static int _playing2 = -1;

    /// <summary>음악 1번 채널(사건 607) — 0번 채널(배경음)과 따로 겹쳐 튼다(분석-컷씬). 되풀이는 0번과 같게.</summary>
    public static void Play2(int number)
    {
        if (number == _playing2) return;
        Stop2();
        if (Muted || number < 0 || !McFiles.Exists("SMD", number, "SMD")) return;
        try
        {
            Directory.CreateDirectory(Cache);
            string mp3 = Path.Combine(Cache, $"smd{number:D4}.mp3");
            if (!File.Exists(mp3))
            {
                var d = McFiles.Read("SMD", number, "SMD");
                File.WriteAllBytes(mp3, d.AsSpan(PayloadOffset(d)).ToArray());
            }
            if (mciSendStringW($"open \"{mp3}\" type mpegvideo alias bgm2", null, 0, IntPtr.Zero) != 0) return;
            mciSendStringW($"setaudio bgm2 volume to {Volume.Bgm}", null, 0, IntPtr.Zero);
            mciSendStringW("play bgm2 repeat", null, 0, IntPtr.Zero);
            _playing2 = number;
        }
        catch (IOException) { }
    }

    /// <summary>음악 1번 채널 끄기(사건 608).</summary>
    public static void Stop2()
    {
        if (_playing2 < 0) return;
        mciSendStringW("close bgm2", null, 0, IntPtr.Zero);
        _playing2 = -1;
    }

    public static void Stop()
    {
        if (_playing < 0) return;
        mciSendStringW("close bgm", null, 0, IntPtr.Zero);
        _playing = -1;
    }
}
