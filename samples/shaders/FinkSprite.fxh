// Shared parts of Fink's sample shaders. They use fade.mono's sprite vertex format
// (FadeSpriteBatch), so they work in the engine as they are: the sprite batch sets
// MatrixTransform and the sprite's texture, and passes two float4s of per-sprite data in
// texcoord1 and texcoord2.
//
// Colors coming out of the texture are premultiplied (the content pipeline premultiplies
// textures), so anything added on top is premultiplied too: rgb * a.

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

// One texel in UVs: 1 / the texture's size. Fink's preview fills this in; a game sets it
// (for a sprite sheet, 1 / the sheet's size).
float2 TexelSize = float2(0.03125, 0.03125);

struct VSOutput
{
    float4 position : SV_Position;
    float4 color    : COLOR0;
    float2 uv       : TEXCOORD0;
    float4 data1    : TEXCOORD1;   // per-sprite values
    float4 data2    : TEXCOORD2;
};

VSOutput SpriteVertexShader(float4 position : POSITION0, float4 color : COLOR0, float2 uv : TEXCOORD0,
                            float4 data1 : TEXCOORD1, float4 data2 : TEXCOORD2)
{
    VSOutput o;
    o.position = mul(position, MatrixTransform);
    o.color = color;
    o.uv = uv;
    o.data1 = data1;
    o.data2 = data2;
    return o;
}

float4 Sprite(float2 uv) { return tex2D(SpriteSampler, uv); }

// A straight (not premultiplied) color laid over a premultiplied one.
float4 Over(float4 under, float3 rgb, float a)
{
    return float4(rgb * a + under.rgb * (1 - a), a + under.a * (1 - a));
}

// The texel a uv is in, as whole numbers: for effects that should stay on the pixel grid.
float2 TexelOf(float2 uv) { return floor(uv / TexelSize); }

// A repeatable random number in 0..1 for a texel.
float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
