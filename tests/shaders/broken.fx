float4 Broken;
float4 PS(float2 uv : TEXCOORD0) : COLOR0 { return Broken * undefinedThing; }
technique T { pass { PixelShader = compile ps_3_0 PS(); } };
