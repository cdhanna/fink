// sprites_palette.fx, made by Fink. Do not change this file; Fink writes it on every export.
//
// Draws a Fink atlas: the sprite's texture holds palette indices (red channel, alpha 0 or 1),
// the Palette texture holds 256 colors per row, one row per palette variant, and texcoord1.x
// picks the row (Fade.MonoGame.Fink sets it: set fink sprite palette). The sprite's color
// still tints it. Fade.MonoGame.Fink sets Palette and PaletteRows every frame.
#if defined(SM6) || defined(VULKAN)
    #define PS_SHADERMODEL ps_6_0
    #define PS_TARGET SV_Target0
    Texture2D<float4> SpriteTexture : register(t0);
    sampler SpriteSampler : register(s0);
    Texture2D<float4> Palette : register(t1);
    sampler PaletteSampler : register(s1) = sampler_state
    {
        MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp;
    };
    #define SAMPLE(t, s, uv) t.Sample(s, uv)
#else
    #define PS_SHADERMODEL ps_3_0
    #define PS_TARGET COLOR0
    Texture2D SpriteTexture;
    sampler2D SpriteSampler : register(s0) = sampler_state { Texture = <SpriteTexture>; };
    Texture2D Palette;
    sampler2D PaletteSampler : register(s1) = sampler_state
    {
        Texture = <Palette>;
        MinFilter = Point; MagFilter = Point; MipFilter = Point; AddressU = Clamp; AddressV = Clamp;
    };
    #define SAMPLE(t, s, uv) tex2D(s, uv)
#endif

float PaletteRows;

float4 MainPS(float4 color : COLOR0, float2 uv : TEXCOORD0, float4 custom : TEXCOORD1) : PS_TARGET
{
    float4 indexed = SAMPLE(SpriteTexture, SpriteSampler, uv);
    if (indexed.a < 0.5) return float4(0, 0, 0, 0);
    float index = floor(indexed.r * 255.0 + 0.5);
    float rows = max(PaletteRows, 1.0);
    float row = clamp(floor(custom.x + 0.5), 0.0, rows - 1.0);
    float4 c = SAMPLE(Palette, PaletteSampler, float2((index + 0.5) / 256.0, (row + 0.5) / rows));
    // the content build premultiplies textures; the sprite batch blends straight alpha
    c.rgb /= max(c.a, 0.0001);
    return c * color;
}

technique SpriteDrawing
{
    pass P0
    {
        PixelShader = compile PS_SHADERMODEL MainPS();
    }
};