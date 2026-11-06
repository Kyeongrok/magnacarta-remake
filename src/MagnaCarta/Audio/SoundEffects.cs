using System.Runtime.InteropServices;
using MagnaCarta.Data;

namespace MagnaCarta.Audio;

/// <summary>
/// 효과음 — SED\%04d.SED 소리 상자 안의 RIFF WAVE(분석-컷씬 §3.1). 알맹이를 메모리째 PlaySound 로 비동기 재생한다
/// (한 번에 하나 — 겹쳐 트는 것은 아직 없음). SED 0006 은 머리가 깨져 있어 RIFF 를 찾아 쓴다.
/// </summary>
internal static class SoundEffects
{
    [DllImport("winmm.dll")]
    private static extern bool PlaySound(byte[]? data, IntPtr module, uint flags);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    private static readonly Dictionary<int, byte[]?> Cache = [];
    private static byte[]? _current;    // PlaySound 가 끝날 때까지 붙잡아 둔다

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, System.Text.StringBuilder? ret, int retLen, IntPtr callback);
    private static readonly HashSet<string> Loops = [];

    /// <summary>
    /// 필드 소리 물체(fld C 갈래 11 = MCFOT_SOUND, 사건 600/601) — 모델 칸을 SED 번호로 보고(추정) 되풀이 재생. MCI 로 효과음과 겹쳐 튼다.
    /// </summary>
    public static void StartLoop(string key, int number)
    {
        if (Music.Muted || Loops.Contains(key) || !McFiles.Exists("SED", number, "SED")) return;
        var d = McFiles.Read("SED", number, "SED");
        int at = d.AsSpan().IndexOf("RIFF"u8);
        if (at < 0) return;
        string dir = Path.Combine(Path.GetTempPath(), "MagnaCartaPatch");
        Directory.CreateDirectory(dir);
        string wav = Path.Combine(dir, $"sed{number:D4}.wav");
        if (!File.Exists(wav)) File.WriteAllBytes(wav, d.AsSpan(at).ToArray());
        if (mciSendStringW($"open \"{wav}\" type mpegvideo alias {key}", null, 0, IntPtr.Zero) != 0) return;
        mciSendStringW($"setaudio {key} volume to {400 * Volume.Sound / Volume.Max}", null, 0, IntPtr.Zero);
        mciSendStringW($"play {key} repeat", null, 0, IntPtr.Zero);
        Loops.Add(key);
    }

    public static bool IsLooping(string key) => Loops.Contains(key);

    /// <summary>MCI 소리 크기 0~1000.</summary>
    public static void SetLoopVolume(string key, int volume)
    {
        if (Loops.Contains(key)) mciSendStringW($"setaudio {key} volume to {Math.Clamp(volume, 0, 1000) * Volume.Sound / Volume.Max}", null, 0, IntPtr.Zero);
    }

    public static void StopLoop(string key)
    {
        if (!Loops.Remove(key)) return;
        mciSendStringW($"close {key}", null, 0, IntPtr.Zero);
    }

    public static void StopAllLoops()
    {
        foreach (var k in Loops.ToList()) StopLoop(k);
    }

    private static readonly string? SoundLog = Environment.GetEnvironmentVariable("MC_SOUND_LOG") is { Length: > 0 } l ? l : null;

    public static void Play(int number)
    {
        if (SoundLog != null) File.AppendAllText(SoundLog, $"SED {number}" + Environment.NewLine);   // 확인용(소리를 못 듣는 확인)
        if (Music.Muted || number < 0) return;
        if (!Cache.TryGetValue(number, out var wav))
        {
            wav = null;
            if (McFiles.Exists("SED", number, "SED"))
            {
                var d = McFiles.Read("SED", number, "SED");
                int at = d.AsSpan().IndexOf("RIFF"u8);
                if (at >= 0) wav = d.AsSpan(at).ToArray();   // MP3 든 것(갈래 1)은 PlaySound 가 못 튼다 — RIFF 만
            }
            Cache[number] = wav;
        }
        if (wav == null || Volume.Sound <= 0) return;
        _current = Volume.ScaleWav(wav, Volume.Sound);
        PlaySound(_current, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
    }
}
