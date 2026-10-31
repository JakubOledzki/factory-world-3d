// Body of the Custom node in M_FWCharacter (Tools/make_materials.py pastes it in; comment lines are dropped).
// Paints the UE5 mannequin (SKM_Manny_Simple) as the character the player picked in the creator: skin, hair
// by style, eyes, face, facial hair, shirt by style, trousers and shoes, all from the server's palettes.
// Regions are found from the bind-pose position (PreSkinnedLocalPosition, cm): the mesh faces +Y, the feet are
// at Z = 0 and the top of the head at ~182; in the bind pose the arms run from the shoulder (|x| 19, z 144)
// through the elbow (35, 121) to the wrist (48, 105).
// Inputs: P (pre-skinned local position), Skin, Hair, Eyes, Shirt, Pants, Shoes (colours), HairStyle,
// ShirtStyle, FacialHair, FaceStyle (indices as in character.options). Returns base colour; RoughOut is the
// roughness.

float ax = abs(P.x);
// Small value noise for fabric and hair texture
float grain = frac(sin(dot(floor(P.xz * 3.0), float2(12.9898, 78.233))) * 43758.5453);
float fine = frac(sin(dot(floor(P.yz * 9.0 + P.x * 5.0), float2(39.346, 11.135))) * 24634.6345);

// Arm: the part of the mesh out beyond the torso; t runs 0 at the shoulder, ~0.57 at the elbow, 1 at the wrist
float2 armDir = normalize(float2(28.8, -39.1));
float t = dot(float2(ax - 19.0, P.z - 143.6), armDir) / 48.5;
float arm = step(18.0 + max(0.0, 140.0 - P.z) * 0.12, ax) * step(80.0, P.z);

// Head and neck
float3 headCentre = float3(0.0, 2.0, 170.0);
float3 h = P - headCentre;
float head = step(155.0, P.z) * (1.0 - arm);
float front = smoothstep(4.0, 8.0, P.y);  // the face side of the head

float3 color = Skin;
float rough = 0.45;

// Trousers up to the waist, shoes below the ankle
float legs = (1.0 - arm) * step(P.z, 101.0);
color = lerp(color, Pants * lerp(0.9, 1.05, grain), legs);
rough = lerp(rough, 0.85, legs);
// A belt
float belt = legs * smoothstep(97.0, 98.0, P.z) * (1.0 - smoothstep(100.0, 101.0, P.z));
color = lerp(color, float3(0.05, 0.04, 0.035), belt);
float feet = step(P.z, 13.0);
color = lerp(color, Shoes * lerp(0.85, 1.0, fine), feet);
rough = lerp(rough, 0.55, feet);
// Soles
color = lerp(color, float3(0.03, 0.03, 0.03), step(P.z, 2.2));

// Shirt: the torso from the waist to the neck, sleeves by style (0 t-shirt, 1 tank top, 2 hoodie)
float torso = (1.0 - arm) * step(101.0, P.z) * step(P.z, ShirtStyle > 1.5 ? 156.0 : 151.0);
// Tank top: bare shoulders, wide arm holes
float armHole = ShirtStyle > 0.5 && ShirtStyle < 1.5 ? step(13.0, ax) * step(128.0, P.z) : 0.0;
float neckLine = ShirtStyle < 1.5 ? smoothstep(4.0, 6.0, P.y) * step(147.0, P.z) * step(ax, 7.0) : 0.0;
torso *= (1.0 - armHole) * (1.0 - neckLine);
float sleeveEnd = ShirtStyle > 1.5 ? 1.02 : (ShirtStyle > 0.5 ? -1.0 : 0.33);
float sleeve = arm * step(t, sleeveEnd);
float shirt = saturate(torso + sleeve);
color = lerp(color, Shirt * lerp(0.88, 1.04, grain), shirt);
rough = lerp(rough, 0.9, shirt);
// Hoodie: ribbed cuffs and hem, and the hood folded behind the neck
if (ShirtStyle > 1.5)
{
    float rib = (step(0.94, t) * sleeve) + torso * (1.0 - step(105.0, P.z));
    color = lerp(color, Shirt * 0.72, saturate(rib));
    float hood = step(149.0, P.z) * step(P.z, 163.0) * step(P.y, -4.0) * step(ax, 11.0) * (1.0 - arm);
    color = lerp(color, Shirt * 0.8, hood);
    rough = lerp(rough, 0.9, hood);
}

// Eyes: white, iris in the picked colour, dark pupil
float2 eye = float2(ax - 3.3, P.z - 170.4);
float eyeDist = length(eye * float2(1.0, 1.35));
float onFace = head * front;
float sclera = onFace * (1.0 - smoothstep(1.2, 1.45, eyeDist));
float iris = onFace * (1.0 - smoothstep(0.62, 0.78, eyeDist));
float pupil = onFace * (1.0 - smoothstep(0.26, 0.34, eyeDist));
color = lerp(color, float3(0.82, 0.8, 0.76), sclera);
color = lerp(color, Eyes, iris);
color = lerp(color, float3(0.015, 0.015, 0.015), pupil);
// Brows and lips in the skin's own shades
float brow = onFace * step(171.9, P.z) * step(P.z, 172.6) * step(1.6, ax) * step(ax, 5.2);
color = lerp(color, Hair * 0.8, brow);
float lips = onFace * step(162.9, P.z) * step(P.z, 163.9) * step(ax, 2.4);
color = lerp(color, Skin * float3(0.72, 0.5, 0.48), lips);

// Face (0 calm, 1 tired, 2 cheerful, 3 mature)
if (FaceStyle > 0.5 && FaceStyle < 1.5)
{
    float bags = onFace * step(168.0, P.z) * step(P.z, 169.3) * step(1.8, ax) * step(ax, 4.9);
    color = lerp(color, Skin * float3(0.72, 0.62, 0.64), bags * 0.8);
}
else if (FaceStyle > 1.5 && FaceStyle < 2.5)
{
    float cheeks = onFace * (1.0 - smoothstep(0.8, 2.2, length(float2(ax - 5.0, P.z - 166.0))));
    color = lerp(color, Skin * float3(1.08, 0.72, 0.68), cheeks * 0.7);
}
else if (FaceStyle > 2.5)
{
    float lines = onFace * step(0.6, frac(P.z * 1.6)) * step(175.0, P.z) * step(P.z, 177.5) * step(ax, 4.0);
    color = lerp(color, Skin * 0.82, lines * 0.6);
}

// Facial hair (0 none, 1 moustache, 2 goatee, 3 full beard)
float beard = 0.0;
if (FacialHair > 0.5 && FacialHair < 1.5)
{
    beard = onFace * step(164.0, P.z) * step(P.z, 165.4) * step(ax, 3.4);
}
else if (FacialHair > 1.5 && FacialHair < 2.5)
{
    beard = onFace * step(156.5, P.z) * step(P.z, 162.6) * step(ax, 2.4);
    beard = max(beard, onFace * step(164.0, P.z) * step(P.z, 165.2) * step(ax, 3.0));
}
else if (FacialHair > 2.5)
{
    beard = head * smoothstep(0.0, 3.0, P.y) * step(P.z, 166.0 + step(4.5, ax) * 2.5) * (1.0 - lips);
}
color = lerp(color, Hair * lerp(0.75, 1.0, fine), beard);
rough = lerp(rough, 0.7, beard);

// Hair (0 bald, 1 short, 2 spiky, 3 long, 4 ponytail, 5 curly)
float hair = 0.0;
float hairline = 174.5 - 2.0 * (1.0 - front);  // lower at the back and sides
if (HairStyle > 0.5 && HairStyle < 1.5)
{
    hair = head * step(hairline, P.z);
    hair = max(hair, head * step(P.y, -4.0) * step(163.0, P.z));
}
else if (HairStyle > 1.5 && HairStyle < 2.5)
{
    float spikes = 1.5 * sin(atan2(h.y, h.x) * 9.0);
    hair = head * step(hairline + spikes, P.z);
}
else if (HairStyle > 2.5 && HairStyle < 3.5)
{
    // Long: over the top and down the back to the shoulder blades
    hair = head * step(hairline - 1.0, P.z);
    hair = max(hair, (1.0 - arm) * step(P.y, -2.5) * step(132.0 + ax * 0.6, P.z) * step(ax, 13.0) * step(P.z, 181.0));
    hair = max(hair, head * step(5.5, ax) * step(158.0, P.z) * (1.0 - front));
}
else if (HairStyle > 3.5 && HairStyle < 4.5)
{
    // Ponytail: short on top, a tail down the middle of the back
    hair = head * step(hairline, P.z);
    hair = max(hair, head * step(P.y, -4.0) * step(165.0, P.z));
    hair = max(hair, step(P.y, -6.0) * step(ax, 2.6) * step(133.0, P.z) * step(P.z, 170.0) * (1.0 - arm));
}
else if (HairStyle > 4.5)
{
    // Curly: a full head of curls, lower all round
    float curls = 0.8 * sin(P.x * 2.1) * sin(P.y * 2.3 + P.z * 1.7);
    hair = head * step(hairline - 2.5 + curls - front * 1.5, P.z);
    hair = max(hair, head * step(P.y, -2.0) * step(161.0, P.z));
}
// Mature: grey at the temples
float3 hairColor = Hair;
if (FaceStyle > 2.5)
{
    hairColor = lerp(Hair, float3(0.55, 0.55, 0.53), step(5.5, ax) * 0.6);
}
float strands = lerp(0.78, 1.08, frac(sin(dot(floor(float2(atan2(h.y, h.x) * 30.0, P.z * 0.5)), float2(7.13, 3.71))) * 9123.17));
color = lerp(color, hairColor * strands, hair);
rough = lerp(rough, 0.55, hair);

RoughOut = rough;
return color;
