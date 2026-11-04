// DROP SHADOW: a flat copy of the sprite's shape, offset and behind it.
//
//   ShadowColor   its color; alpha is how dark
//   Offset        where it falls, in texels (x right, y down)
//
// Per sprite: texcoord2.xy is added to Offset, so a jumping sprite's shadow can fall further
// away as it rises.
// Needs empty pixels around the sprite to fall into: Fink's preview has a margin.
#include "FinkSprite.fxh"

float4 ShadowColor = float4(0, 0, 0, 0.45);
float2 Offset = float2(2, 2);

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    float2 offset = Offset + i.data2.xy;
    float shadow = Sprite(i.uv - offset * TexelSize).a * ShadowColor.a;
    // the shadow goes under the sprite: the sprite over it
    float4 s = float4(ShadowColor.rgb * shadow, shadow);
    return c + s * (1 - c.a);
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
