// Shared v4 glass material, derived from the frozen toolbar preview.
float2 ToolbarDesktopUv(float2 screenPixel)
{
    float2 globalPixel = windowRect.xy + screenPixel;
    return (globalPixel - desktop.xy) / max(desktop.zw, float2(1.0, 1.0));
}

float4 ToolbarSampleBackground(float2 uv)
{
    return state.x > 0.5 ? SampleDesktopTexture(uv) : float4(FixedBackdrop(uv * desktop.zw + desktop.xy - windowRect.xy), 1.0);
}

// All radii are screen-space pixels. The samples soften local detail without
// painting a translucent white sheet over the wallpaper.
float3 ToolbarSampleFrosted(float2 uv, float radiusPixels)
{
    float2 radius = radiusPixels / max(desktop.zw, float2(1.0, 1.0));
    float2 nearAxis = radius * 0.38;
    float2 diagonal = radius * 0.58;
    float2 farAxis = radius * 0.92;
    float2 farDiagonal = radius * 1.08;

    float3 color = ToolbarSampleBackground(uv).rgb * 0.20;
    color += ToolbarSampleBackground(uv + float2(nearAxis.x, 0.0)).rgb * 0.09;
    color += ToolbarSampleBackground(uv - float2(nearAxis.x, 0.0)).rgb * 0.09;
    color += ToolbarSampleBackground(uv + float2(0.0, nearAxis.y)).rgb * 0.09;
    color += ToolbarSampleBackground(uv - float2(0.0, nearAxis.y)).rgb * 0.09;
    color += ToolbarSampleBackground(uv + diagonal).rgb * 0.04;
    color += ToolbarSampleBackground(uv - diagonal).rgb * 0.04;
    color += ToolbarSampleBackground(uv + float2(diagonal.x, -diagonal.y)).rgb * 0.04;
    color += ToolbarSampleBackground(uv + float2(-diagonal.x, diagonal.y)).rgb * 0.04;
    color += ToolbarSampleBackground(uv + float2(farAxis.x, 0.0)).rgb * 0.05;
    color += ToolbarSampleBackground(uv - float2(farAxis.x, 0.0)).rgb * 0.05;
    color += ToolbarSampleBackground(uv + float2(0.0, farAxis.y)).rgb * 0.05;
    color += ToolbarSampleBackground(uv - float2(0.0, farAxis.y)).rgb * 0.05;
    color += ToolbarSampleBackground(uv + farDiagonal).rgb * 0.02;
    color += ToolbarSampleBackground(uv - farDiagonal).rgb * 0.02;
    color += ToolbarSampleBackground(uv + float2(farDiagonal.x, -farDiagonal.y)).rgb * 0.02;
    color += ToolbarSampleBackground(uv + float2(-farDiagonal.x, farDiagonal.y)).rgb * 0.02;
    return color;
}

float ToolbarRoundedBoxDistance(float2 localPosition, float2 halfSize, float radius)
{
    float2 q = abs(localPosition) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float3 ToolbarGlass(float4 rect, float3 background, float2 screenPixel)
{
    float cornerRadius = min(theme1.x, min(rect.z, rect.w) * 0.5);
    float edgeThickness = max(theme1.w, 0.5);
    float fogRadius = theme1.z;
    float fogStrength = theme5.y;
    float refractionPixels = theme1.y;
    float shadowRadius = theme5.w;
    float shadowStrength = theme2.w;
    float highlightStrength = theme4.x;

    float2 center = rect.xy + rect.zw * 0.5;
    float2 halfSize = rect.zw * 0.5;
    float2 localPosition = screenPixel - center;
    float distanceField = ToolbarRoundedBoxDistance(localPosition, halfSize, cornerRadius);
    float inside = 1.0 - smoothstep(-1.0, 1.0, distanceField);

    float2 shadowCenter = center + float2(1.0, 11.0);
    float shadowDistance = ToolbarRoundedBoxDistance(
        screenPixel - shadowCenter,
        halfSize + float2(2.0, 4.0),
        cornerRadius + 3.0);
    // A blurred filled occluder fades monotonically across its edge. Cutting
    // its interior out creates a displaced dark ring and a false second pane.
    float shadowMask = 1.0 - smoothstep(-max(shadowRadius, 1.0),
        max(shadowRadius, 1.0), shadowDistance);
    background = lerp(
        background,
        float3(0.12, 0.16, 0.20),
        shadowMask * shadowStrength);

    const float epsilon = 0.65;
    float dx = ToolbarRoundedBoxDistance(
        localPosition + float2(epsilon, 0.0),
        halfSize,
        cornerRadius) -
        ToolbarRoundedBoxDistance(
            localPosition - float2(epsilon, 0.0),
            halfSize,
            cornerRadius);
    float dy = ToolbarRoundedBoxDistance(
        localPosition + float2(0.0, epsilon),
        halfSize,
        cornerRadius) -
        ToolbarRoundedBoxDistance(
            localPosition - float2(0.0, epsilon),
            halfSize,
            cornerRadius);
    float2 normal = normalize(float2(dx, dy) + float2(0.0001, 0.0001));

    if (inside <= 0.0)
    {
        return background;
    }

    float edgeDepth = max(-distanceField, 0.0);
    float edgeCurve = exp(-edgeDepth / edgeThickness);
    // Return continuously to the original sample at the silhouette, rather
    // than jumping eight pixels at the mask edge. Keep the lens inside the pane.
    float lensCurve = (1.0 - exp(-edgeDepth / (edgeThickness * 1.5))) *
        exp(-edgeDepth / (edgeThickness * 2.25));
    float2 refractedPixel = screenPixel + normal * lensCurve * refractionPixels;
    float2 clearUv = ToolbarDesktopUv(refractedPixel);
    float3 clearSample = ToolbarSampleBackground(clearUv).rgb;
    float3 scattered = ToolbarSampleFrosted(clearUv, fogRadius);

    // The interior remains clear; fog is concentrated into the curved edge.
    float fogAmount = fogStrength * lerp(0.40, 1.0, edgeCurve);
    float3 glass = lerp(clearSample, scattered, fogAmount);

    // A restrained neutral tint preserves wallpaper contours and dark text contrast.
    glass = lerp(glass, theme0.rgb, theme0.a * (0.52 + 0.48 * edgeCurve));
    float luma = dot(glass, float3(0.2126, 0.7152, 0.0722));
    glass *= lerp(0.985, 1.012, saturate(0.58 - localPosition.y / max(rect.w, 1.0)));
    glass += (luma - 0.72) * float3(0.010, 0.006, 0.002);

    // A cool boundary and one local specular arc give the pane a continuous
    // curved edge without a white outline around the whole shape.
    float boundary = exp(-abs(distanceField) / 0.85);
    glass = lerp(glass, float3(0.47, 0.57, 0.65), boundary * 0.16);

    float2 keyLight = normalize(float2(-0.58, -0.82));
    float facing = saturate(dot(normal, keyLight));
    float fineSpecular = pow(facing, 8.0) * boundary * highlightStrength;
    glass += theme3.rgb * fineSpecular * 0.52;

    float2 warmLight = normalize(float2(0.55, 0.84));
    float warmFacing = saturate(dot(normal, warmLight));
    float warmArc = pow(warmFacing, 12.0) * boundary;
    glass += float3(1.0, 0.87, 0.72) * warmArc * 0.035;

    float innerLift = exp(-max(edgeDepth - 1.2, 0.0) / 8.0) * edgeCurve;
    glass += theme3.rgb * innerLift * 0.018;

    return lerp(background, glass, inside);
}
