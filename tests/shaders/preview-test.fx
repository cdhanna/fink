// A small shader for checking Fink's shader preview. It uses fade.mono's sprite vertex format
// (FadeSpriteBatch): texcoord1 and texcoord2 carry per-sprite values, like the game's shaders.
#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

Texture2D SpriteTexture;
sampler2D SpriteSampler = sampler_state { Texture = <SpriteTexture>; };
float4x4 MatrixTransform;

float4 Tint;          // multiplied in; alpha mixes toward it
float OutlineAmount;  // 0..1: an outline one texel outside the sprite
float4 OutlineColor;
float2 TexelSize;     // 1 / texture size
float Time;           // seconds
float Wobble;         // texels of sideways wave

struct VSOutput
{
    float4 position : SV_Position;
    float4 color    : COLOR0;
    float2 texCoord : TEXCOORD0;
    float4 custom   : TEXCOORD1;
    float4 custom2  : TEXCOORD2;
};

VSOutput SpriteVertexShader(float4 position : POSITION0, float4 color : COLOR0, float2 texCoord : TEXCOORD0,
                            float4 custom : TEXCOORD1, float4 custom2 : TEXCOORD2)
{
    VSOutput output;
    output.position = mul(position, MatrixTransform);
    output.color = color;
    output.texCoord = texCoord;
    output.custom = custom;
    output.custom2 = custom2;
    return output;
}

float4 SpritePixelShader(VSOutput input) : SV_Target0
{
    float2 uv = input.texCoord;
    uv.x += sin(uv.y * 20.0 + Time * 4.0) * Wobble * TexelSize.x;
    float4 c = tex2D(SpriteSampler, uv) * input.color;
    c.rgb = lerp(c.rgb, c.rgb * Tint.rgb, Tint.a);
    // texcoord1.x brightens, like a per-sprite flash
    c.rgb += input.custom.x * c.a;

    float around = 0;
    around += tex2D(SpriteSampler, uv + float2(TexelSize.x, 0)).a;
    around += tex2D(SpriteSampler, uv - float2(TexelSize.x, 0)).a;
    around += tex2D(SpriteSampler, uv + float2(0, TexelSize.y)).a;
    around += tex2D(SpriteSampler, uv - float2(0, TexelSize.y)).a;
    float edge = saturate(around) * (1 - c.a) * OutlineAmount;
    return c + OutlineColor * edge;
}

technique SpriteBatch
{
    pass
    {
        VertexShader = compile VS_SHADERMODEL SpriteVertexShader();
        PixelShader = compile PS_SHADERMODEL SpritePixelShader();
    }
};
