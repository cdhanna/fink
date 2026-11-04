// WAVE: rows slide side to side in a moving wave, like heat haze, water or a flag.
//
//   Amplitude   how far rows move, in texels
//   Frequency   waves down the sprite
//   Speed       waves per second
//   Snap        1 moves rows by whole texels (stays crisp pixel art), 0 moves them smoothly
//   Time        seconds; the preview runs it
//
// Per sprite: texcoord1.w offsets the wave's phase, so sprites side by side don't move as one.
// Needs empty pixels left and right of the sprite to move into: Fink's preview has a margin.
#include "FinkSprite.fxh"

float Amplitude = 1.5;
float Frequency = 2;
float Speed = 1;
float Snap = 1;
float Time;

float4 PS(VSOutput i) : SV_Target0
{
    // the row this pixel is on, in texels, so a whole row moves together
    float row = floor(i.uv.y / TexelSize.y) * TexelSize.y;
    float shift = sin((row * Frequency + Time * Speed + i.data1.w) * 6.2831853) * Amplitude;
    if (Snap > 0.5) shift = round(shift);
    return Sprite(i.uv + float2(shift * TexelSize.x, 0)) * i.color;
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL PS();
    }
};
