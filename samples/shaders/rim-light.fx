// RIM LIGHT: lights the edge of the sprite that faces a light, one or two texels in. Reads
// well on pixel art: a lit edge with no blur.
//
//   LightDir     where the light comes from, in texels: (-1, -1) is the top left
//   RimColor     the light's color; alpha is how strong
//   Width        1 or 2 texels of lit edge
//   ShadeAmount  darkens the edge facing away from the light (0: off)
//
// Per sprite: texcoord2.zw is added to LightDir, so each sprite can be lit from where its
// light is.
#include "FinkSprite.fxh"

float2 LightDir = float2(-1, -1);
float4 RimColor = float4(1, 0.95, 0.7, 0.8);
float Width = 1;
float ShadeAmount = 0.35;

float Empty(float2 uv, float2 d) { return Sprite(uv + d * TexelSize).a < 0.5 ? 1 : 0; }

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    if (c.a < 0.5) return c;
    float2 dir = LightDir + i.data2.zw;
    float2 d = float2(dir.x > 0.3 ? 1 : dir.x < -0.3 ? -1 : 0, dir.y > 0.3 ? 1 : dir.y < -0.3 ? -1 : 0);
    float lit = Empty(i.uv, d);
    float shade = Empty(i.uv, -d);
    if (Width > 1.5)
    {
        lit = max(lit, Empty(i.uv, d * 2));
        shade = max(shade, Empty(i.uv, -d * 2));
    }
    c.rgb = lerp(c.rgb, RimColor.rgb * c.a, lit * RimColor.a);
    c.rgb *= 1 - shade * (1 - lit) * ShadeAmount;
    return c;
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
