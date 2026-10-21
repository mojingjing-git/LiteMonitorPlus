cbuffer Parameters : register(b0)
{
    float4 viewport;
    float4 panel0;
    float4 panel1;
    float4 panel2;
    float4 panel3;
    float4 theme0;
    float4 theme1;
    float4 theme2;
    float4 theme3;
    float4 desktop;
    float4 windowRect;
    float4 bars;
    float4 flags;
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

float2 DesktopUv(float2 screenPixel)
{
    float2 globalPixel = windowRect.xy + screenPixel;
    return (globalPixel - desktop.xy) / max(desktop.zw, float2(1.0, 1.0));
}

float4 SampleBackground(float2 uv)
{
    return backgroundTexture.SampleLevel(linearClamp, saturate(uv), 0.0);
}

float4 SampleBlurred(float2 uv, float radius)
{
    float2 texel = radius / max(desktop.zw, float2(1.0, 1.0));
    float4 color = SampleBackground(uv) * 0.2;
    color += SampleBackground(uv + float2(texel.x, 0.0)) * 0.12;
    color += SampleBackground(uv - float2(texel.x, 0.0)) * 0.12;
    color += SampleBackground(uv + float2(0.0, texel.y)) * 0.12;
    color += SampleBackground(uv - float2(0.0, texel.y)) * 0.12;
    color += SampleBackground(uv + texel) * 0.08;
    color += SampleBackground(uv - texel) * 0.08;
    color += SampleBackground(uv + float2(texel.x, -texel.y)) * 0.08;
    color += SampleBackground(uv + float2(-texel.x, texel.y)) * 0.08;
    return color;
}

float SdRoundedBox(float2 localPosition, float2 halfSize, float radius)
{
    float2 q = abs(localPosition) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float GlassPanel(float4 rect, inout float3 color, float2 screenPixel, float time)
{
    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float2 localPosition = screenPixel - center;
    float distanceField = SdRoundedBox(localPosition, halfSize - 2.0, theme1.x);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);

    const float epsilon = 0.75;
    float dx = SdRoundedBox(localPosition + float2(epsilon, 0.0), halfSize - 2.0, theme1.x)
        - SdRoundedBox(localPosition - float2(epsilon, 0.0), halfSize - 2.0, theme1.x);
    float dy = SdRoundedBox(localPosition + float2(0.0, epsilon), halfSize - 2.0, theme1.x)
        - SdRoundedBox(localPosition - float2(0.0, epsilon), halfSize - 2.0, theme1.x);
    float2 normal = normalize(float2(dx, dy) + float2(0.0001, 0.0001));

    float edge = saturate(1.0 - abs(distanceField) / 24.0);
    float innerGlow = exp(-abs(distanceField + 1.5) / 4.0);
    float broadSheen = saturate(1.0 - max(distanceField, 0.0) / 72.0);
    float shadow = exp(-max(distanceField, 0.0) / 17.0) * step(0.0, distanceField);
    float3 shadowColor = theme2.xyz;
    float shadowBias = saturate(dot(normalize(localPosition + float2(0.001, 0.001)), float2(0.58, 0.82)) * 0.5 + 0.5);
    color = lerp(color, shadowColor, shadow * theme2.w * (0.24 + shadowBias * 0.32));

    if (inside > 0.0)
    {
        float2 baseUv = DesktopUv(screenPixel);
        float2 refractedUv = DesktopUv(screenPixel + normal * edge * theme1.z);
        float3 clearSample = SampleBackground(refractedUv).rgb;
        float3 scattered = SampleBlurred(refractedUv, theme1.w).rgb;
        float3 glassSample = lerp(clearSample, scattered, 0.10 + edge * 0.40);
        glassSample = lerp(glassSample, theme0.rgb, theme0.a * (0.62 + edge * 0.38));
        float backgroundLuma = dot(glassSample, float3(0.2126, 0.7152, 0.0722));
        float brightField = smoothstep(0.42, 0.82, backgroundLuma);
        glassSample = lerp(glassSample, glassSample * 0.72 + theme2.xyz * 0.08, brightField * 0.42);

        float lightDirection = dot(normal, normalize(float2(-0.65, -0.76)));
        float topBias = saturate(0.55 - localPosition.y / max(halfSize.y * 2.0, 1.0));
        float coolRim = innerGlow * (0.34 + saturate(lightDirection) * 0.66);
        float warmRim = innerGlow * saturate(0.18 - lightDirection * 0.28);
        float topSheen = broadSheen * topBias * (0.16 + saturate(lightDirection) * 0.10);

        glassSample *= lerp(0.965, 1.025, topBias);
        glassSample += theme3.rgb * coolRim * 0.105;
        glassSample += float3(1.0, 0.91, 0.76) * warmRim * 0.024;
        glassSample += theme3.rgb * topSheen * 0.035;

        float movingCaustic = 0.5 + 0.5 * sin(time * 0.35 + screenPixel.y * 0.025);
        glassSample += theme3.rgb * innerGlow * movingCaustic * 0.018;
        color = lerp(color, glassSample, inside);
    }

    return inside;
}

float ChamferInside(float2 localPosition, float2 halfSize, float cut)
{
    float2 q = abs(localPosition);
    float side = min(halfSize.x - q.x, halfSize.y - q.y);
    float corner = (halfSize.x + halfSize.y - cut) - q.x - q.y;
    return min(side, corner);
}

float CyberPanel(float4 rect, inout float3 color, float2 screenPixel, float time, bool primary)
{
    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float2 localPosition = screenPixel - center;
    float cut = primary ? theme1.x : theme1.y;
    float insideDistance = ChamferInside(localPosition, halfSize - 2.0, cut);
    float inside = step(0.0, insideDistance);
    float edge = saturate(1.0 - abs(insideDistance) / 1.7);
    float innerBevel = exp(-abs(insideDistance - 7.0) / 1.6) * step(0.0, insideDistance);
    float glow = exp(-abs(insideDistance - 5.0) / 6.5) * step(0.0, insideDistance);
    float shadow = saturate(1.0 - abs(insideDistance + 9.0) / 16.0) * step(insideDistance, 0.0);

    color = lerp(color, float3(0.005, 0.008, 0.012), shadow * 0.7);
    if (inside > 0.0)
    {
        float2 local = localPosition / max(halfSize, float2(1.0, 1.0));
        float brushed = sin((screenPixel.y + sin(screenPixel.x * 0.013) * 2.4) * 0.23) * 0.5 + 0.5;
        float grain = frac(sin(dot(floor(screenPixel * 0.5), float2(12.9898, 78.233))) * 43758.5453);
        float3 panel = theme0.rgb * (0.80 + brushed * 0.16) + (grain - 0.5) * 0.014;
        panel += theme2.rgb * (1.0 - saturate(length(local))) * 0.018;
        panel *= lerp(0.82, 1.06, saturate(0.55 - localPosition.y / max(halfSize.y * 2.0, 1.0)));
        float structural = smoothstep(0.996, 1.0, abs(sin((screenPixel.x - screenPixel.y) * 0.038))) * 0.045;
        panel += theme2.rgb * structural;
        color = lerp(color, panel, 0.94);
        color += theme3.rgb * edge * 0.18;
        color += theme2.rgb * innerBevel * 0.46;
        color += theme3.rgb * glow * 0.22;

        float2 rivetOffset = abs(abs(localPosition) - (halfSize - float2(13.0, 13.0)));
        float rivetCore = 1.0 - smoothstep(1.2, 2.8, length(rivetOffset));
        float rivetWell = 1.0 - smoothstep(3.0, 6.0, length(rivetOffset));
        color += theme2.rgb * rivetWell * 0.24;
        color += theme3.rgb * rivetCore * 0.20;

        float headerBand = 1.0 - smoothstep(0.0, 1.2, abs((screenPixel.y - rect.y) - 9.0));
        float headerSegments = step(0.24, frac((screenPixel.x - rect.x) / 48.0));
        color += theme3.rgb * headerBand * headerSegments * 0.32;

        float ventMask = step(halfSize.x - 24.0, abs(localPosition.x));
        float ventLines = smoothstep(0.82, 1.0, abs(sin((screenPixel.y + rect.y) * 0.16))) * ventMask;
        color += theme2.rgb * ventLines * 0.08;

        float scannerY = rect.y + frac(time * 0.16) * rect.w;
        float scanner = exp(-abs(screenPixel.y - scannerY) * 0.72) * innerBevel;
        color += theme3.rgb * scanner * 0.09;
    }

    return inside;
}

void GlassMeter(float4 rect, float progress, float2 screenPixel, inout float3 color)
{
    float2 localPosition = screenPixel - (rect.xy + rect.zw * 0.5);
    float insideDistance = SdRoundedBox(localPosition, rect.zw * 0.5, rect.w * 0.5);
    float inside = step(insideDistance, 0.0);
    if (inside > 0.0)
    {
        float filled = step(screenPixel.x, rect.x + rect.z * saturate(progress));
        float3 track = lerp(color, float3(0.72, 0.85, 0.92), 0.12);
        float3 fill = lerp(color, theme3.rgb, 0.78);
        float core = exp(-abs(localPosition.y) * 5.0);
        color = lerp(color, lerp(track, fill, filled), 0.86);
        color += theme3.rgb * core * filled * 0.05;
        float leadingEdge = filled * exp(-abs(screenPixel.x - (rect.x + rect.z * saturate(progress))) / 1.2);
        color += theme3.rgb * leadingEdge * 0.20;
    }
}

void CyberBar(float4 rect, float progress, float2 screenPixel, inout float3 color, float warn)
{
    float2 center = rect.xy + rect.zw * 0.5;
    float2 localPosition = screenPixel - center;
    float2 halfSize = rect.zw * 0.5;
    float insideDistance = ChamferInside(localPosition, halfSize, 3.0);
    float inside = step(0.0, insideDistance);
    float edge = saturate(1.0 - abs(insideDistance) / 1.4);
    if (inside > 0.0)
    {
        float3 fillColor = lerp(theme3.rgb, float3(1.0, 0.36, 0.18), warn);
        float filled = step(screenPixel.x, rect.x + rect.z * saturate(progress));
        float3 base = lerp(float3(0.012, 0.024, 0.032), fillColor * 0.66, filled);
        float scan = 0.82 + 0.18 * sin(screenPixel.x * 0.17);
        float core = filled * exp(-abs(localPosition.y) * 7.0) * scan;
        float leadingEdge = filled * exp(-abs(screenPixel.x - (rect.x + rect.z * saturate(progress))) / 1.8);
        float ticks = step(0.86, frac((screenPixel.x - rect.x) / 24.0));
        color = lerp(color, base, 0.92);
        color += fillColor * core * 0.17;
        color += fillColor * leadingEdge * 0.30;
        color += theme2.rgb * ticks * (1.0 - filled) * 0.12;
        color += fillColor * edge * 0.32;
    }
}

float4 PSMain(VertexOutput input) : SV_Target
{
    float2 screenPixel = input.uv * viewport.xy;
    int mode = (int)round(viewport.z);
    float time = viewport.w;
    float3 color = SampleBackground(DesktopUv(screenPixel)).rgb;

    if (mode == 0)
    {
        GlassPanel(panel0, color, screenPixel, time);
        GlassPanel(panel1, color, screenPixel, time);
        GlassPanel(panel2, color, screenPixel, time);
        GlassPanel(panel3, color, screenPixel, time);

        GlassMeter(float4(panel0.x + 32.0, panel0.y + 182.0, 240.0, 3.0), bars.x, screenPixel, color);
        GlassMeter(float4(panel1.x + 32.0, panel1.y + 160.0, 276.0, 3.0), bars.x, screenPixel, color);
        GlassMeter(float4(panel1.x + 380.0, panel1.y + 160.0, 276.0, 3.0), bars.y, screenPixel, color);
        GlassMeter(float4(panel1.x + 32.0, panel1.y + 262.0, 276.0, 3.0), bars.z, screenPixel, color);
        GlassMeter(float4(panel1.x + 380.0, panel1.y + 262.0, 276.0, 3.0), bars.w, screenPixel, color);
    }
    else
    {
        CyberPanel(panel0, color, screenPixel, time, true);
        CyberPanel(panel1, color, screenPixel, time, false);
        CyberPanel(panel2, color, screenPixel, time, false);

        CyberBar(float4(panel0.x + 40.0, panel0.y + 164.0, panel0.z - 80.0, 10.0), bars.x, screenPixel, color, 0.0);
        CyberBar(float4(panel0.x + 40.0, panel0.y + 278.0, panel0.z - 80.0, 10.0), bars.y, screenPixel, color, 0.0);
        CyberBar(float4(panel0.x + 40.0, panel0.y + 390.0, panel0.z - 80.0, 9.0), bars.z, screenPixel, color, 0.0);
        CyberBar(float4(panel0.x + 40.0, panel0.y + 502.0, panel0.z - 80.0, 9.0), bars.w, screenPixel, color, 0.0);
        CyberBar(float4(panel1.x + 38.0, panel1.y + 210.0, panel1.z - 76.0, 2.0), bars.w, screenPixel, color, 0.0);
    }

    float4 foreground = foregroundTexture.SampleLevel(linearClamp, input.uv, 0.0);
    color = color * (1.0 - foreground.a) + foreground.rgb;
    return float4(saturate(color), 1.0);
}
