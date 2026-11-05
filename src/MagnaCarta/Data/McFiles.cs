using System.Buffers.Binary;
using System.Text;

namespace MagnaCarta.Data;

/// <summary>원본 게임 폴더. 환경 변수 <c>MC_DIR</c> 로 바꾼다. 파일은 읽기만 한다.</summary>
internal static class McFiles
{
    public const string DefaultDir = @"C:\Users\ocean\Desktop\ETC\DGGL\Games\MagCarta_Win_230428\Store";

    /// <summary>원본 게임 폴더: MC_DIR → 실행 파일이 있는 폴더(원본 폴더에 넣고 돌릴 때, 3DOBS 가 있으면) → 개발 기계 기본값.</summary>
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("MC_DIR") is { Length: > 0 } d ? d
        : Directory.Exists(Path.Combine(AppContext.BaseDirectory, "3DOBS")) ? AppContext.BaseDirectory
        : DefaultDir;

    public static string PathOf(string folder, int number, string ext) => Path.Combine(Dir, folder, $"{number:D4}.{ext}");

    public static byte[] Read(string folder, int number, string ext) => File.ReadAllBytes(PathOf(folder, number, ext));

    public static bool Exists(string folder, int number, string ext) => File.Exists(PathOf(folder, number, ext));

    static McFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Cp949 => Encoding.GetEncoding(949);
}

/// <summary>리틀 엔디언 차례 읽개. 원본 <c>Cfread</c> 처럼 파일 끝 뒤는 0 으로 본다.</summary>
internal sealed class Reader(byte[] data, int offset = 0)
{
    public readonly byte[] Data = data;
    public int Pos = offset;
    public bool End => Pos >= Data.Length;
    public int Remaining => Data.Length - Pos;

    public uint U32()
    {
        uint v = Pos + 4 <= Data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos)) : 0;
        Pos += 4;
        return v;
    }

    public int I32() => unchecked((int)U32());

    public ushort U16()
    {
        ushort v = Pos + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos)) : (ushort)0;
        Pos += 2;
        return v;
    }

    public float F32() => BitConverter.Int32BitsToSingle(I32());

    public System.Numerics.Vector3 Vec3() => new(F32(), F32(), F32());

    public System.Numerics.Matrix4x4 Mat()
    {
        // 파일은 행 우선 D3D 꼴(넷째 행 = 이동) — System.Numerics 와 같은 배치다
        return new(F32(), F32(), F32(), F32(), F32(), F32(), F32(), F32(),
                   F32(), F32(), F32(), F32(), F32(), F32(), F32(), F32());
    }

    public void Skip(int n) => Pos += n;

    /// <summary>개수 하나. 남은 바이트에 <paramref name="unit"/> 바이트씩 다 못 담으면 자료가 어긋난 것.</summary>
    public int Count(int unit = 1)
    {
        uint n = U32();
        if ((long)n * unit > Remaining) throw new InvalidDataException($"개수 {n} 가 너무 큼 @0x{Pos - 4:X}");
        return (int)n;
    }
}
