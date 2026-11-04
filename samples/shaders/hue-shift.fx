// HUE SHIFT: recolors the sprite by turning its hues, for color variants of one drawing
// (team colors, a poisoned look) or a cycling rainbow.
//
//   Hue          turns, 0..1 (0.5 is the opposite colors)
//   Saturation   1 as drawn, 0 gray, above 1 more vivid
//   Brightness   1 as drawn
//   CycleSpeed   turns per second when animating (0: still)
//   Time         seconds; the preview runs it
//
// Per sprite: texcoord1.z is added to Hue, so one sheet gives every sprite its own color.
#include "FinkSprite.fxh"

float Hue = 0.33;
float Saturation = 1;
float Brightness = 1;
float CycleSpeed = 0;
float Time;

float3 RgbToHsv(float3 c)
{
    float4 K = float4(0, -1.0 / 3.0, 2.0 / 3.0, -1);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    return float3(abs(q.z + (q.w - q.y) / (6 * d + 1e-6)), d / (q.x + 1e-6), q.x);
}

float3 HsvToRgb(float3 c)
{
    float3 p = abs(frac(c.xxx + float3(1, 2.0 / 3.0, 1.0 / 3.0)) * 6 - 3);
    return c.z * lerp(float3(1, 1, 1), saturate(p - 1), c.y);
}

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    if (c.a <= 0) return c;
    float3 rgb = c.rgb / c.a;                     // un-premultiply
    float3 hsv = RgbToHsv(rgb);
    hsv.x = frac(hsv.x + Hue + i.data1.z + Time * CycleSpeed);
    hsv.y = saturate(hsv.y * Saturation);
    hsv.z *= Brightness;
    return float4(HsvToRgb(hsv) * c.a, c.a);
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
