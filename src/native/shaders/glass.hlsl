Texture2D<float4> backdrop : register(t0);
SamplerState linearClamp : register(s0);

cbuffer Surface : register(b0) {
    float4 outputRect;
    float4 sourceRect;
    float4 panelRect;
    float4 material;
    float4 sourceLimits;
    float4 pointer;
    float4 light;
    float4 reserved;
    float4 shape;
};

float3 sampleBackdrop(float2 pixel, float mip) {
    float2 uv = (pixel - sourceRect.xy) / max(sourceRect.zw, 1.0);
    return backdrop.SampleLevel(linearClamp, uv, mip).rgb;
}

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
    float2 pixel = outputRect.xy + position.xy;
    float2 halfSize = panelRect.zw * 0.5;
    float2 p = pixel - panelRect.xy - halfSize;
    float radius = clamp(shape.w, 1.0, min(halfSize.x, halfSize.y));
    float2 corner = abs(p) - halfSize + radius;
    float distance = length(max(corner, 0.0)) + min(max(corner.x, corner.y), 0.0) - radius;
    float coverage = 1.0 - smoothstep(-0.6, 0.6, distance);
    if (coverage == 0.0) return 0.0;

    float2 normal = corner.x > corner.y ? float2(sign(p.x), 0.0) : float2(0.0, sign(p.y));
    if (min(corner.x, corner.y) > 0.0) normal = normalize(corner) * sign(p);
    float depth = max(0.0, -distance);
    float bevel = 1.0 - smoothstep(0.0, 12.0 * material.w, depth);
    float rim = exp(-abs(distance + 0.65) * 1.2);

#ifdef FROSTED
    float3 color = sampleBackdrop(pixel, 2.8) * 0.63 + float3(0.024, 0.030, 0.041);
    color += rim * (0.026 + saturate(-normal.y) * 0.057);
#else
    float2 refracted = pixel - normal * (5.0 + 1.6 * pointer.x) * bevel * material.w;
    float3 color = sampleBackdrop(refracted, 0.55) * 0.84 + float3(0.012, 0.017, 0.025);
    float2 direction = normalize(light.zw * halfSize - p + float2(0.001, -0.001));
    float highlight = pow(saturate(dot(normal, direction)), 7.0);
    color += rim * (0.034 + 0.050 * saturate(-normal.y) + 0.15 * highlight) * shape.z;
#endif
    return float4(saturate(color) * coverage, coverage);
}
