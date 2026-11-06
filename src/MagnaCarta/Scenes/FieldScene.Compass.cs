using System.Numerics;
using MagnaCarta.Data;

namespace MagnaCarta.Scenes;

/// <summary>
/// 나침반 — 원본 MCFldDealer 의 m_pCompassPlane(0x52070810). 2D 마디의 자식 id 가 FrameData 번호다:
/// 0x88 = FrameData 136(둥근 판 128×128, 「N」, BGR 234) 을 (W &lt; 700 ? 540 : 700, 0) 에,
/// 0x89 = FrameData 137(OBS 216 주황 화살, 12×17, 중심 (−6, −9)) 을 판 위 (+48, 47) 에.
/// 화살은 탐색 틱(0x52070b79)마다 「주인공 → 목표」 각 acos(−dy/|d|)(dx &lt; 0 이면 부호 바꿈) − 주인공 yaw(+0x104) 로 돈다.
/// 목표 = 필드 물체 갈래 4 의 자리(들기 0x52069a4d → 장면 +0x1714), 없으면 (0, 1e8, 0) — 아주 먼 북쪽.
/// 사건 명령 15 [물체 id] 는 깜빡이(FrameData 138 = OBS 217, 8장)를 그 물체에 잇는다(0x52070e30): 이을 때 거리 |d0| 로 비율 k = 25 / |d0| 를 정해
/// 판 가운데에서 목표 쪽으로 |d|·k 화소(처음엔 25, 다가가면 줄어듦). 사건 중(모드 2)에는 판·화살·깜빡이를 모두 숨긴다(0x52070fa1).
/// </summary>
internal sealed partial class FieldScene
{
    private Vector3 _compassTarget = new(0, 1e8f, 0);
    private static (Image32 Img, int X, int Y)? _compassArrow;
    private static List<(Image32 Img, int X, int Y)>? _compassBlip;
    private Vector3? _compassLink;
    private float _compassLinkScale;

    /// <summary>사건 명령 15 — 깜빡이를 물체 id 의 자리에 잇는다.</summary>
    private void LinkCompass(int objectId)
    {
        if (_field.Objects.Find(o => o.Id == objectId) is not { } o) return;
        float d = Vector2.Distance(new(o.Position.X, o.Position.Y), new(_pos.X, _pos.Y));
        _compassLink = o.Position;
        _compassLinkScale = d > 1 ? 25f / d : 0;
    }

    private static List<(Image32 Img, int X, int Y)> CompassBlip()
    {
        if (_compassBlip != null) return _compassBlip;
        _compassBlip = [];
        try
        {
            if (!McFiles.Exists("OBS", 217, "OBS")) return _compassBlip;
            var motions = ObsSprite.Decode(McFiles.Read("OBS", 217, "OBS"));
            foreach (var f in motions.Count > 0 ? motions[0].Frames : [])
            {
                var img = new Image32(f.Width, f.Height);
                Buffer.BlockCopy(f.Bgra, 0, img.Bgra, 0, Math.Min(f.Bgra.Length, img.Bgra.Length));
                _compassBlip.Add((img, f.X, f.Y));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        return _compassBlip;
    }

    private void InitCompass()
    {
        if (_field.Objects.Find(o => o.Kind == 4) is { } t) _compassTarget = t.Position;
    }

    private static (Image32 Img, int X, int Y)? CompassArrow()
    {
        if (_compassArrow != null) return _compassArrow;
        try
        {
            if (!McFiles.Exists("OBS", 216, "OBS")) return null;
            // 움직임 목록이 비어 있는 꼴이라 첫 그림을 바로 읽는다
            if (ObsSprite.DecodeFirstFrame(McFiles.Read("OBS", 216, "OBS")) is not { } f) return null;
            var img = new Image32(f.Width, f.Height);
            Buffer.BlockCopy(f.Bgra, 0, img.Bgra, 0, Math.Min(f.Bgra.Length, img.Bgra.Length));
            _compassArrow = (img, f.X, f.Y);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { }
        return _compassArrow;
    }

    private void DrawCompass()
    {
        // 원본 화면(640 또는 800 폭) 좌표를 패치판 창에 — 800×600 기준으로 줄여 맞춘다
        float sc = Math.Min(_ui.Width / 800f, _ui.Height / 600f);
        int ox = (int)((_ui.Width - 800 * sc) / 2);
        int P(float v) => (int)(v * sc);
        var plate = Frame(136);
        if (plate.Width > 1) BlendScaled(_ui.Image, plate, ox + P(700), P(0), Math.Max(1, P(plate.Width)), Math.Max(1, P(plate.Height)), 0, 0, plate.Width, plate.Height);
        if (CompassArrow() is not { } arrow) return;
        float dx = _compassTarget.X - _pos.X, dy = _compassTarget.Y - _pos.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        float ang = len > 0 ? MathF.Acos(Math.Clamp(-dy / len, -1, 1)) : 0;
        if (dx < 0) ang = -ang;
        ang -= _yaw;
        RotateBlend(_ui.Image, arrow.Img, ox + P(748), P(47), -arrow.X, -arrow.Y, ang, sc);
        if (_compassLink is { } link && CompassBlip() is { Count: > 0 } blip)
        {
            float lx = link.X - _pos.X, ly = link.Y - _pos.Y, ll = MathF.Sqrt(lx * lx + ly * ly);
            float la = ll > 0 ? MathF.Acos(Math.Clamp(-ly / ll, -1, 1)) : 0;
            if (lx < 0) la = -la;
            la -= _yaw;
            float r = ll * _compassLinkScale;
            var fr = blip[(int)(Environment.TickCount64 / 66) % blip.Count];   // 두 틀(33ms)에 한 장(추정)
            float bx = 748 + MathF.Sin(la) * r, by = 47 - MathF.Cos(la) * r;
            BlendScaled(_ui.Image, fr.Img, ox + P(bx + fr.X), P(by + fr.Y), Math.Max(1, P(fr.Img.Width)), Math.Max(1, P(fr.Img.Height)), 0, 0, fr.Img.Width, fr.Img.Height);
        }
    }

    /// <summary>src 를 (cx, cy) 를 축으로 돌려 얹는다 — src 안의 축은 (px, py). 가장 가까운 화소, 알파 섞기.</summary>
    private static void RotateBlend(Image32 dst, Image32 src, int cx, int cy, int px, int py, float angle, float scale)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        int r = (int)MathF.Ceiling(MathF.Sqrt(src.Width * src.Width + src.Height * src.Height) * scale) + 1;
        var tmp = new Image32(2 * r, 2 * r);
        for (int y = 0; y < 2 * r; y++)
            for (int x = 0; x < 2 * r; x++)
            {
                // 화면 → 그림: 거꾸로 돌리고 줄임을 푼다(화면 y 는 아래로)
                float fx = (x - r) / scale, fy = (y - r) / scale;
                int sx = (int)MathF.Floor(c * fx + s * fy) + px, sy = (int)MathF.Floor(-s * fx + c * fy) + py;
                if (sx < 0 || sy < 0 || sx >= src.Width || sy >= src.Height) continue;
                Buffer.BlockCopy(src.Bgra, (sy * src.Width + sx) * 4, tmp.Bgra, (y * 2 * r + x) * 4, 4);
            }
        dst.Blend(tmp, cx - r, cy - r);
    }
}
