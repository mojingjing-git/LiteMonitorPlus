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
    float4 presetStyle;
    float4 paletteBase;
    float4 palettePanel;
    float4 paletteBorder;
    float4 paletteAccent;
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

float ValueNoise(float2 value)
{
    float2 cell = floor(value);
    float2 local = frac(value);
    local = local * local * (3.0 - 2.0 * local);
    float a = Hash21(cell);
    float b = Hash21(cell + float2(1.0, 0.0));
    float c = Hash21(cell + float2(0.0, 1.0));
    float d = Hash21(cell + float2(1.0, 1.0));
    return lerp(lerp(a, b, local.x), lerp(c, d, local.x), local.y);
}

float FbmNebula(float2 value)
{
    float total = 0.0;
    float amplitude = 0.54;
    float2x2 turn = float2x2(0.80, 0.60, -0.60, 0.80);
    [unroll]
    for (int octave = 0; octave < 3; octave++)
    {
        total += ValueNoise(value) * amplitude;
        value = mul(turn, value) * 2.03 + 17.17;
        amplitude *= 0.48;
    }

    return total;
}

float StarField(float2 pixel, float time)
{
    float2 cellSize = float2(15.0, 15.0);
    float2 scaled = pixel / cellSize;
    float2 cell = floor(scaled);
    float2 local = frac(scaled) - 0.5;
    float seed = Hash21(cell + 3.7);
    float2 offset = (float2(
        Hash21(cell + 7.1),
        Hash21(cell + 13.9)) - 0.5) * 0.62;
    float star = step(0.77, seed) *
        (1.0 - smoothstep(0.025, 0.105, length(local - offset)));
    float twinkle = 0.72 + 0.28 * sin(time * (1.1 + seed * 2.7) + seed * 31.0);
    return star * twinkle;
}

float3 NebulaBackdrop(float2 pixel)
{
    float2 uv = pixel / max(viewport.xy, float2(1.0, 1.0));
    float time = viewport.w * 0.035;
    float2 flow = uv * float2(2.8, 3.8);
    float warpX = FbmNebula(flow + float2(time, -time * 0.6));
    float warpY = FbmNebula(flow * 1.13 + float2(8.0 - time * 0.4, 3.0));
    float2 warped = flow + (float2(warpX, warpY) - 0.5) * 1.65;
    float cloud = FbmNebula(warped);

    float3 violet = lerp(palettePanel.rgb, paletteBorder.rgb, 0.32);
    float3 base = lerp(paletteBase.rgb * 0.78, paletteBase.rgb, saturate(uv.y));
    float auroraA = exp(-abs(uv.y -
        (0.18 + sin(uv.x * 5.2 + time * 1.7) * 0.09 + (cloud - 0.45) * 0.20)) * 7.5);
    float auroraB = exp(-abs(uv.y -
        (0.54 + sin(uv.x * 7.4 - time * 1.2) * 0.055 + (warpX - 0.5) * 0.18)) * 10.5);
    float dust = smoothstep(0.34, 0.76, cloud);
    float3 color = base + palettePanel.rgb * dust * 0.72;
    color += paletteAccent.rgb * auroraA * (0.26 + cloud * 0.24);
    color += violet * auroraB * 0.34;
    color += paletteBorder.rgb * pow(cloud, 3.0) * 0.07;
    color += lerp(float3(0.72, 0.81, 1.0), paletteAccent.rgb, 0.48)
        * StarField(pixel, viewport.w);
    return saturate(color);
}

float4 NebulaGlassRoot(float2 pixel)
{
    float2 center = rootRect.xy + rootRect.zw * 0.5;
    float2 halfSize = rootRect.zw * 0.5;
    float radius = min(theme1.x, min(halfSize.x, halfSize.y));
    float2 local = pixel - center;
    float distanceField = SdRoundedBox(local, halfSize - 1.0, radius);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);
    float3 color = NebulaBackdrop(pixel);
    if (inside > 0.0)
    {
        const float epsilon = 0.75;
        float dx = SdRoundedBox(local + float2(epsilon, 0.0), halfSize - 1.0, radius)
            - SdRoundedBox(local - float2(epsilon, 0.0), halfSize - 1.0, radius);
        float dy = SdRoundedBox(local + float2(0.0, epsilon), halfSize - 1.0, radius)
            - SdRoundedBox(local - float2(0.0, epsilon), halfSize - 1.0, radius);
        float2 normal = normalize(float2(dx, dy) + float2(0.0001, 0.0001));

        float edge = saturate(1.0 - abs(distanceField) / 24.0);
        float rim = exp(-abs(distanceField + 0.8) / 1.25);
        float inner = exp(-abs(distanceField + 5.0) / 8.0) * step(0.0, -distanceField);
        color = NebulaBackdrop(pixel + normal * edge * presetStyle.y);
        float3 auroraViolet = lerp(paletteBorder.rgb, palettePanel.rgb, 0.42);
        color = lerp(color, color * 0.72 + palettePanel.rgb * 0.28,
            edge * 0.34 + inner * 0.12);
        color += paletteBorder.rgb * rim * 0.46;
        color += auroraViolet * inner * 0.08;
        float topGlint = saturate(0.62 - local.y / max(halfSize.y * 2.0, 1.0));
        color += paletteAccent.rgb * topGlint * edge * 0.055;
    }
    return float4(saturate(color), inside);
}

void NebulaGroup(float4 rect, float2 pixel, float time, inout float3 color)
{
    if (rect.z <= 2.0 || rect.w <= 2.0)
    {
        return;
    }

    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float radius = min(8.0 * state.z, min(halfSize.x, halfSize.y));
    float distanceField = SdRoundedBox(pixel - center, halfSize, radius);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);
    float rim = exp(-abs(distanceField + 0.7) / 1.25);
    float innerGlow = exp(-abs(distanceField + 5.0) / 8.0) * step(0.0, -distanceField);
    float header = 1.0 - smoothstep(
        0.0,
        1.2,
        abs(pixel.y - rect.y - 12.0 * state.z));
    float noise = ValueNoise(pixel * 0.12 + time * 0.015);
    float3 violet = lerp(palettePanel.rgb, paletteBorder.rgb, 0.28);
    float3 panel = lerp(paletteBase.rgb, palettePanel.rgb, 0.55 + noise * 0.14);
    color = lerp(color, panel, inside * saturate(0.72 + palettePanel.a * 0.18));
    color += paletteBorder.rgb * rim * 0.34;
    color += violet * innerGlow * 0.09;
    color += paletteAccent.rgb * header * inside * 0.10;
    color += paletteAccent.rgb * pow(noise, 4.0) * inside * 0.075;
}

float3 NixieBackdrop(float2 pixel)
{
    float2 uv = pixel / max(viewport.xy, float2(1.0, 1.0));
    float centerGlow = exp(-length((uv - float2(0.5, 0.36)) *
        float2(1.2, 0.90)) * 4.6);
    float lowerGlow = exp(-length((uv - float2(0.50, 0.82)) *
        float2(1.35, 1.0)) * 5.2);
    float brush = 0.5 + 0.5 * sin(
        pixel.y * 0.68 + sin(pixel.x * 0.031) * 2.4);
    float grain = Hash21(floor(pixel / 2.0)) - 0.5;
    float3 color = lerp(paletteBase.rgb * 0.72, palettePanel.rgb * 0.52, 0.48);
    color *= 0.91 + brush * 0.08 + grain * 0.016;
    color += paletteAccent.rgb * centerGlow * 0.10;
    color += paletteBorder.rgb * lowerGlow * 0.055;
    return saturate(color);
}

float NixieScrew(float2 local, float2 halfSize)
{
    float2 corner = abs(local) - (halfSize - float2(8.0, 8.0));
    float disk = 1.0 - smoothstep(1.8, 2.6, length(corner));
    float slot = 1.0 - smoothstep(0.35, 1.15, abs(corner.x + corner.y * 0.38));
    return saturate(disk * (0.56 + slot * 0.44));
}

float4 NixieBrassRoot(float2 pixel)
{
    float2 center = rootRect.xy + rootRect.zw * 0.5;
    float2 halfSize = rootRect.zw * 0.5;
    float2 local = pixel - center;
    float cut = max(4.0, min(presetStyle.w, min(halfSize.x, halfSize.y) * 0.45));
    float insideDistance = ChamferInside(local, halfSize - 1.0, cut);
    float inside = step(0.0, insideDistance);
    if (inside <= 0.0)
    {
        return float4(NixieBackdrop(pixel), 0.0);
    }

    float edge = saturate(1.0 - abs(insideDistance) / 2.4);
    float bevel = exp(-abs(insideDistance - 5.2) / 1.45) * step(0.0, insideDistance);
    float sweep = 0.5 + 0.5 * sin(
        pixel.y * 0.47 + sin(pixel.x * 0.043) * 1.8);
    float grain = Hash21(floor(pixel * 0.72)) - 0.5;
    float3 panel = lerp(paletteBase.rgb * 0.82, palettePanel.rgb, 0.42);
    panel *= 0.91 + sweep * 0.10 + grain * 0.014;
    panel += paletteBorder.rgb * bevel * 0.22;
    panel += paletteAccent.rgb * edge * 0.19;
    panel += paletteAccent.rgb * bevel * 0.055 * saturate(presetStyle.y / 24.0);
    float screw = NixieScrew(local, halfSize);
    panel = lerp(panel, paletteBorder.rgb, screw * 0.78);
    return float4(saturate(panel), inside);
}

float NixieRivet(float2 pixel, float2 position)
{
    float distance = length(pixel - position);
    float head = 1.0 - smoothstep(1.1, 2.2, distance);
    float inset = 1.0 - smoothstep(2.0, 3.0, distance);
    return saturate(head + inset * 0.34);
}

void NixieGroup(float4 rect, float2 pixel, float time, inout float3 color)
{
    if (rect.z <= 3.0 || rect.w <= 3.0)
    {
        return;
    }

    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float2 local = pixel - center;
    float insideDistance = ChamferInside(local, halfSize, 4.0 * state.z);
    float inside = step(0.0, insideDistance);
    float edge = exp(-abs(insideDistance + 0.8) / 1.55);
    float inner = exp(-abs(insideDistance + 4.5) / 6.5) * step(0.0, -insideDistance);
    float header = 1.0 - smoothstep(
        0.0,
        1.0,
        abs(pixel.y - rect.y - 11.0 * state.z));
    float brush = 0.5 + 0.5 * sin(
        pixel.y * 0.82 + sin(pixel.x * 0.027) * 1.7);
    float grain = Hash21(floor(pixel * 0.83 + time * 0.004)) - 0.5;
    float3 panel = lerp(paletteBase.rgb * 0.78, palettePanel.rgb * 0.62, 0.54);
    panel *= 0.91 + brush * 0.08 + grain * 0.012;
    color = lerp(color, panel, inside * 0.88);
    color += paletteBorder.rgb * edge * 0.33;
    color += paletteAccent.rgb * inner * 0.055;
    color += paletteAccent.rgb * header * inside * 0.08;
    float2 leftTop = rect.xy + float2(7.0, 7.0) * state.z;
    float2 rightTop = rect.xy + float2(rect.z - 7.0 * state.z, 7.0 * state.z);
    float2 leftBottom = rect.xy + float2(7.0 * state.z, rect.w - 7.0 * state.z);
    float2 rightBottom = rect.xy +
        float2(rect.z - 7.0 * state.z, rect.w - 7.0 * state.z);
    float rivets = NixieRivet(pixel, leftTop) + NixieRivet(pixel, rightTop) +
        NixieRivet(pixel, leftBottom) + NixieRivet(pixel, rightBottom);
    color = lerp(color, paletteBorder.rgb, saturate(rivets) * 0.64);
}

float3 CrtBackdrop(float2 pixel)
{
    float2 uv = pixel / max(viewport.xy, float2(1.0, 1.0));
    float2 centered = uv - 0.5;
    float vignette = 1.0 - smoothstep(
        0.34,
        0.78,
        length(centered * float2(1.0, 1.16)));
    float scanline = 0.91 + 0.09 * (0.5 + 0.5 * cos(pixel.y * 2.0943951));
    float glow = exp(-length(centered * float2(1.1, 0.86)) * 3.4);
    float phosphorNoise = Hash21(floor(pixel * 0.5)) - 0.5;
    float3 color = lerp(paletteBase.rgb * 0.78, palettePanel.rgb, 0.24);
    color *= (0.82 + vignette * 0.20) * scanline;
    color += paletteAccent.rgb * glow * 0.095;
    color += paletteAccent.rgb * phosphorNoise * 0.010;
    return saturate(color);
}

float4 CrtPhosphorRoot(float2 pixel)
{
    float2 center = rootRect.xy + rootRect.zw * 0.5;
    float2 halfSize = rootRect.zw * 0.5;
    float2 local = pixel - center;
    float radius = min(10.0 * state.z, min(halfSize.x, halfSize.y));
    float distanceField = SdRoundedBox(local, halfSize - 1.0, radius);
    float outerInside = 1.0 - smoothstep(-1.0, 1.0, distanceField);
    if (outerInside <= 0.0)
    {
        return float4(CrtBackdrop(pixel), 0.0);
    }

    float screenDistance = SdRoundedBox(
        local,
        max(halfSize - float2(7.0, 7.0) * state.z, float2(1.0, 1.0)),
        max(3.0, radius - 6.0 * state.z));
    float screenInside = 1.0 - smoothstep(-1.0, 1.0, screenDistance);
    float bezel = 1.0 - screenInside;
    float outerRim = exp(-abs(distanceField + 0.7) / 1.25);
    float innerRim = exp(-abs(screenDistance + 0.8) / 1.25);
    float scanline = 0.90 + 0.10 * (0.5 + 0.5 * cos(pixel.y * 2.0943951));
    float3 screen = CrtBackdrop(pixel) * scanline;
    float3 bezelColor = lerp(paletteBase.rgb * 1.24, palettePanel.rgb, 0.36);
    float3 color = lerp(bezelColor, screen, screenInside);
    color += paletteBorder.rgb * outerRim * 0.18;
    color += paletteAccent.rgb * innerRim * 0.28;
    color += paletteAccent.rgb * bezel * 0.015;
    return float4(saturate(color), outerInside);
}

void CrtGroup(float4 rect, float2 pixel, float time, inout float3 color)
{
    if (rect.z <= 3.0 || rect.w <= 3.0)
    {
        return;
    }

    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float radius = min(3.0 * state.z, min(halfSize.x, halfSize.y));
    float distanceField = SdRoundedBox(pixel - center, halfSize, radius);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);
    float rim = exp(-abs(distanceField + 0.8) / 1.15);
    float scanline = 0.86 + 0.14 * (0.5 + 0.5 * cos(pixel.y * 2.0943951));
    float3 screen = lerp(paletteBase.rgb * 0.54, palettePanel.rgb * 0.36, 0.44);
    screen *= scanline;
    color = lerp(color, screen, inside * 0.92);
    color += paletteAccent.rgb * rim * 0.30;
    float header = 1.0 - smoothstep(
        0.0,
        1.0,
        abs(pixel.y - rect.y - 11.0 * state.z));
    float tickMask = step(0.62, frac((pixel.x - rect.x) / 19.0));
    color += paletteAccent.rgb * header * tickMask * inside * 0.075;
    float sweepY = rect.y + frac(time * 0.17) * rect.w;
    float sweep = exp(-abs(pixel.y - sweepY) * 0.34) * inside;
    color += paletteAccent.rgb * sweep * 0.018;
}

float3 RenderNebulaPreset(float2 pixel)
{
    float3 color = NebulaGlassRoot(pixel).rgb;
    float4 groups[12] =
    {
        panel0, panel1, panel2, panel3, panel4, panel5,
        panel6, panel7, panel8, panel9, panel10, panel11
    };
    int groupCount = min((int)round(state.y), 12);
    [loop]
    for (int index = 0; index < groupCount; index++)
    {
        NebulaGroup(groups[index], pixel, viewport.w, color);
    }
    return color;
}

float3 RenderNixiePreset(float2 pixel)
{
    float3 color = NixieBrassRoot(pixel).rgb;
    float4 groups[12] =
    {
        panel0, panel1, panel2, panel3, panel4, panel5,
        panel6, panel7, panel8, panel9, panel10, panel11
    };
    int groupCount = min((int)round(state.y), 12);
    [loop]
    for (int index = 0; index < groupCount; index++)
    {
        NixieGroup(groups[index], pixel, viewport.w, color);
    }
    return color;
}

float3 RenderCrtPreset(float2 pixel)
{
    float3 color = CrtPhosphorRoot(pixel).rgb;
    float4 groups[12] =
    {
        panel0, panel1, panel2, panel3, panel4, panel5,
        panel6, panel7, panel8, panel9, panel10, panel11
    };
    int groupCount = min((int)round(state.y), 12);
    [loop]
    for (int index = 0; index < groupCount; index++)
    {
        CrtGroup(groups[index], pixel, viewport.w, color);
    }
    return color;
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
    int preset = (int)round(presetStyle.x);
    float3 color;
    if (preset == 1)
    {
        color = RenderNebulaPreset(pixel);
    }
    else if (preset == 2)
    {
        color = RenderNixiePreset(pixel);
    }
    else if (preset == 3)
    {
        color = RenderCrtPreset(pixel);
    }
    else
    {
        int mode = (int)round(viewport.z);
        color = ReadBackdrop(pixel, float2(0.0, 0.0));
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
    }

    float4 foreground = foregroundTexture.SampleLevel(linearClamp, input.uv, 0.0);
    color = color * (1.0 - foreground.a) + foreground.rgb;
    return float4(saturate(color), 1.0);
}
