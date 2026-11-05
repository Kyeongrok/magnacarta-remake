namespace MagnaCarta.Render;

/// <summary>HLSL 한 묶음. 행렬은 System.Numerics 꼴(행벡터) 그대로 올리므로 row_major 로 받는다.</summary>
internal static class Shaders
{
    public const string Source = """
cbuffer Constants : register(b0)
{
    row_major float4x4 ViewProjection;
    row_major float4x4 World;
    float4 Rect;
    float4 Tint;
    float4 LightDir;
    float4 LightColor;
    float4 Ambient;
    float4 Fog;
    float4 FogColor;
    float4 CameraPos;
};

Texture2D Tex0 : register(t0);
Texture2D Tex1 : register(t1);
SamplerState Wrap : register(s0);
SamplerState Clamp : register(s1);
SamplerState PointClamp : register(s2);

// ── 화면 그림(시작 화면 등): 정점 버퍼 없이 네모 하나 ──────────────────────
struct ScreenOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

ScreenOut ScreenVS(uint id : SV_VertexID)
{
    float2 c = float2((id == 1 || id == 4 || id == 5) ? 1 : 0, (id == 2 || id == 3 || id == 5) ? 1 : 0);
    ScreenOut o;
    o.pos = float4(lerp(Rect.x, Rect.z, c.x), lerp(Rect.y, Rect.w, c.y), 0, 1);
    o.uv = c;
    return o;
}

float4 ScreenPS(ScreenOut i) : SV_Target
{
    return (Fog.w > 0 ? Tex0.Sample(PointClamp, i.uv) : Tex0.Sample(Clamp, i.uv)) * Tint;
}

// ── 맵: 텍스처 × 라이트맵 ───────────────────────────────────────────────────
struct MapIn { float3 pos : POSITION; float2 uv : TEXCOORD0; float2 lm : TEXCOORD1; };
struct MapOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 lm : TEXCOORD1; float dist : TEXCOORD2; };

MapOut MapVS(MapIn i)
{
    MapOut o;
    o.pos = mul(float4(i.pos, 1), ViewProjection);
    o.uv = i.uv;
    o.lm = i.lm;
    o.dist = distance(i.pos, CameraPos.xyz);
    return o;
}

float4 MapPS(MapOut i) : SV_Target
{
    float4 c = Tex0.Sample(Wrap, i.uv) * Tint;
    clip(c.a - 0.4);
    // 원본은 텍스처 × 라이트맵을 2배 밝기로 섞는 것으로 보인다(분석-스테이지: /128 눈대중)
    float3 light = Fog.z > 0 ? Tex1.Sample(Clamp, i.lm).rgb * 2 : Ambient.rgb + LightColor.rgb * 0.6;
    float3 col = c.rgb * light;
    float f = saturate((i.dist - Fog.x) / max(Fog.y - Fog.x, 1));
    return float4(lerp(col, FogColor.rgb, f), c.a);
}

// ── 캐릭터: CPU 에서 스키닝한 정점 ──────────────────────────────────────────
struct CharIn { float3 pos : POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD0; };
struct CharOut { float4 pos : SV_Position; float3 nrm : NORMAL; float2 uv : TEXCOORD0; };

CharOut CharVS(CharIn i)
{
    CharOut o;
    float4 w = mul(float4(i.pos, 1), World);
    o.pos = mul(w, ViewProjection);
    o.nrm = mul(float4(i.nrm, 0), World).xyz;
    o.uv = i.uv;
    return o;
}

float4 CharPS(CharOut i) : SV_Target
{
    float4 t = Tex0.Sample(Wrap, i.uv);
    if (LightDir.w > 0.5) t.a = 1;   // material flag 0: texture alpha is not used (some flag-0 textures are 100% alpha 0)
    float4 c = t * Tint;
    clip(c.a - 0.3);
    float3 n = normalize(i.nrm);
    float d = saturate(dot(n, -LightDir.xyz));
    return float4(c.rgb * (Ambient.rgb + LightColor.rgb * d), c.a);
}

// ── 바닥 그림자 원반 ────────────────────────────────────────────────────────
struct ShadowIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct ShadowOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

ShadowOut ShadowVS(ShadowIn i)
{
    ShadowOut o;
    o.pos = mul(mul(float4(i.pos, 1), World), ViewProjection);
    o.uv = i.uv;
    return o;
}

float4 ShadowPS(ShadowOut i) : SV_Target
{
    float r = length(i.uv * 2 - 1);
    return float4(0, 0, 0, saturate(1 - r) * 0.45);
}
""";
}
