// OUTLINE: a hard pixel outline outside the sprite, 1 to 3 texels thick.
//
//   OutlineColor   its color; alpha fades it
//   Thickness      1, 2 or 3 texels
//   Corners        1 fills the diagonal corners too (a square outline), 0 leaves them (rounder)
//
// Per sprite: texcoord1.x adds to the outline's alpha, so a game can light up one sprite
// (a selected unit, say) without a parameter change.
// Needs empty pixels around the sprite to draw into: Fink's preview has a margin for that.
#include "FinkSprite.fxh"

float4 OutlineColor = float4(1, 1, 1, 1);
float Thickness = 1;
float Corners = 0;

float Covered(float2 uv, float2 d) { return Sprite(uv + d * TexelSize).a > 0.5 ? 1 : 0; }

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    float near = 0;
    [unroll] for (int r = 1; r <= 3; r++)
    {
        float on = r <= Thickness ? 1 : 0;
        near += on * (Covered(i.uv, float2(r, 0)) + Covered(i.uv, float2(-r, 0)) +
                      Covered(i.uv, float2(0, r)) + Covered(i.uv, float2(0, -r)));
        near += on * Corners * (Covered(i.uv, float2(r, r)) + Covered(i.uv, float2(-r, r)) +
                                Covered(i.uv, float2(r, -r)) + Covered(i.uv, float2(-r, -r)));
    }
    float edge = saturate(near) * (c.a < 0.5 ? 1 : 0);
    float a = saturate(OutlineColor.a + i.data1.x) * edge;
    return Over(c, OutlineColor.rgb, a);
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
