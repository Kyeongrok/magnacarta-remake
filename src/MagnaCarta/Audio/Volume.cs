namespace MagnaCarta.Audio;

/// <summary>
/// 소리 크기 — 원본 AUDIO SETTINGS 창(FrameData 130)의 BGM · SOUND · VOICE 세 줄. 0(OFF)~1000(MAX).
/// 원본은 0~100(손잡이를 놓은 마우스 x − 줄 시작, 0x520813c3)을 소리 체계(0x520f2204 +0x10/+0x18/…)에 두고, 끌 때 레지스트리
/// HKLM\SOFTWARE\Softmax\MagnaCarta 의 「MCSetting」(0x34 바이트 덩어리 +0x24/+0x28/+0x2c)에 남긴다. 값이 없으면(음수) ValueData 0x27~0x29 = 70·100·75.
/// 패치판은 ×10 해 0~1000 으로 %LOCALAPPDATA%\MagnaCartaPatch\audio.txt 에 세 수를 한 줄씩 둔다.
/// </summary>
internal static class Volume
{
    public const int Max = 1000;
    public static readonly int[] Levels = Load();   // 0 BGM · 1 SOUND · 2 VOICE

    public static int Bgm => Levels[0];
    public static int Sound => Levels[1];
    public static int Voice => Levels[2];

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch", "audio.txt");

    private static int[] Load()
    {
        var v = new[] { 700, 1000, 750 };   // 원본 기본값 BGM 70 · SOUND 100 · VOICE 75 (ValueData 0x27~0x29)
        try
        {
            if (File.Exists(FilePath))
            {
                var lines = File.ReadAllLines(FilePath);
                for (int k = 0; k < 3 && k < lines.Length; k++)
                    if (int.TryParse(lines[k].Trim(), out int n)) v[k] = Math.Clamp(n, 0, Max);
            }
        }
        catch (IOException) { }
        return v;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, Levels.Select(l => l.ToString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>바꾼 크기를 지금 울리는 배경음 · 목소리에 바로 건다.</summary>
    public static void Apply()
    {
        Music.ApplyVolume();
    }

    /// <summary>PlaySound 는 크기를 못 받으니 WAV 알맹이(PCM 8/16비트)를 직접 줄인다. 다른 꼴이나 MAX 면 그대로.</summary>
    public static byte[] ScaleWav(byte[] wav, int level)
    {
        if (level >= Max) return wav;
        int fmt = wav.AsSpan().IndexOf("fmt "u8), data = wav.AsSpan().IndexOf("data"u8);
        if (fmt < 0 || data < 0 || fmt + 24 > wav.Length || data + 8 > wav.Length) return wav;
        int format = BitConverter.ToInt16(wav, fmt + 8), bits = BitConverter.ToInt16(wav, fmt + 22);
        if (format != 1) return wav;
        var o = (byte[])wav.Clone();
        int start = data + 8, end = Math.Min(o.Length, start + BitConverter.ToInt32(wav, data + 4));
        if (bits == 16)
            for (int i = start; i + 1 < end; i += 2)
            {
                int s = BitConverter.ToInt16(o, i) * level / Max;
                o[i] = (byte)s; o[i + 1] = (byte)(s >> 8);
            }
        else if (bits == 8)
            for (int i = start; i < end; i++) o[i] = (byte)(128 + (o[i] - 128) * level / Max);
        return o;
    }
}
