using System.Globalization;
using System.Numerics;

namespace MagnaCarta.Data;

/// <summary>
/// 사건 카메라 컷(fld F 묶음, 명령 500) 다듬기 — 원본 파일은 그대로 두고 패치판이 덮어쓴다(사용자 요청: 프롤로그 카메라가 어지럽다).
/// 컷마다 재생 속도 · 부드럽게(키 이동 평균 반지름 0~1) · 시야각(0 = 원본). 게임 안 F7 컷 조정 창이 고치고 저장한다.
/// 읽는 곳: 게임 폴더 <c>cuts.txt</c>(릴리즈에 넣는 기본값) 다음 %LOCALAPPDATA%\MagnaCartaPatch\cuts.txt(사용자가 고친 값, 앞의 것을 덮음).
/// 줄 꼴: <c>필드 컷 속도 부드럽게 시야각</c>.
/// </summary>
internal static class CutTuning
{
    public record struct Tune(float Speed, float Smooth, float Fov)
    {
        public static readonly Tune Default = new(1, 0, 0);
        public readonly bool IsDefault => Speed == 1 && Smooth == 0 && Fov == 0;
    }

    private static readonly Dictionary<(int Field, int Cut), Tune> Table = Load();

    private static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnaCartaPatch", "cuts.txt");
    private static string AppPath => Path.Combine(AppContext.BaseDirectory, "cuts.txt");

    private static Dictionary<(int, int), Tune> Load()
    {
        var t = new Dictionary<(int, int), Tune>();
        foreach (var path in new[] { AppPath, UserPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 5 || p[0].StartsWith('#')) continue;
                    if (int.TryParse(p[0], out int f) && int.TryParse(p[1], out int c)
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sp)
                        && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float sm)
                        && float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                        t[(f, c)] = new Tune(Math.Clamp(sp, 0.1f, 4f), Math.Clamp(sm, 0, 1), Math.Clamp(fv, 0, 120));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return t;
    }

    public static Tune Get(int field, int cut) => Table.GetValueOrDefault((field, cut), Tune.Default);

    public static void Set(int field, int cut, Tune tune)
    {
        if (tune.IsDefault) Table.Remove((field, cut)); else Table[(field, cut)] = tune;
    }

    /// <summary>사용자 파일에 지금 표를 통째로 쓴다(게임 폴더 기본값과 같은 줄도 같이 — 단순하게).</summary>
    public static string Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
            var lines = new List<string> { "# 필드 컷 속도 부드럽게 시야각 — 패치판 F7 컷 조정 창이 씀" };
            foreach (var ((f, c), v) in Table.OrderBy(kv => kv.Key))
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{f} {c} {v.Speed:0.##} {v.Smooth:0.##} {v.Fov:0.#}"));
            File.WriteAllLines(UserPath, lines);
            return UserPath;
        }
        catch (IOException e) { return "저장 못 함: " + e.Message; }
        catch (UnauthorizedAccessException e) { return "저장 못 함: " + e.Message; }
    }

    /// <summary>원본 컷에 부드럽게·시야각을 입힌 컷. 부드럽게 = 키 자리·방향을 앞뒤 (부드럽게 × 키 수 / 4)개 이동 평균.</summary>
    public static CameraCut Apply(CameraCut cut, Tune t)
    {
        if (t.Smooth <= 0 && t.Fov <= 0) return cut;
        var keys = cut.Keys;
        int r = (int)MathF.Round(t.Smooth * Math.Max(1, keys.Length) / 4f);
        var outKeys = new CameraKey[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            Vector3 eye = Vector3.Zero, fwd = Vector3.Zero, up = Vector3.Zero;
            float wsum = 0;
            for (int j = Math.Max(0, i - r); j <= Math.Min(keys.Length - 1, i + r); j++)
            {
                float w = r == 0 ? 1 : 1 - MathF.Abs(j - i) / (r + 1f);   // 세모 가중치
                eye += keys[j].Eye * w; fwd += keys[j].Forward * w; up += keys[j].Up * w; wsum += w;
            }
            eye /= wsum; fwd = Vector3.Normalize(fwd); up = Vector3.Normalize(up);
            // 행렬로 되돌림(자리 = 행3, 앞 = −행2, 위 = 행1, 오른쪽 = 행0)
            var right = Vector3.Normalize(Vector3.Cross(up, -fwd));
            up = Vector3.Normalize(Vector3.Cross(-fwd, right));
            var m = new Matrix4x4(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, -fwd.X, -fwd.Y, -fwd.Z, 0, eye.X, eye.Y, eye.Z, 1);
            outKeys[i] = keys[i] with { M = m, Fov = t.Fov > 0 ? t.Fov : keys[i].Fov };
        }
        return cut with { Keys = outKeys };
    }
}
