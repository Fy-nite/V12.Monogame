float2 ViewportSize = float2(1280.0, 720.0);

sampler2D tex0 : register(s0);
sampler2D fontTex : register(s1);

float4 scissorTransform;
float2 scissorTranslation;
float2 scissorExt;

float4 brushTransform;
float2 brushTranslation;
int brushType;
float4 brushColor1;
float4 brushColor2;
float4 brushParams;
float2 brushParams2;

float4 textureTransform;
float2 textureTranslation;

float2 atlasTexelSize;
float sdfPxRange;

struct VertexInput
{
    float2 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
    float4 Color : COLOR0;
};

struct VertexOutput
{
    float4 Position : POSITION0;
    float2 FragPos : TEXCOORD1;
    float2 FragUV : TEXCOORD2;
    float4 FragColor : COLOR0;
};

VertexOutput VSMain(VertexInput input)
{
    VertexOutput output;
    output.Position = float4(
        input.Position.x / ViewportSize.x * 2.0 - 1.0,
        1.0 - input.Position.y / ViewportSize.y * 2.0,
        0.0, 1.0);
    output.FragPos = input.Position;
    output.FragUV = input.TexCoord;
    output.FragColor = input.Color;
    return output;
}

float2 ApplyTransform(float4 t, float2 translation, float2 p)
{
    return float2(dot(t.xy, p), dot(t.zw, p)) + translation;
}

float BrushFactor(float2 p)
{
    float2 q = ApplyTransform(brushTransform, brushTranslation, p);
    if (brushType == 1)
    {
        float2 start = brushParams.xy;
        float2 dir = brushParams.zw - start;
        float len = length(dir);
        if (len < 0.001) return 0.0;
        return saturate(dot(q - start, dir) / (len * len));
    }
    if (brushType == 2)
    {
        return saturate(smoothstep(brushParams.z, brushParams.w, length(q - brushParams.xy)));
    }
    if (brushType == 3)
    {
        float2 center = brushParams.xy;
        float2 halfSize = brushParams.zw;
        float radius = brushParams2.x;
        float feather = brushParams2.y;
        if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;
        float2 d = abs(q - center) - (halfSize - float2(radius, radius));
        float dist = min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - radius;
        return smoothstep(-feather * 0.5, feather * 0.5, dist);
    }
    return 0.0;
}

float ScissorMask(float2 p)
{
    if (scissorExt.x < 0.0 || scissorExt.y < 0.0) return 1.0;
    float2 smoothEdges = scissorExt - abs(ApplyTransform(scissorTransform, scissorTranslation, p));
    return saturate(smoothEdges.x) * saturate(smoothEdges.y);
}

float SdfScreenPxRange(float2 uv)
{
    float2 unitRange = float2(sdfPxRange, sdfPxRange) * atlasTexelSize;
    float2 dx = ddx(uv);
    float2 dy = ddy(uv);
    float2 screenTexSize = 1.0 / max(abs(dx) + abs(dy), float2(0.00001, 0.00001));
    float2 range = unitRange * screenTexSize;
    return max(min(range.x, range.y), 1.0);
}

float4 PSMain(VertexOutput input) : COLOR0
{
    float mask = ScissorMask(input.FragPos);

    float4 color = input.FragColor;
    if (brushType > 0)
        color = lerp(brushColor1, brushColor2, BrushFactor(input.FragPos));

    float4 outColor;
    if (input.FragUV.x >= 2.0)
    {
        float2 uv = input.FragUV - 2.0;
        float sd = tex2D(fontTex, uv).r;
        float screenPxDistance = SdfScreenPxRange(uv) * (sd - 0.5);
        float coverage = saturate(screenPxDistance + 0.5);
        outColor = color * coverage * mask;
    }
    else
    {
        float edgeAlpha = saturate(input.FragUV.x);
        float4 fill = color * tex2D(tex0, ApplyTransform(textureTransform, textureTranslation, input.FragPos));
        outColor = fill * edgeAlpha * mask;
    }
    return outColor;
}

technique Paper
{
    pass P0
    {
        VertexShader = compile vs_3_0 VSMain();
        PixelShader = compile ps_3_0 PSMain();
    }
}
