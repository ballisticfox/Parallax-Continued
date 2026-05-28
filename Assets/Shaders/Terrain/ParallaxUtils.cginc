#if !defined (SCALED)
    #include "../Includes/BiplanarFunctions.cginc"
#else
    #if !defined (BAKED)
        #include "../Includes/ScaledBiplanarFunctions.cginc"
    #endif
#endif

//
//  Cube-face UV helpers (shared between fragment-shader colormap sampling and vertex-shader heightmap displacement)
//  texcoord2 packing (written by PQSMod_PlanetUV into mesh.uv3):
//      texcoord2.x = faceIndex + faceU  (integer part = cube face 0..5, fractional part = u in [0,1))
//      texcoord2.y = faceV              (in [0,1])
//
void UnpackFaceUV(float2 packed, out float faceIndex, out float2 faceUV)
{
    faceIndex = floor(packed.x);
    faceUV = float2(frac(packed.x), packed.y);
}

float2 CorrectFaceUV(float2 faceUV, float faceIndex)
{
    if (faceIndex == 0)      return float2(faceUV.y, 1.0 - faceUV.x);  // XP: 90° CCW
    else if (faceIndex == 1) return float2(1.0 - faceUV.y, faceUV.x);  // XN: 90° CW
    else if (faceIndex == 2) return 1.0 - faceUV;                      // YP: 180°
    else if (faceIndex == 3) return 1.0 - faceUV;                      // YN: 180°
    else if (faceIndex == 4) return 1.0 - faceUV;                      // ZP: 180°
    else                     return faceUV;                            // ZN: identity
}

//
//  Mitchell-Netravali bicubic (B=C=1/3) — matches the CPU-side heightmap sampler so PQS-built
//  geometry and GPU-displaced geometry agree at the blend boundary. Weights sum to 1 by construction.
//
float MitchellNetravali1D(float x)
{
    float ax = abs(x);
    float ax2 = ax * ax;
    float ax3 = ax2 * ax;
    // B = C = 1/3 → kernel coefficients simplify as below.
    if (ax < 1.0)
    {
        return (7.0 * ax3 - 12.0 * ax2 + 16.0 / 3.0) / 6.0;
    }
    else if (ax < 2.0)
    {
        return (-(7.0 / 3.0) * ax3 + 12.0 * ax2 - 20.0 * ax + 32.0 / 3.0) / 6.0;
    }
    return 0.0;
}

// Heightmap-specific helpers live behind the SCALED guard because the heightmap VT uniforms,
// _HeightOffset, _HeightScale, _NearFieldEnd and _BlendWidth are only declared in the terrain-side
// ParallaxVariables.cginc — the scaled shader doesn't use them.
#if !defined (SCALED)

// Virtual texture pyramid sampler. Walks the page table starting at startLevel, falls back to coarser
// levels when a tile isn't loaded, and remaps the within-tile UV into the cache atlas accounting for
// the per-tile border. Inputs are the corrected face UV (post-CorrectFaceUV) and the face index.
// Uses tex2Dlod for both the page table (point sampling — exact texel lookup) and the atlas (LOD 0,
// bilinear filter from the texture's sampler state) so this is safe to call from any shader stage.
float4 SampleVTPyramid(
    sampler2D atlas, sampler2D pageTable,
    float atlasSize, float tileSize, float tileBorder, float maxLevelF,
    float2 faceUV, float faceIndex, int startLevel)
{
    int maxLevel = (int)maxLevelF;
    int faceStride = 1 << maxLevel;
    float slotSize = tileSize + 2.0 * tileBorder;
    float invAtlas = 1.0 / atlasSize;
    int faceIdx = (int)faceIndex;

    int desiredLevel = clamp(startLevel, 0, maxLevel);
    float2 pageDim = float2(6.0 * faceStride, (1 << (maxLevel + 1)) - 1);

    // Walk from desiredLevel up to level 0 looking for a loaded tile.
    [unroll]
    for (int attempt = 0; attempt <= 16; attempt++)
    {
        int level = desiredLevel - attempt;
        if (level < 0) break;

        int gridSize = 1 << level;
        int2 tileCoord = clamp((int2)(faceUV * gridSize), int2(0, 0), int2(gridSize - 1, gridSize - 1));

        int yStart = (1 << level) - 1;
        int2 pageCoord = int2(faceIdx * faceStride + tileCoord.x, yStart + tileCoord.y);
        // Page table: point-sampled. +0.5 hits the texel center; the C# loader sets filter=Point/wrap=Clamp.
        float2 pageUV = (float2(pageCoord) + 0.5) / pageDim;
        float4 page = tex2Dlod(pageTable, float4(pageUV, 0, 0));

        if (page.a > 0.5)
        {
            // Slot index encoded as bytes (0..255) in R/G of the page texel.
            float2 slot = floor(page.rg * 255.0 + 0.5);

            // UV within this tile → atlas UV (skip the border padding around each slot).
            float2 withinTileUV = frac(faceUV * gridSize);
            float2 atlasPx = slot * slotSize + tileBorder + withinTileUV * tileSize;
            return tex2Dlod(atlas, float4(atlasPx * invAtlas, 0, 0));
        }
    }

    // No tile loaded at any level on this face — debug magenta so misses are obvious.
    return float4(1.0, 0.0, 1.0, 0.0);
}

//
//  GPU Heightmap Displacement
//  Overrides the vertex radial position with a value sampled from the heightmap VT pyramid when the
//  vertex is far from camera. Blend strip controlled by _NearFieldEnd / _BlendWidth keeps PQS geometry
//  near the craft (for collision accuracy). Called in Domain_Shader (post-tessellation) so tessellated
//  vertices get freshly sampled heights instead of linear interpolation of already-displaced corners —
//  important for capturing heightmap detail on coarse far-field PQS quads when _MaxTessellationRange is
//  stretched out.
//
float3 ApplyGPUHeightmapDisplacement(float3 worldPos, float2 texcoord2)
{
    float distToCamera = length(worldPos - _WorldSpaceCameraPos);
    float blendFactor = saturate((distToCamera - _NearFieldEnd) / _BlendWidth);

    // Skip the sample/transform entirely when no GPU contribution is requested.
    if (blendFactor <= 0.0)
        return worldPos;

    float faceIndex;
    float2 faceUV;
    UnpackFaceUV(texcoord2, faceIndex, faceUV);
    faceUV = CorrectFaceUV(faceUV, faceIndex);

    // Vertex stage can't use ddx/ddy, so we ask for the deepest level we have and let the page-table
    // walk-up settle on whatever coarser tile is actually loaded for this (face, x, y).
    float sampledHeight = SampleVTPyramid(
        _HeightTileAtlas, _HeightPageTable,
        _HeightTileAtlasSize, _HeightTileSize, _HeightTileBorder, _HeightMaxTileLevel,
        faceUV, faceIndex, (int)_HeightMaxTileLevel).r;

    float targetRadius = _PlanetRadius + _HeightOffset + sampledHeight * _HeightScale;
    float3 dirFromCenter = normalize(worldPos - _PlanetOrigin);
    float3 correctedWorldPos = _PlanetOrigin + dirFromCenter * targetRadius;

    return lerp(worldPos, correctedWorldPos, blendFactor);
}

// Fragment-stage colormap sample. Derives the desired pyramid level from screen-space UV derivatives so
// distant geometry pulls coarse tiles and close-up geometry pulls fine ones. Inputs are the corrected
// face UV (post-CorrectFaceUV) and the face index.
float3 SampleColormapVT(float2 faceUV, float faceIndex)
{
    float screenPixelUV = max(length(ddx(faceUV)), length(ddy(faceUV)));
    int desiredLevel = (int)floor(-log2(max(screenPixelUV * _ColorTileSize, 1e-12)));

    return SampleVTPyramid(
        _ColorTileAtlas, _ColorPageTable,
        _ColorTileAtlasSize, _ColorTileSize, _ColorTileBorder, _ColorMaxTileLevel,
        faceUV, faceIndex, desiredLevel).rgb;
}

// Fragment-stage tangent-space normal sample, transformed to world space via a sphere-aligned TBN.
//
// TBN convention: N = radial planet direction (worldPos - _PlanetOrigin); T = east (perpendicular to the
// world up axis, tangent to the sphere); B = N x T (locally north). Normal maps must be baked in this
// same convention — R perturbs eastward, G perturbs northward. The 0.999 pole threshold swaps T to a
// safe orthogonal when worldUp is nearly parallel to N.
//
// Returns true and writes the VT-derived world-space normal to `vtWorldNormal` when a tile is resident.
// Returns false on either "normal VT not bound for this body" or "no tile loaded for this pixel at any
// pyramid level" — caller should skip the VT blend in that case.
//
// Designed to be called ONCE per fragment; the result feeds both the early biplanar-input blend and the
// late finalNormal blend so we don't pay for the page-table walk + atlas sample twice.
bool TrySampleVTWorldNormal(float2 faceUV, float faceIndex, float3 worldPos, out float3 vtWorldNormal)
{
    vtWorldNormal = float3(0.0, 1.0, 0.0); // unused on miss

    if (_HasNormalVT < 0.5) return false;

    float screenPixelUV = max(length(ddx(faceUV)), length(ddy(faceUV)));
    int desiredLevel = (int)floor(-log2(max(screenPixelUV * _NormalTileSize, 1e-12)));
    float4 packed = SampleVTPyramid(
        _NormalTileAtlas, _NormalPageTable,
        _NormalTileAtlasSize, _NormalTileSize, _NormalTileBorder, _NormalMaxTileLevel,
        faceUV, faceIndex, desiredLevel);

    // SampleVTPyramid returns alpha=0 (debug magenta) when no tile is loaded at any level — fall back.
    if (packed.a < 0.5) return false;

    float3 tangentN = UnpackNormal(packed);

    float3 N = normalize(worldPos - _PlanetOrigin);
    float3 worldUp = float3(0.0, 1.0, 0.0);
    float3 T = (abs(dot(worldUp, N)) > 0.999) ? float3(1.0, 0.0, 0.0) : normalize(cross(worldUp, N));
    float3 B = cross(N, T);

    vtWorldNormal = normalize(tangentN.x * T + tangentN.y * B + tangentN.z * N);
    return true;
}

// Conditional VT-normal blend. Lerps baseNormal toward vtWorldNormal by blendFactor, but only when
// hasVTNormal is true and blendFactor > 0. Returns baseNormal unchanged otherwise — preserves the
// original (no-VT) shading path exactly. Same blend factor as ApplyGPUHeightmapDisplacement so the
// near/far hand-off stays in lockstep with the GPU displacement.
float3 BlendNormalWithVT(float3 baseNormal, float3 vtWorldNormal, bool hasVTNormal, float blendFactor)
{
    if (!hasVTNormal || blendFactor <= 0.0) return baseNormal;
    return normalize(lerp(baseNormal, vtWorldNormal, blendFactor));
}

// Walks the color page table and returns the highest level that is actually resident,
// starting from startLevel and falling back toward 0. Returns -1 if nothing is loaded.
// Used by PARALLAX_VT_DEBUG to colour terrain by streaming quality without reading the atlas.
int GetVTResidentLevel(sampler2D pageTable, float maxLevelF, float2 faceUV, float faceIndex, int startLevel)
{
    int maxLevel     = (int)maxLevelF;
    int faceStride   = 1 << maxLevel;
    int faceIdx      = (int)faceIndex;
    int desiredLevel = clamp(startLevel, 0, maxLevel);
    float2 pageDim   = float2(6.0 * faceStride, (float)((1 << (maxLevel + 1)) - 1));

    [unroll]
    for (int attempt = 0; attempt <= 16; attempt++)
    {
        int level = desiredLevel - attempt;
        if (level < 0) break;

        int gridSize    = 1 << level;
        int2 tileCoord  = clamp((int2)(faceUV * gridSize), int2(0, 0), int2(gridSize - 1, gridSize - 1));
        int  yStart     = (1 << level) - 1;
        int2 pageCoord  = int2(faceIdx * faceStride + tileCoord.x, yStart + tileCoord.y);
        float2 pageUV   = (float2(pageCoord) + 0.5) / pageDim;
        float4 page     = tex2Dlod(pageTable, float4(pageUV, 0, 0));
        if (page.a > 0.5) return level;
    }
    return -1;
}

// Maps a resident VT level to a debug colour. Warm = coarse, cool = fine.
// red=0, orange=1, yellow=2, lime=3, green=4, cyan=5, blue=6+, magenta=missing.
float3 VTLevelColor(int level)
{
    if (level == 0) return float3(1.0, 0.0, 0.0);  // red
    if (level == 1) return float3(1.0, 0.5, 0.0);  // orange
    if (level == 2) return float3(1.0, 1.0, 0.0);  // yellow
    if (level == 3) return float3(0.5, 1.0, 0.0);  // lime
    if (level == 4) return float3(0.0, 1.0, 0.0);  // green
    if (level == 5) return float3(0.0, 1.0, 1.0);  // cyan
    if (level >= 6) return float3(0.0, 0.4, 1.0);  // blue
    return float3(1.0, 0.0, 1.0);                  // magenta — no tile loaded
}

#endif // !SCALED

//
//  Tessellation Functions
//  Most as or adapted from, and credit to, https://nedmakesgames.medium.com/mastering-tessellation-shaders-and-their-many-uses-in-unity-9caeb760150e
//

// Clip space range
#if defined (SHADER_API_GLCORE)
#define FAR_CLIP_VALUE 0
#else
#define FAR_CLIP_VALUE 1
#endif

// Clip tolerances
#define BACKFACE_CLIP_TOLERANCE -0.25
#define FRUSTUM_CLIP_TOLERANCE   0.75

#define BARYCENTRIC_INTERPOLATE(fieldName) \
		patch[0].fieldName * barycentricCoordinates.x + \
		patch[1].fieldName * barycentricCoordinates.y + \
		patch[2].fieldName * barycentricCoordinates.z

#define MAX_ADVANCED_BLENDING_SMOOTHNESS 1.0
#define MIN_ADVANCED_BLENDING_SMOOTHNESS 0.15

// True if point is outside bounds defined by lower and higher
bool IsOutOfBounds(float3 p, float3 lower, float3 higher)
{
    return p.x < lower.x || p.x > higher.x || p.y < lower.y || p.y > higher.y || p.z < lower.z || p.z > higher.z;
}

// True if vertex is outside of camera frustum
// Inputs a clip space position
bool IsPointOutOfFrustum(float4 pos)
{
    float3 culling = pos.xyz;
    float w = pos.w + FRUSTUM_CLIP_TOLERANCE;
    // UNITY_RAW_FAR_CLIP_VALUE is either 0 or 1, depending on graphics API
    // Most use 0, however OpenGL uses 1
    float3 lowerBounds = float3(-w, -w, -w * FAR_CLIP_VALUE);
    float3 higherBounds = float3(w, w, w);
    return IsOutOfBounds(culling, lowerBounds, higherBounds);
}

// Does the triangle normal face the camera position?
bool ShouldBackFaceCull(float3 nrm1, float3 nrm2, float3 nrm3, float3 w1, float3 w2, float3 w3)
{
    float3 faceNormal = (nrm1 + nrm2 + nrm3) * 0.333f;
    float3 faceWorldPos = (w1 + w2 + w2) * 0.333f;
    
    // Can't backface cull the shadow caster pass
#if !defined (PARALLAX_SHADOW_CASTER_PASS)
    return dot(faceNormal, normalize(_WorldSpaceCameraPos - faceWorldPos)) < BACKFACE_CLIP_TOLERANCE;
#else
    return 0;
#endif
}

// True if should be clipped by frustum or winding cull
// Inputs are clip space positions, world space normals, world space positions
bool ShouldClipPatch(float4 cp0, float4 cp1, float4 cp2, float3 n0, float3 n1, float3 n2, float3 wp0, float3 wp1, float3 wp2)
{
    bool allOutside = IsPointOutOfFrustum(cp0) && IsPointOutOfFrustum(cp1) && IsPointOutOfFrustum(cp2);
    return allOutside || ShouldBackFaceCull(n0, n1, n2, wp0, wp1, wp2);
}

// Remap
float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
{
    value = saturate((value - fromMin) / (fromMax - fromMin));
    return lerp(toMin, toMax, value);
}

// Calculate factor from edge length
// Vector inputs are world space
float EdgeTessellationFactor(float scale, float bias, float3 p0World, float4 p0Clip, float3 p1World, float4 p1Clip)
{
    float screenSpaceDistance = distance(p0Clip.xyz / p0Clip.w, p1Clip.xyz / p1Clip.w);
    float factor = screenSpaceDistance * (float) _ScreenParams.y / scale;
    float worldSpaceDistance = distance(_WorldSpaceCameraPos, (p0World + p1World) * 0.5);
    float distanceScalingFactor = 1 - saturate(worldSpaceDistance / _MaxTessellationRange);

    return max(1, factor * distanceScalingFactor);
}

//
// Smoothing Functions
//

// Calculate Phong projection offset
float3 PhongProjectedPosition(float3 flatPositionWS, float3 cornerPositionWS, float3 normalWS)
{
    return flatPositionWS - dot(flatPositionWS - cornerPositionWS, normalWS) * normalWS;
}

// Apply Phong smoothing
float3 CalculatePhongPosition(float3 bary, float3 p0PositionWS, float3 p0NormalWS, float3 p1PositionWS, float3 p1NormalWS, float3 p2PositionWS, float3 p2NormalWS)
{
    float3 flatPositionWS = bary.x * p0PositionWS + bary.y * p1PositionWS + bary.z * p2PositionWS;
    float3 smoothedPositionWS =
        bary.x * PhongProjectedPosition(flatPositionWS, p0PositionWS, p0NormalWS) +
        bary.y * PhongProjectedPosition(flatPositionWS, p1PositionWS, p1NormalWS) +
        bary.z * PhongProjectedPosition(flatPositionWS, p2PositionWS, p2NormalWS);
    return lerp(flatPositionWS, smoothedPositionWS, 0.333);
}

#define CALCULATE_VERTEX_DISPLACEMENT(o, landMask, displacementTex)                                                                                                 \
    float displacementRange = 1 - min(1, terrainDistance / max(_TileDisplacementRange, 1.0));                                                                      \
    float displacement = BLEND_CHANNELS_IN_TEX(landMask, displacementTex);                                                                                          \
    float displacementOffset = _DisplacementOffset;                                                                                                                 \
    displacement = lerp(displacement, displacementTex.a, landMask.b);                                                                                               \
    float displacementAndOffset = displacement + displacementOffset;                                                                                                \
    displacementAndOffset = lerp(displacementAndOffset * exponent * 2, displacementAndOffset * exponent * 4, texLevelBlend);                                        \
    float3 displacedWorldPos = o.worldPos + displacementAndOffset * o.worldNormal * _DisplacementScale * displacementRange;

//
//  Ingame Calcs
//

// Calculate Slope
// Vaue of 0 means flat land, 1 means slope
float GetSlope(float3 worldPos, float3 worldNormal)
{
    // We abs in the very strange case of overhangs
    float slope = abs(dot(normalize(worldPos - _PlanetOrigin), worldNormal));
    slope = pow(slope, _SteepPower);
    slope = saturate((slope - _SteepMidpoint) * _SteepContrast + _SteepMidpoint);
    return 1 - slope;
}

// Get blend as a percentage between two altitudes
float GetPercentageAltitudeBetween(float altitude, float lowerLimit, float upperLimit)
{
    float percentage = (altitude - lowerLimit) / (upperLimit - lowerLimit);
    return saturate(percentage);
}

// Get Blend Factors
float4 GetAltitudeMask(float3 worldPos, float3 worldNormal, float altitude, float midpoint)
{
    float lowMidBlend = GetPercentageAltitudeBetween(altitude, _LowMidBlendStart, _LowMidBlendEnd);
    float midHighBlend = GetPercentageAltitudeBetween(altitude, _MidHighBlendStart, _MidHighBlendEnd);
    float slope = GetSlope(worldPos, worldNormal);

    // Land mask - Low-mid blend in red, mid-high blend in green, steep in blue
    return float4(lowMidBlend, midHighBlend, slope, midpoint);
}

// Get land mask macro
float4 GetLandMask(float3 worldPos, float3 worldNormal)
{
    float altitude = length(worldPos - _PlanetOrigin) - _PlanetRadius;
    float midpoint = altitude / (_MidHighBlendStart + _LowMidBlendEnd);
    return GetAltitudeMask(worldPos, worldNormal, altitude, midpoint);
}
// Displacement blending
float GetDisplacementLerpFactor(float heightLerp, float displacement1, float displacement2, float logDistance)
{
    float _Smoothness = lerp(MIN_ADVANCED_BLENDING_SMOOTHNESS, MAX_ADVANCED_BLENDING_SMOOTHNESS, saturate(logDistance * 0.15 - 0.5));
    
    // heightLerp * heightLerp not needed but balances out the blend (makes it more central)
    displacement2 += (heightLerp);
    displacement1 *= (1 - heightLerp);

    displacement2 = saturate(displacement2);
    displacement1 = saturate(displacement1);

    float diff = (displacement2 - displacement1) * heightLerp;

     
    diff /= _Smoothness;
    diff = saturate(diff);
    return diff;
}

#if defined (ADVANCED_BLENDING)
    #define CALCULATE_ADVANCED_BLENDING_FACTORS(landMask, displacement)                                                             \
    float lowMidDisplacementFactor = GetDisplacementLerpFactor(landMask.r, displacement.r, displacement.g, logDistance);            \
    float midHighDisplacementFactor = GetDisplacementLerpFactor(landMask.g, displacement.g, displacement.b, logDistance);           \
    float blendedDisplacements = BLEND_CHANNELS_IN_TEX(landMask, displacement);                                                     \
    float displacementSteepBlendFactor = GetDisplacementLerpFactor(landMask.b, blendedDisplacements, displacement.a, logDistance);  \
                                                                                                                                    \
    landMask.r = lowMidDisplacementFactor;                                                                                          \
    landMask.g = midHighDisplacementFactor;                                                                                         \
    landMask.b = displacementSteepBlendFactor;                                                                                      
#else
    #define CALCULATE_ADVANCED_BLENDING_FACTORS(landMask, displacement)
#endif


// Blend textures based on landmask
#define BLEND_ALL_TEXTURES(landMask, lowTex, midTex, highTex)                                                                    \
        lerp(lowTex, midTex, landMask.r) * (landMask.a < 0.5) + lerp(midTex, highTex, landMask.g) * (landMask.a >= 0.5);

#define BLEND_TWO_TEXTURES(blend, tex1, tex2)                                                                                    \
        lerp(tex1, tex2, blend);

//
// Texture Set Calcs
// When using lighter shader variations these aren't included
//

#if defined (INFLUENCE_MAPPING)
    #define BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)                                                                                                        \
        fixed4 diffuseName = SampleBiplanarTexture(diffuseSampler, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);                                                       \
        NORMAL_FLOAT normalName = SampleBiplanarNormal(normalSampler, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);                                                          \
        float diffuseName##Lum = (diffuseName.r * 0.21f + diffuseName.g * 0.72f + diffuseName.b * 0.07f) + 0.5f;                                                                                \
        diffuseName.rgb = lerp(vertexColor * diffuseName##Lum, diffuseName.rgb, diffuseName##InfluenceValue);
#else
    #define BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)                                                                                                        \
        fixed4 diffuseName = SampleBiplanarTexture(diffuseSampler, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);                                                       \
        NORMAL_FLOAT normalName = SampleBiplanarNormal(normalSampler, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);
#endif

#define BIPLANAR_TEXTURE(texName, texSampler)   fixed4 texName = SampleBiplanarTexture(texSampler, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);

#define UNUSED_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)  \
    fixed4 diffuseName = 0;                                                         \
    float3 normalName = 0;

#define BLEND_DIFFUSE_INPUT_PARAMS landMask, lowDiffuse, midDiffuse, highDiffuse

// If we are sampling a low texture, else declare nothing
#if defined (PARALLAX_SINGLE_LOW) || defined (PARALLAX_DOUBLE_LOWMID) || defined (PARALLAX_FULL)
    #define DECLARE_LOW_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#else
    #define DECLARE_LOW_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) UNUSED_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#endif

// If we are sampling a mid texture, else declare nothing
#if defined (PARALLAX_SINGLE_MID) || defined (PARALLAX_DOUBLE_LOWMID) || defined (PARALLAX_DOUBLE_MIDHIGH) || defined (PARALLAX_FULL)
    #define DECLARE_MID_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#else
    #define DECLARE_MID_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) UNUSED_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#endif

// If we are sampling a high texture, else declare nothing
#if defined (PARALLAX_SINGLE_HIGH) || defined (PARALLAX_DOUBLE_MIDHIGH) || defined (PARALLAX_FULL)
    #define DECLARE_HIGH_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#else
    #define DECLARE_HIGH_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) UNUSED_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)
#endif

#if defined (ADVANCED_BLENDING)
    #define DECLARE_DISPLACEMENT_TEXTURE(displacementTexName, displacementSampler) BIPLANAR_TEXTURE(displacementTexName, displacementSampler)
#else
    #define DECLARE_DISPLACEMENT_TEXTURE(displacementTexName, displacementSampler)
#endif

#if defined (AMBIENT_OCCLUSION)
    #define DECLARE_AMBIENT_OCCLUSION_TEXTURE(occlusionTexName, occlusionSampler) BIPLANAR_TEXTURE(occlusionTexName, occlusionSampler)
#else
    #define DECLARE_AMBIENT_OCCLUSION_TEXTURE(occlusionTexName, occlusionSampler)
#endif

// We are always sampling the slope texture
#define DECLARE_STEEP_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler) BIPLANAR_TEXTURE_SET(diffuseName, normalName, diffuseSampler, normalSampler)

// Get global influence texture and values, otherwise declare nothing
#if defined (INFLUENCE_MAPPING)
    #define DECLARE_INFLUENCE_TEXTURE float4 globalInfluence = SampleBiplanarTexture(_InfluenceMap, params, worldUVsLevel0, worldUVsLevel1, i.worldNormal, texLevelBlend);
    #define DECLARE_INFLUENCE_VALUES                                \
        float lowDiffuseInfluenceValue = globalInfluence.r;         \
        float midDiffuseInfluenceValue = globalInfluence.g;         \
        float highDiffuseInfluenceValue = globalInfluence.b;        \
        float steepDiffuseInfluenceValue = globalInfluence.a;
#else
    #define DECLARE_INFLUENCE_TEXTURE
    #define DECLARE_INFLUENCE_VALUES
#endif

//
//  Texture Blend Calcs
//  When some textures aren't being blended, their values are unused and anything is optimized out at compile time
//

#if defined (PARALLAX_SINGLE_LOW)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          lowTex
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       tex.r

#elif defined (PARALLAX_SINGLE_MID)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          midTex
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       tex.g

#elif defined (PARALLAX_SINGLE_HIGH)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          highTex
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       tex.b

#elif defined (PARALLAX_DOUBLE_LOWMID)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          BLEND_TWO_TEXTURES(landMask.r, lowTex, midTex)
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       BLEND_TWO_TEXTURES(landMask.r, tex.r, tex.g)

#elif defined (PARALLAX_DOUBLE_MIDHIGH)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          BLEND_TWO_TEXTURES(landMask.g, midTex, highTex)
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       BLEND_TWO_TEXTURES(landMask.g, tex.g, tex.b)

#elif defined (PARALLAX_FULL)
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          BLEND_ALL_TEXTURES(landMask, lowTex, midTex, highTex)
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       BLEND_ALL_TEXTURES(landMask, tex.r, tex.g, tex.b)
#else
    // No keywords defined, fallback to low - But because of unused texture set, this will be black
    #define BLEND_TEXTURES(landMask, lowTex, midTex, highTex)          lowTex
    #define BLEND_CHANNELS_IN_TEX(landMask, tex)                       tex.r
#endif

#if defined (AMBIENT_OCCLUSION)
    #define BLEND_OCCLUSION(landMask, occlusion)                                    \
        fixed4 altitudeOcclusion = BLEND_CHANNELS_IN_TEX(landMask, occlusion);      \
        fixed4 finalOcclusion = lerp(altitudeOcclusion, occlusion.a, landMask.b);
#else
    #define BLEND_OCCLUSION(landMask, occlusion)
#endif