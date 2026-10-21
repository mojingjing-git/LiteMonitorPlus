cbuffer SceneParameters : register(b0)
{
    float4 viewport;
    float4 rootRect;
    float4 panel0;
    float4 panel1;
    float4 panel2;
    float4 panel3;
    float4 panel4;
    float4 panel5;
    float4 panel6;
    float4 panel7;
    float4 panel8;
    float4 panel9;
    float4 panel10;
    float4 panel11;
    float4 theme0;
    float4 theme1;
    float4 theme2;
    float4 theme3;
    float4 theme4;
    float4 theme5;
    float4 motion;
    float4 desktop;
    float4 windowRect;
    float4 state;
};

Texture2D<float4> backgroundTexture : register(t0);
Texture2D<float4> foregroundTexture : register(t1);
SamplerState linearClamp : register(s0);

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    float2 position = float2(
        vertexId == 1 ? 3.0 : -1.0,
        vertexId == 2 ? 3.0 : -1.0);
    VertexOutput output;
    output.position = float4(position, 0.0, 1.0);
    output.uv = float2(
        position.x * 0.5 + 0.5,
        1.0 - (position.y * 0.5 + 0.5));
    return output;
}

float Hash21(float2 value)
{
    value = frac(value * float2(123.34, 456.21));
    value += dot(value, value + 45.32);
    return frac(value.x * value.y);
}

float3 FixedBackdrop(float2 pixel)
{
    float2 uv = pixel / max(viewport.xy, float2(1.0, 1.0));
    float3 color = lerp(
        float3(0.025, 0.065, 0.11),
        float3(0.035, 0.20, 0.18),
        saturate(uv.y * 1.1));
    float aurora = exp(-abs(uv.y - (0.18 + sin(uv.x * 6.0 + viewport.w * 0.20) * 0.08)) * 9.0);
    color += float3(0.05, 0.20, 0.34) * aurora * 0.65;
    float warm = exp(-length((uv - float2(0.76, 0.70)) * float2(1.0, 1.35)) * 5.0);
    color += float3(0.40, 0.18, 0.055) * warm * 0.38;
    float cool = exp(-length((uv - float2(0.22, 0.82)) * float2(1.0, 1.2)) * 5.4);
    color += float3(0.04, 0.26, 0.18) * cool * 0.34;

    float2 gridUv = pixel / 42.0;
    float2 gridLine = abs(frac(gridUv) - 0.5);
    float grid = 1.0 - smoothstep(0.465, 0.495, max(gridLine.x, gridLine.y));
    color += float3(0.10, 0.24, 0.28) * grid * 0.055;

    float2 cell = floor(pixel / 18.0);
    color += (Hash21(cell) - 0.5) * 0.009;
    return saturate(color);
}

float4 SampleDesktopTexture(float2 displayUv)
{
    float2 uv = saturate(displayUv);
    // DXGI_MODE_ROTATION: identity=1, 90=2, 180=3, 270=4.
    // Convert display-space UV to the unrotated duplication surface.
    if (state.w == 2.0) uv = float2(uv.y, 1.0 - uv.x);
    else if (state.w == 3.0) uv = 1.0 - uv;
    else if (state.w == 4.0) uv = float2(1.0 - uv.y, uv.x);
    return backgroundTexture.SampleLevel(linearClamp, uv, 0.0);
}

float2 DesktopUv(float2 screenPixel, float2 offset)
{
    float2 globalPixel = windowRect.xy + screenPixel + offset;
    return (globalPixel - desktop.xy) / max(desktop.zw, float2(1.0, 1.0));
}

float3 ReadBackdrop(float2 screenPixel, float2 offset)
{
    float3 sampled = FixedBackdrop(screenPixel + offset);
    if (state.x > 0.5)
    {
        sampled = SampleDesktopTexture(DesktopUv(screenPixel, offset)).rgb;
    }

    return sampled;
}

float3 BlurredBackdrop(float2 screenPixel, float radius)
{
    // ReadBackdrop consumes physical pixel offsets and normalizes exactly once.
    float2 screenOffset = radius;
    float3 color = ReadBackdrop(screenPixel, float2(0.0, 0.0)) * 0.20;
    color += ReadBackdrop(screenPixel, float2(screenOffset.x, 0.0)) * 0.12;
    color += ReadBackdrop(screenPixel, float2(-screenOffset.x, 0.0)) * 0.12;
    color += ReadBackdrop(screenPixel, float2(0.0, screenOffset.y)) * 0.12;
    color += ReadBackdrop(screenPixel, float2(0.0, -screenOffset.y)) * 0.12;
    color += ReadBackdrop(screenPixel, screenOffset) * 0.08;
    color += ReadBackdrop(screenPixel, -screenOffset) * 0.08;
    color += ReadBackdrop(screenPixel, float2(screenOffset.x, -screenOffset.y)) * 0.08;
    color += ReadBackdrop(screenPixel, float2(-screenOffset.x, screenOffset.y)) * 0.08;
    return color;
}

#include "ToolbarGlass.hlsli"

float SdRoundedBox(float2 localPosition, float2 halfSize, float radius)
{
    float2 q = abs(localPosition) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float ChamferInside(float2 localPosition, float2 halfSize, float cut)
{
    float2 q = abs(localPosition);
    float side = min(halfSize.x - q.x, halfSize.y - q.y);
    float corner = (halfSize.x + halfSize.y - cut) - q.x - q.y;
    return min(side, corner);
}

float4 GlassRoot(float2 pixel, float3 color)
{
    float2 center = rootRect.xy + rootRect.zw * 0.5;
    float2 halfSize = rootRect.zw * 0.5;
    float radius = min(theme1.x, min(halfSize.x, halfSize.y));
    float2 local = pixel - center;
    float distanceField = SdRoundedBox(local, halfSize - 1.0, radius);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);
    if (inside <= 0.0)
    {
        return float4(color, 0.0);
    }

    const float epsilon = 0.75;
    float dx = SdRoundedBox(local + float2(epsilon, 0.0), halfSize - 1.0, radius)
        - SdRoundedBox(local - float2(epsilon, 0.0), halfSize - 1.0, radius);
    float dy = SdRoundedBox(local + float2(0.0, epsilon), halfSize - 1.0, radius)
        - SdRoundedBox(local - float2(0.0, epsilon), halfSize - 1.0, radius);
    float2 normal = normalize(float2(dx, dy) + float2(0.0001, 0.0001));

    float edge = saturate(1.0 - abs(distanceField) / 28.0);
    float outline = exp(-abs(distanceField + 0.7) / 1.15);
    float innerGlow = exp(-abs(distanceField) / 7.0) * saturate(-distanceField / 8.0);
    float broadSheen = saturate(1.0 - max(distanceField, 0.0) / 96.0);

    float2 refractedUvOffset = normal * edge * theme1.y;
    float3 glass = ReadBackdrop(pixel, refractedUvOffset);
    float3 scattered = BlurredBackdrop(pixel + refractedUvOffset, theme1.z);
    glass = lerp(glass, scattered, 0.08 + edge * 0.34);
    float centerMask = saturate(1.0 - saturate(-distanceField / max(min(halfSize.x, halfSize.y), 1.0)));
    float clearCenter = saturate(theme3.a) * centerMask * 0.72;
    glass = lerp(glass, theme0.rgb, theme0.a * (0.44 + edge * 0.22) * (1.0 - clearCenter));

    float lightDirection = dot(normal, normalize(float2(-0.65, -0.76)));
    float topBias = saturate(0.56 - local.y / max(halfSize.y * 2.0, 1.0));
    float rim = outline * (0.48 + saturate(lightDirection) * 0.52);
    float sheen = broadSheen * topBias * (0.09 + saturate(lightDirection) * 0.06);
    float thickness = saturate(theme1.w);
    float highlight = saturate(theme4.x);
    float counterHighlight = saturate(theme4.y);
    glass += theme3.rgb * rim * 0.19 * thickness * highlight;
    glass += theme3.rgb * innerGlow * 0.025 * thickness * counterHighlight;
    glass += theme3.rgb * sheen * 0.026;

    float luma = dot(glass, float3(0.2126, 0.7152, 0.0722));
    float shadowScale = saturate(theme2.a / 0.34);
    glass = lerp(
        glass,
        glass * 0.84 + theme2.rgb * 0.055 * shadowScale,
        smoothstep(0.48, 0.78, luma) * 0.22);
    color = lerp(color, glass, inside);
    return float4(color, inside);
}

void GlassGroupSeparator(float4 rect, float2 pixel, inout float3 color)
{
    if (rect.z <= 1.0 || rect.w <= 1.0)
    {
        return;
    }

    bool inHeader = pixel.x >= rect.x + 1.0 && pixel.x <= rect.x + rect.z - 1.0 &&
        pixel.y >= rect.y + 1.0 && pixel.y <= rect.y + 27.0 * state.z;
    float headerTint = inHeader ? 0.020 : 0.0;
    float separator = 1.0 - smoothstep(
        0.0,
        1.0,
        abs(pixel.y - (rect.y + 28.0 * state.z)));
    color += float3(0.66, 0.84, 0.93) * (headerTint + separator * 0.055);
}

float4 CyberRoot(float2 pixel, float3 color)
{
    float2 center = rootRect.xy + rootRect.zw * 0.5;
    float2 halfSize = rootRect.zw * 0.5;
    float2 local = pixel - center;
    float insideDistance = ChamferInside(local, halfSize - 1.0, theme1.x);
    float inside = step(0.0, insideDistance);
    float edge = saturate(1.0 - abs(insideDistance) / 1.55);
    float innerBevel = exp(-abs(insideDistance - 6.0) / 1.3) * step(0.0, insideDistance);
    float glow = exp(-abs(insideDistance - 4.0) / 5.2) * step(0.0, insideDistance);

    if (inside > 0.0)
    {
        float brush = sin((pixel.y + sin(pixel.x * 0.013) * 2.0) * 0.22) * 0.5 + 0.5;
        float grain = Hash21(floor(pixel * 0.55)) - 0.5;
        float3 panel = theme0.rgb * (0.84 + brush * 0.12) + grain * 0.012;
        float header = 1.0 - smoothstep(0.0, 1.4, abs(pixel.y - rootRect.y - 8.0));
        float segments = step(0.26, frac((pixel.x - rootRect.x) / 52.0));
        float ventMask = step(halfSize.x - 18.0, abs(local.x));
        float ventLines = smoothstep(
            0.84,
            1.0,
            abs(sin((pixel.y + rootRect.y) * 0.15))) * ventMask;
        panel += theme3.rgb * header * segments * 0.26;
        panel += theme2.rgb * ventLines * 0.06 * saturate(theme4.w / 0.12);
        panel += theme2.rgb * innerBevel * 0.36;
        panel += theme3.rgb * edge * 0.13;
        panel += theme3.rgb * glow * 0.08 * saturate(theme4.x);
        color = lerp(color, panel, saturate(theme4.z));
    }

    return float4(color, inside);
}

void CyberGroupFrame(float4 rect, float2 pixel, float time, inout float3 color)
{
    if (rect.z <= 2.0 || rect.w <= 2.0)
    {
        return;
    }

    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float2 local = pixel - center;
    float cut = max(5.0, min(theme1.y, min(halfSize.x, halfSize.y) * 0.45));
    float insideDistance = ChamferInside(local, halfSize, cut);
    float edge = saturate(1.0 - abs(insideDistance) / 1.15);
    float bevel = exp(-abs(insideDistance - 4.0) / 1.1) * step(0.0, insideDistance);
    float scanLine = exp(-abs(pixel.y - (rect.y + frac(time * 0.10) * rect.w)) * 0.72);

    color = lerp(color, float3(0.005, 0.012, 0.016), edge * 0.38);
    color += theme2.rgb * bevel * 0.10;
    color += theme3.rgb * edge * 0.17 * saturate(theme4.y);
    color += theme3.rgb * scanLine * bevel * 0.022 * saturate(theme4.x);

    float headerBand = 1.0 - smoothstep(
        0.0,
        1.1,
        abs(pixel.y - rect.y - 11.0 * state.z));
    float segment = step(0.30, frac((pixel.x - rect.x) / 44.0));
    color += theme3.rgb * headerBand * segment * 0.16;

    float2 cornerDistance = abs(abs(local) - (halfSize - float2(6.0, 6.0)));
    float rivet = 1.0 - smoothstep(0.5, 2.2, length(cornerDistance));
    color += theme3.rgb * rivet * 0.18;
}

void AddGroup(float4 rect, float2 pixel, float time, int mode, inout float3 color)
{
    if (mode == 0)
    {
        GlassGroupSeparator(rect, pixel, color);
    }
    else
    {
        CyberGroupFrame(rect, pixel, time, color);
    }
}

float4 PSMain(VertexOutput input) : SV_Target
{
    float2 pixel = input.uv * viewport.xy;
    int mode = (int)round(viewport.z);
    float3 color = ReadBackdrop(pixel, float2(0.0, 0.0));
    float4 surface = mode == 0
        ? (theme5.x > 0.5 ? float4(ToolbarGlass(rootRect, color, pixel), 1.0) : GlassRoot(pixel, color))
        : CyberRoot(pixel, color);
    color = surface.rgb;

    int groupCount = (int)round(state.y);
    if (mode == 0 && theme5.x > 0.5) groupCount = 0;
    if (groupCount > 0) { AddGroup(panel0, pixel, viewport.w, mode, color); }
    if (groupCount > 1) { AddGroup(panel1, pixel, viewport.w, mode, color); }
    if (groupCount > 2) { AddGroup(panel2, pixel, viewport.w, mode, color); }
    if (groupCount > 3) { AddGroup(panel3, pixel, viewport.w, mode, color); }
    if (groupCount > 4) { AddGroup(panel4, pixel, viewport.w, mode, color); }
    if (groupCount > 5) { AddGroup(panel5, pixel, viewport.w, mode, color); }
    if (groupCount > 6) { AddGroup(panel6, pixel, viewport.w, mode, color); }
    if (groupCount > 7) { AddGroup(panel7, pixel, viewport.w, mode, color); }
    if (groupCount > 8) { AddGroup(panel8, pixel, viewport.w, mode, color); }
    if (groupCount > 9) { AddGroup(panel9, pixel, viewport.w, mode, color); }
    if (groupCount > 10) { AddGroup(panel10, pixel, viewport.w, mode, color); }
    if (groupCount > 11) { AddGroup(panel11, pixel, viewport.w, mode, color); }

    float4 foreground = foregroundTexture.SampleLevel(linearClamp, input.uv, 0.0);
    color = color * (1.0 - foreground.a) + foreground.rgb;
    return float4(saturate(color), 1.0);
}
