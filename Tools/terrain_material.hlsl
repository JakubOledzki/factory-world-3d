// Body of the Custom node in M_FWTerrain (Tools/make_materials.py pastes it in; comment lines are dropped).
// Inputs: WP (absolute world position, cm), N (vertex normal, world), Cam (camera position), the texture
// objects BaseD/BaseN/BaseM, GrassD/GrassN/GrassM, CliffD/CliffN/CliffM, SnowD/SnowN/SnowM
// (M = AO, roughness, metal), and the scalars Tint, GrassAmount, SnowHeight, Scale.
// Normal maps sample raw (BC5 gives x, y in 0..1). Returns base colour; additional outputs NormalOut
// (tangent space) and ArmOut (AO, roughness, metal).

float dist = length(WP - Cam);
float2 uv = WP.xy / Scale;
// Far away the same textures are spread over ~15x the area, so slopes a kilometre off show no grid of repeats
float farMix = smoothstep(2500.0, 18000.0, dist);
float2 uvFar = uv * 0.065 + 0.17;
// Near, a second rotated sample hides the repetition; a slow noise picks between them
float2 uv2 = float2(uv.x * 0.8 - uv.y * 0.6, uv.x * 0.6 + uv.y * 0.8) * 0.37 + 0.31;
float macro = saturate(0.5 + 0.5 * sin(WP.x * 0.00061 + sin(WP.y * 0.00043) * 2.1) * cos(WP.y * 0.00057 - WP.x * 0.00021));
float detailMix = smoothstep(0.3, 0.7, macro);

#define NEAR(T, UV) lerp(Texture2DSample(T, T##Sampler, UV), Texture2DSample(T, T##Sampler, uv2), detailMix)
#define LAYER(T, UV) lerp(NEAR(T, UV), Texture2DSample(T, T##Sampler, uvFar), farMix)

float3 baseD = LAYER(BaseD, uv).rgb;
float3 baseN = LAYER(BaseN, uv).rgb * 2 - 1;
float3 baseM = LAYER(BaseM, uv).rgb;

float3 grassD = LAYER(GrassD, uv * 0.7).rgb;
float3 grassN = LAYER(GrassN, uv * 0.7).rgb * 2 - 1;
float3 grassM = LAYER(GrassM, uv * 0.7).rgb;

// Rock faces are projected from the side they face, so cliffs do not smear; larger far away
float3 n = normalize(N);
float2 side = abs(n.x) > abs(n.y) ? WP.yz : WP.xz;
float2 cuv = side / (Scale * lerp(1.6, 9.0, farMix));
float3 cliffD = Texture2DSample(CliffD, CliffDSampler, cuv).rgb;
float3 cliffN = Texture2DSample(CliffN, CliffNSampler, cuv).rgb * 2 - 1;
float3 cliffM = Texture2DSample(CliffM, CliffMSampler, cuv).rgb;

float3 snowD = Texture2DSample(SnowD, SnowDSampler, uv * 0.5).rgb * 0.82;
float3 snowN = Texture2DSample(SnowN, SnowNSampler, uv * 0.5).rgb * 2 - 1;
float3 snowM = Texture2DSample(SnowM, SnowMSampler, uv * 0.5).rgb;

// Masks: grass in patches on gentle ground, rock on steep slopes, snow high up
float patches = saturate(0.5 + 0.5 * sin(WP.x * 0.0023 + 1.7 * sin(WP.y * 0.0017)) * sin(WP.y * 0.0029 + 1.3 * sin(WP.x * 0.0011)));
float slope = 1 - saturate(n.z);
float cliff = smoothstep(0.13, 0.3, slope);
float grass = saturate(smoothstep(0.35, 0.75, patches + baseD.g * 0.4) * GrassAmount) * (1 - cliff);
float snow = smoothstep(SnowHeight, SnowHeight + 8000, WP.z + (patches - 0.5) * 3000) * (1 - cliff * 0.8);

float3 color = lerp(baseD, grassD, grass);
float3 normal = lerp(baseN, grassN, grass);
float3 arm = lerp(baseM, grassM, grass);
color = lerp(color, cliffD, cliff);
normal = lerp(normal, cliffN, cliff);
arm = lerp(arm, cliffM, cliff);
color = lerp(color, snowD, snow);
normal = lerp(normal, snowN, snow);
arm = lerp(arm, snowM, snow);

// Large-scale brightness changes, so the ground does not look printed; flatter normals far away
color *= lerp(0.82, 1.12, macro) * Tint;
normal.xy *= lerp(1.0, 0.4, farMix);
// Normal maps are BC5 (x, y only): rebuild z after blending
NormalOut = float3(normal.xy, sqrt(saturate(1 - dot(normal.xy, normal.xy))));
ArmOut = arm;
return color;
