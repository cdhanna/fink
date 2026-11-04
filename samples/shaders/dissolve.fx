// DISSOLVE: the sprite breaks up texel by texel, with a glowing edge where it's going.
//
//   Progress    0 is whole, 1 is gone
//   EdgeColor   the color of the texels about to go
//   EdgeWidth   how much of the remaining sprite glows (0..1 of the noise range)
//   Speed       when above 0, Progress is ignored and it loops on its own, this many times a second
//   Time        seconds; the preview runs it
//
// Per sprite: texcoord1.y is added to Progress, and texcoord1.z picks a different pattern
// (any number), so a crowd of sprites doesn't dissolve in lockstep.
#include "FinkSprite.fxh"

float Progress = 0.4;
float4 EdgeColor = float4(1, 0.6, 0.2, 1);
float EdgeWidth = 0.12;
float Speed = 0;
float Time;

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    float progress = Speed > 0 ? frac(Time * Speed) : saturate(Progress + i.data1.y);
    float n = Hash(TexelOf(i.uv) + i.data1.z * 17.0);
    if (n < progress) return float4(0, 0, 0, 0);
    float glow = n < progress + EdgeWidth ? 1 : 0;
    return float4(lerp(c.rgb, EdgeColor.rgb * c.a, glow * EdgeColor.a), c.a);
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
