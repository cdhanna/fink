// HIT FLASH: turns the sprite toward a solid color, the "I got hit" blink.
//
//   FlashColor   the color it turns
//   Amount       0 is the sprite as drawn, 1 is all FlashColor
//   Blink        flashes per second when animating (0: hold at Amount)
//   Time         seconds; the preview runs it
//
// Per sprite: texcoord1.x is added to Amount, so a game flashes one sprite by setting its data,
// with every sprite sharing the one shader.
#include "FinkSprite.fxh"

float4 FlashColor = float4(1, 1, 1, 1);
float Amount = 0.8;
float Blink = 0;
float Time;

float4 PS(VSOutput i) : SV_Target0
{
    float4 c = Sprite(i.uv) * i.color;
    float amount = saturate(Amount + i.data1.x);
    if (Blink > 0) amount *= step(0.5, frac(Time * Blink));
    // the flash keeps the sprite's shape: only where it has alpha
    c.rgb = lerp(c.rgb, FlashColor.rgb * c.a, amount * FlashColor.a);
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
